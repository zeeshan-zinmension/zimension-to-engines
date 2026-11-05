using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Zimension.Core;

namespace Zimension.Editor
{
    /// <summary>
    /// Fetches a Zimension export for import: a share link or code (resolved through the backend's
    /// /world/unity-export/link/:code contract, see docs/BACKEND_UNITY_EXPORT.md), a direct .zip URL,
    /// or a local zip/folder.
    ///
    /// Robustness:
    /// - downloads into a per-link cache (Library/ZimensionDownloads, never Assets or version control),
    ///   writing to a .part file renamed into place only after verification;
    /// - resumes interrupted downloads with Range + If-Range (ETag), restarting cleanly when the
    ///   server returns the whole file or the version changed;
    /// - retries network failures, timeouts, 408/429 and 5xx with backoff, never 4xx;
    /// - aborts a stalled transfer after <see cref="Options.StallTimeout"/>;
    /// - verifies size and SHA-256 when the link reports them, and that the file is a zip containing
    ///   masterData.json;
    /// - reuses an already downloaded, verified version without network transfer;
    /// - maps HTTP errors to actionable messages and never logs link codes or signed URLs.
    /// Engine-free (System.Net.Http): runs on a worker thread; the caller imports on the main thread.
    /// </summary>
    public sealed class ExportDownloader
    {
        public const string DefaultApiBase = "https://api.zimension3d.com/api/v1";

        public sealed class Options
        {
            public string ApiBase = DefaultApiBase;
            public string CacheRoot;
            public TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
            public TimeSpan ResolveTimeout = TimeSpan.FromSeconds(30);
            public int MaxAttempts = 5;
            public long MaxBytes = 4L * 1024 * 1024 * 1024;
            /// <summary>Backoff before retry n (1-based); tests shorten it.</summary>
            public Func<int, TimeSpan> Backoff = attempt => TimeSpan.FromSeconds(Math.Min(16, Math.Pow(2, attempt - 1)));
            public HttpMessageHandler Handler; // tests
        }

        public struct Progress
        {
            public string Stage;
            public long Received, Total; // Total -1 when unknown
            public double BytesPerSecond;
            public float Fraction => Total > 0 ? (float)Math.Min(1.0, (double)Received / Total) : -1f;
        }

        public sealed class Result
        {
            /// <summary>Local zip (or folder for a local folder input) to import.</summary>
            public string Path;
            public string WorldName = "", WorldId = "", Version = "", Sha256 = "";
            public long Size;
            /// <summary>The input as it should be remembered for Pull latest ("" for local files).</summary>
            public string SourceLink = "";
            public bool FromCache;
            public bool IsLocal;
        }

        public sealed class DownloadException : Exception
        {
            public readonly bool Retryable;
            public readonly int Status;
            public DownloadException(string message, bool retryable = false, int status = 0, Exception inner = null) : base(message, inner)
            {
                Retryable = retryable; Status = status;
            }
        }

        /// <summary>The file on the server is not the version the link reported: re-resolve.</summary>
        sealed class VersionChangedException : Exception
        {
            /// <summary>The version and checksum the link reported for the attempt that failed.</summary>
            public readonly string Resolved;
            public VersionChangedException(string resolved) : base("The export changed on the server while downloading.") { Resolved = resolved; }
        }

        enum Kind { Local, Code, ResolveUrl, DirectUrl }

        static readonly Regex CodePattern = new Regex(@"^[A-Za-z0-9_-]{8,64}$", RegexOptions.CultureInvariant);
        static readonly Regex CodeInUrl = new Regex(@"/unity-export/(?:link/)?([A-Za-z0-9_-]{8,64})(?:[/?#]|$)", RegexOptions.CultureInvariant);

        readonly Options m_Options;

        public ExportDownloader(Options options)
        {
            m_Options = options ?? new Options();
            if (string.IsNullOrEmpty(m_Options.CacheRoot)) throw new ArgumentException("CacheRoot is required");
        }

        /// <summary>How an input is interpreted (for UI hints and tests).</summary>
        public static string Describe(string input)
        {
            switch (Classify(input, out _))
            {
                case Kind.Local: return "local file";
                case Kind.Code: return "Zimension link code";
                case Kind.ResolveUrl: return "Zimension link";
                default: return "download URL";
            }
        }

        static Kind Classify(string raw, out string value)
        {
            value = (raw ?? "").Trim().Trim('"');
            if (value.Length == 0) throw new DownloadException("Paste a Zimension Unity export link, a link code, or a .zip URL.");
            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(value) || Directory.Exists(value)) return Kind.Local;
                if (CodePattern.IsMatch(value)) return Kind.Code;
                throw new DownloadException("That is not a link, a link code, or an existing file: " + value);
            }
            var match = CodeInUrl.Match(value);
            if (match.Success)
            {
                // The API resolve URL itself, or a web-app share URL carrying the code (e.g. #/unity-export/<code>).
                if (value.IndexOf("/unity-export/link/", StringComparison.OrdinalIgnoreCase) >= 0 && value.IndexOf("/download", StringComparison.OrdinalIgnoreCase) < 0)
                    return Kind.ResolveUrl;
                if (value.IndexOf("/download", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    value = match.Groups[1].Value;
                    return Kind.Code;
                }
            }
            return Kind.DirectUrl;
        }

        HttpClient Client(TimeSpan timeout)
        {
            var handler = m_Options.Handler ?? new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None };
            var client = new HttpClient(handler, disposeHandler: m_Options.Handler == null) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ZimensionUnityImporter/" + ZimensionImporterVersion.Value);
            return client;
        }

        public Task<Result> FetchAsync(string input, IProgress<Progress> progress, CancellationToken cancel) =>
            Task.Run(() => Fetch(input, progress, cancel), cancel);

        async Task<Result> Fetch(string input, IProgress<Progress> progress, CancellationToken cancel)
        {
            var kind = Classify(input, out var value);
            if (kind == Kind.Local)
            {
                // Validate now so a wrong file fails before the import starts.
                using (BundleSource.Open(value)) { }
                return new Result { Path = value, IsLocal = true };
            }
            string failed = null;
            for (int pass = 0; ; pass++)
            {
                try { return await FetchOnce(kind, value, progress, cancel, failed).ConfigureAwait(false); }
                catch (VersionChangedException e) when (pass < 2 && kind != Kind.DirectUrl && e.Resolved != failed)
                {
                    failed = e.Resolved;
                    progress?.Report(new Progress { Stage = "The export was updated while downloading; fetching the new version", Total = -1 });
                }
                catch (VersionChangedException e)
                {
                    throw new DownloadException("The export keeps changing on the server. Try again in a moment.", false, 0, e);
                }
            }
        }

        async Task<Result> FetchOnce(Kind kind, string value, IProgress<Progress> progress, CancellationToken cancel, string failedBefore)
        {
            var result = new Result();
            string downloadUrl;
            Uri resolveUri = null;
            if (kind == Kind.DirectUrl)
            {
                downloadUrl = value;
                result.SourceLink = value;
            }
            else
            {
                resolveUri = kind == Kind.Code
                    ? new Uri(m_Options.ApiBase.TrimEnd('/') + "/world/unity-export/link/" + value)
                    : new Uri(value);
                result.SourceLink = resolveUri.ToString(); // remembered as the full link, so Pull latest does not depend on settings
                progress?.Report(new Progress { Stage = "Resolving link", Total = -1 });
                var info = await WithRetries(() => Resolve(resolveUri, cancel), progress, "Resolving link", cancel).ConfigureAwait(false);
                downloadUrl = info.Str("downloadUrl");
                if (string.IsNullOrEmpty(downloadUrl)) throw new DownloadException("The link did not return a download URL.");
                if (!Uri.IsWellFormedUriString(downloadUrl, UriKind.Absolute)) downloadUrl = new Uri(resolveUri, downloadUrl).ToString();
                result.WorldName = info.Str("worldName");
                result.WorldId = info.Str("worldId");
                result.Version = info.Str("version");
                result.Sha256 = info.Str("sha256").ToLowerInvariant();
                result.Size = (long)info.Num("size", 0);
                if (result.Size > m_Options.MaxBytes) throw new DownloadException($"The export is {result.Size / (1024 * 1024)} MB, over the {m_Options.MaxBytes / (1024 * 1024)} MB limit.");
                if (failedBefore != null && failedBefore == ResolvedKey(result))
                    throw new DownloadException("The download is corrupted (checksum mismatch): the server's file does not match the export it reports. Export the project again.");
            }
            var folder = System.IO.Path.Combine(m_Options.CacheRoot, CacheKey(result.SourceLink));
            Directory.CreateDirectory(folder);
            var final = System.IO.Path.Combine(folder, "export.zip");
            var part = final + ".part";
            var metaPath = System.IO.Path.Combine(folder, "download.json");
            // A verified copy of this exact version is reused.
            if (File.Exists(final) && result.Sha256.Length == 64 && Sha256File(final) == result.Sha256)
            {
                result.Path = final;
                result.FromCache = true;
                result.Size = new FileInfo(final).Length;
                progress?.Report(new Progress { Stage = "Already downloaded", Received = result.Size, Total = result.Size });
                return result;
            }
            var meta = ReadMeta(metaPath);
            // A partial file from a different version cannot be resumed.
            if (File.Exists(part) && (meta.Str("version") != result.Version || meta.Str("sha256") != result.Sha256)) File.Delete(part);
            await WithRetries(async () =>
            {
                await Download(downloadUrl, part, metaPath, result, progress, cancel).ConfigureAwait(false);
                return true;
            }, progress, "Downloading", cancel).ConfigureAwait(false);
            progress?.Report(new Progress { Stage = "Verifying", Received = new FileInfo(part).Length, Total = new FileInfo(part).Length });
            Verify(part, result, kind != Kind.DirectUrl);
            if (File.Exists(final)) File.Delete(final);
            File.Move(part, final);
            result.Path = final;
            result.Size = new FileInfo(final).Length;
            if (result.Sha256.Length != 64) result.Sha256 = Sha256File(final);
            WriteMeta(metaPath, result, "");
            return result;
        }

        async Task<T> WithRetries<T>(Func<Task<T>> action, IProgress<Progress> progress, string stage, CancellationToken cancel)
        {
            for (int attempt = 1; ; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                try { return await action().ConfigureAwait(false); }
                catch (DownloadException e) when (e.Retryable && attempt < m_Options.MaxAttempts)
                {
                    progress?.Report(new Progress { Stage = $"{stage}: {e.Message} Retrying ({attempt}/{m_Options.MaxAttempts - 1})...", Total = -1 });
                    await Task.Delay(m_Options.Backoff(attempt), cancel).ConfigureAwait(false);
                }
            }
        }

        async Task<JsonNode> Resolve(Uri uri, CancellationToken cancel)
        {
            using (var client = Client(m_Options.ResolveTimeout))
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response;
                try { response = await client.SendAsync(request, cancel).ConfigureAwait(false); }
                catch (TaskCanceledException e) when (!cancel.IsCancellationRequested) { throw new DownloadException("The Zimension server did not answer in time.", true, 0, e); }
                catch (HttpRequestException e) { throw new DownloadException("Cannot reach the Zimension server: " + e.Message, true, 0, e); }
                using (response)
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    JsonNode.TryParse(text, out var body);
                    if (!response.IsSuccessStatusCode) throw StatusError((int)response.StatusCode, body, "link");
                    if (body == null || !body.IsObject) throw new DownloadException("The link did not return export details (unexpected response).");
                    if (body["success"].IsBool && !body["success"].AsBool) throw new DownloadException(body.Str("error", body.Str("message", "The link was rejected.")));
                    return body;
                }
            }
        }

        static DownloadException StatusError(int status, JsonNode body, string what)
        {
            var detail = body != null && body.IsObject ? body.Str("error", body.Str("message")) : "";
            string message;
            switch (status)
            {
                case 401: case 403: message = "This export link is private or you do not have access to it."; break;
                case 404: message = what == "link" ? "Export link not found. Check the link, or export the project again in Zimension." : "The export file was not found on the server."; break;
                case 410: message = "This export link has expired or was revoked. Export the project again in Zimension for a new link."; break;
                case 413: message = "The export is too large for the server."; break;
                case 408: case 429: message = "The server is busy."; break;
                default: message = status >= 500 ? $"The Zimension server had a problem ({status})." : $"The request failed ({status})."; break;
            }
            if (!string.IsNullOrEmpty(detail) && detail.Length < 200) message += " " + detail;
            bool retry = status == 408 || status == 429 || status >= 500;
            return new DownloadException(message, retry, status);
        }

        async Task Download(string url, string part, string metaPath, Result result, IProgress<Progress> progress, CancellationToken cancel)
        {
            long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
            var meta = ReadMeta(metaPath);
            var etag = meta.Str("etag");
            using (var client = Client(Timeout.InfiniteTimeSpan))
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (existing > 0 && etag.Length > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existing, null);
                    request.Headers.TryAddWithoutValidation("If-Range", etag);
                }
                else existing = 0;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    stall.CancelAfter(m_Options.StallTimeout);
                    HttpResponseMessage response;
                    try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException e) when (!cancel.IsCancellationRequested) { throw new DownloadException("The download did not start in time.", true, 0, e); }
                    catch (HttpRequestException e) { throw new DownloadException("Network error: " + e.Message, true, 0, e); }
                    using (response)
                    {
                        int status = (int)response.StatusCode;
                        if (status == 416) { File.Delete(part); throw new DownloadException("The partial download no longer matches; restarting.", true, status); }
                        if (!response.IsSuccessStatusCode)
                        {
                            string text = null;
                            try { text = await response.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                            JsonNode.TryParse(text ?? "", out var body);
                            throw StatusError(status, body, "file");
                        }
                        bool partial = status == 206;
                        if (!partial) existing = 0; // the server sent the whole file
                        var newTag = response.Headers.ETag?.ToString() ?? "";
                        var tagValue = newTag.Trim('"').Replace("W/", "").Trim('"').ToLowerInvariant();
                        if (result.Sha256.Length == 64 && tagValue.Length == 64 && tagValue != result.Sha256)
                        {
                            if (File.Exists(part)) File.Delete(part);
                            throw new VersionChangedException(ResolvedKey(result));
                        }
                        if (partial && etag.Length > 0 && newTag.Length > 0 && newTag != etag) { File.Delete(part); throw new DownloadException("The export changed on the server; restarting.", true); }
                        long? length = response.Content.Headers.ContentLength;
                        long total = length.HasValue ? existing + length.Value : (result.Size > 0 ? result.Size : -1);
                        if (total > m_Options.MaxBytes) throw new DownloadException($"The download is over the {m_Options.MaxBytes / (1024 * 1024)} MB limit.");
                        WriteMeta(metaPath, result, newTag);
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(part, partial ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        {
                            var buffer = new byte[1 << 16];
                            long received = existing;
                            var started = DateTime.UtcNow; long startBytes = existing;
                            var lastReport = DateTime.MinValue;
                            while (true)
                            {
                                stall.CancelAfter(m_Options.StallTimeout);
                                int read;
                                try { read = await input.ReadAsync(buffer, 0, buffer.Length, stall.Token).ConfigureAwait(false); }
                                catch (OperationCanceledException e) when (!cancel.IsCancellationRequested) { throw new DownloadException("The download stalled.", true, 0, e); }
                                catch (IOException e) { throw new DownloadException("The connection dropped.", true, 0, e); }
                                catch (HttpRequestException e) { throw new DownloadException("The connection dropped.", true, 0, e); }
                                if (read == 0) break;
                                await output.WriteAsync(buffer, 0, read, cancel).ConfigureAwait(false);
                                received += read;
                                if (received > m_Options.MaxBytes) throw new DownloadException($"The download is over the {m_Options.MaxBytes / (1024 * 1024)} MB limit.");
                                var now = DateTime.UtcNow;
                                if ((now - lastReport).TotalMilliseconds > 150)
                                {
                                    lastReport = now;
                                    double seconds = Math.Max(0.001, (now - started).TotalSeconds);
                                    progress?.Report(new Progress { Stage = "Downloading", Received = received, Total = total, BytesPerSecond = (received - startBytes) / seconds });
                                }
                            }
                            if (length.HasValue && received != existing + length.Value)
                                throw new DownloadException("The download ended early.", true);
                            progress?.Report(new Progress { Stage = "Downloading", Received = received, Total = received });
                        }
                    }
                }
            }
        }

        void Verify(string part, Result result, bool fromLink)
        {
            var size = new FileInfo(part).Length;
            if (result.Size > 0 && size != result.Size)
            {
                File.Delete(part);
                if (fromLink) throw new VersionChangedException(ResolvedKey(result));
                throw new DownloadException($"The download is {size} bytes but the export is {result.Size} bytes. Try again.");
            }
            if (result.Sha256.Length == 64 && Sha256File(part) != result.Sha256)
            {
                File.Delete(part);
                throw new DownloadException("The download is corrupted (checksum mismatch). Try again.");
            }
            try { using (BundleSource.Open(part)) { } }
            catch (Exception e)
            {
                File.Delete(part);
                throw new DownloadException("The downloaded file is not a Zimension export: " + e.Message, false, 0, e);
            }
        }

        static string ResolvedKey(Result result) => (result.Version ?? "") + "|" + (result.Sha256 ?? "");

        // ---- Cache bookkeeping ----------------------------------------------------------------

        static string CacheKey(string link)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(link ?? ""));
                var sb = new StringBuilder();
                for (int i = 0; i < 10; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var bytes = sha.ComputeHash(stream);
                var sb = new StringBuilder(64);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static JsonNode ReadMeta(string path)
        {
            try { return File.Exists(path) && JsonNode.TryParse(File.ReadAllText(path), out var node) && node.IsObject ? node : JsonNode.NewObject(); }
            catch (IOException) { return JsonNode.NewObject(); }
        }

        static void WriteMeta(string path, Result result, string etag)
        {
            var meta = ReadMeta(path);
            meta.Set("version", new JsonNode(result.Version ?? ""));
            meta.Set("sha256", new JsonNode(result.Sha256 ?? ""));
            meta.Set("worldName", new JsonNode(result.WorldName ?? ""));
            if (etag.Length > 0) meta.Set("etag", new JsonNode(etag));
            meta.Set("updatedUtc", new JsonNode(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
            File.WriteAllText(path, meta.Dump(pretty: true));
        }

        /// <summary>Delete cached downloads (all, or every one except the given paths).</summary>
        public static long ClearCache(string cacheRoot)
        {
            if (!Directory.Exists(cacheRoot)) return 0;
            long freed = 0;
            foreach (var file in Directory.GetFiles(cacheRoot, "*", SearchOption.AllDirectories)) freed += new FileInfo(file).Length;
            Directory.Delete(cacheRoot, true);
            return freed;
        }
    }

    /// <summary>The importer version without UnityEditor references (for the HTTP user agent).</summary>
    public static class ZimensionImporterVersion
    {
        public const string Value = "0.3.2";
    }
}
