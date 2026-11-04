// The link importer's downloader against an in-process server implementing
// docs/BACKEND_UNITY_EXPORT.md, with injected faults: dropped connections (resume with Range),
// stalls, 5xx/429 retries, a version change mid-download, wrong checksums, non-zip payloads,
// 404/410 messages, cache reuse and cancellation.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zimension.Editor;

static class DownloadChecks
{
    sealed class Server : IDisposable
    {
        readonly HttpListener m_Listener = new HttpListener();
        public readonly string Root;
        public byte[] Zip;
        public string Sha;
        public string Version = "v1";
        public string Code = "Abcdefghijklmnop1234";
        public int Status = 200;            // resolve status override
        public int FailDownloads;           // next N downloads answer 503
        public long DropAfter = -1;         // next download drops the connection after this many bytes
        public bool StallOnce;              // next download stops sending (stall)
        public string ReportedSha;          // override the sha256 the link reports
        public bool ChangeVersionAfterDrop; // after a drop, the file changes (new ETag)
        public int Resolves, Downloads;
        public readonly List<string> Ranges = new List<string>();
        readonly CancellationTokenSource m_Stop = new CancellationTokenSource();

        public Server(byte[] zip)
        {
            int port = 20000 + new Random().Next(10000);
            Root = $"http://localhost:{port}/";
            m_Listener.Prefixes.Add(Root);
            m_Listener.Start();
            SetZip(zip);
            Task.Run(Loop);
        }

        public void SetZip(byte[] zip)
        {
            Zip = zip;
            using (var sha = SHA256.Create()) Sha = BitConverter.ToString(sha.ComputeHash(zip)).Replace("-", "").ToLowerInvariant();
        }

        public string ApiBase => Root + "api/v1";

        async Task Loop()
        {
            while (!m_Stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await m_Listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        void Json(HttpListenerResponse res, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            res.StatusCode = status; res.ContentType = "application/json"; res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length); res.Close();
        }

        void Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath;
            var res = ctx.Response;
            try
            {
                if (path == $"/api/v1/world/unity-export/link/{Code}")
                {
                    Resolves++;
                    if (Status != 200) { Json(res, Status, "{\"success\":false,\"error\":\"nope\"}"); return; }
                    Json(res, 200, $"{{\"success\":true,\"worldId\":\"w1\",\"worldName\":\"Fixture\",\"version\":\"{Version}\",\"size\":{Zip.Length},\"sha256\":\"{ReportedSha ?? Sha}\",\"downloadUrl\":\"/files/{Code}.zip\"}}");
                    return;
                }
                if (path == $"/files/{Code}.zip" || path == "/direct/export.zip")
                {
                    Downloads++;
                    if (FailDownloads > 0) { FailDownloads--; Json(res, 503, "{\"error\":\"busy\"}"); return; }
                    var etag = $"\"{Sha}\"";
                    long start = 0;
                    var range = ctx.Request.Headers["Range"];
                    if (range != null) Ranges.Add(range);
                    var ifRange = ctx.Request.Headers["If-Range"];
                    if (range != null && range.StartsWith("bytes=") && (ifRange == null || ifRange == etag))
                    {
                        start = long.Parse(range.Substring(6).TrimEnd('-'));
                        res.StatusCode = 206;
                        res.AddHeader("Content-Range", $"bytes {start}-{Zip.Length - 1}/{Zip.Length}");
                    }
                    res.AddHeader("ETag", etag);
                    res.ContentType = "application/zip";
                    long length = Zip.Length - start;
                    res.ContentLength64 = length;
                    var output = res.OutputStream;
                    if (DropAfter >= 0)
                    {
                        long drop = DropAfter; DropAfter = -1;
                        output.Write(Zip, (int)start, (int)Math.Min(drop, length));
                        output.Flush();
                        if (ChangeVersionAfterDrop) { ChangeVersionAfterDrop = false; var changed = (byte[])Zip.Clone(); SetZip(Rezip(changed)); Version = "v2"; }
                        res.Abort();
                        return;
                    }
                    if (StallOnce)
                    {
                        StallOnce = false;
                        output.Write(Zip, (int)start, (int)Math.Min(1000, length));
                        output.Flush();
                        Thread.Sleep(2500);
                        res.Abort();
                        return;
                    }
                    output.Write(Zip, (int)start, (int)length);
                    res.Close();
                    return;
                }
                if (path == "/direct/notzip.zip") { var b = Encoding.UTF8.GetBytes("<html>login</html>"); res.ContentLength64 = b.Length; res.OutputStream.Write(b, 0, b.Length); res.Close(); return; }
                Json(res, 404, "{\"success\":false}");
            }
            catch { try { res.Abort(); } catch { } }
        }

        public void Dispose() { m_Stop.Cancel(); try { m_Listener.Stop(); } catch { } }
    }

    static byte[] FixtureZip(string extra = "")
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var master = zip.CreateEntry("static\\files\\masterData.json");
            using (var w = new StreamWriter(master.Open())) w.Write("{\"world\":{\"_id\":\"w1\",\"name\":\"Fixture\"},\"worldItems\":[]}");
            var blob = zip.CreateEntry("static/files/blob.bin");
            var rnd = new Random(7); var data = new byte[600_000]; rnd.NextBytes(data);
            using (var s = blob.Open()) s.Write(data, 0, data.Length);
            if (extra.Length > 0) { var e = zip.CreateEntry("static/files/extra.txt"); using (var w = new StreamWriter(e.Open())) w.Write(extra); }
        }
        return ms.ToArray();
    }

    static byte[] Rezip(byte[] old) => FixtureZip("changed " + old.Length);

    static ExportDownloader Downloader(Server server, string cache, int stallMs = 60000) => new ExportDownloader(new ExportDownloader.Options
    {
        ApiBase = server.ApiBase, CacheRoot = cache, Backoff = _ => TimeSpan.FromMilliseconds(10),
        StallTimeout = TimeSpan.FromMilliseconds(stallMs),
    });

    static string Run(Func<Task> action)
    {
        try { action().GetAwaiter().GetResult(); return null; }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
    }

    public static void Run(Action<bool, string> expect)
    {
        var cache = Path.Combine(Path.GetTempPath(), "zim-cache-" + Guid.NewGuid().ToString("N"));
        using (var server = new Server(FixtureZip()))
        {
            try
            {
                // Classification.
                expect(ExportDownloader.Describe(server.Code) == "Zimension link code", "bare code");
                expect(ExportDownloader.Describe($"https://app.zimension3d.com/#/unity-export/{server.Code}") == "Zimension link code", "web share URL carries the code");
                expect(ExportDownloader.Describe($"{server.ApiBase}/world/unity-export/link/{server.Code}") == "Zimension link", "API link");
                expect(ExportDownloader.Describe("https://cdn.example/export.zip") == "download URL", "direct URL");

                // Resolve by code and download.
                ExportDownloader.Result result = null;
                var progress = new List<ExportDownloader.Progress>();
                var error = Run(async () => result = await Downloader(server, cache).FetchAsync(server.Code, new SyncProgress(progress), CancellationToken.None));
                expect(error == null, "code import downloads: " + error);
                expect(result != null && File.Exists(result.Path) && result.Sha256 == server.Sha && result.WorldName == "Fixture" && result.Version == "v1", "result carries path, sha, world and version");
                expect(result != null && result.SourceLink.EndsWith("/world/unity-export/link/" + server.Code), "source link remembered as the full link");
                expect(progress.Exists(p => p.Stage == "Downloading" && p.Total == server.Zip.Length), "progress reports total size");
                using (BundleSource.Open(result.Path)) { }

                // Cache reuse.
                int downloads = server.Downloads;
                error = Run(async () => result = await Downloader(server, cache).FetchAsync(server.Code, null, CancellationToken.None));
                expect(error == null && result.FromCache && server.Downloads == downloads, "same version is reused from the cache");

                // New version: Pull latest downloads again.
                server.SetZip(FixtureZip("v2")); server.Version = "v2";
                error = Run(async () => result = await Downloader(server, cache).FetchAsync(result.SourceLink, null, CancellationToken.None));
                expect(error == null && !result.FromCache && result.Version == "v2" && result.Sha256 == server.Sha, "a new version is downloaded via the remembered link: " + error);

                // Dropped connection resumes with Range.
                server.SetZip(FixtureZip("v3")); server.Version = "v3"; server.DropAfter = 200_000; server.Ranges.Clear();
                error = Run(async () => result = await Downloader(server, cache).FetchAsync(server.Code, null, CancellationToken.None));
                expect(error == null && result.Sha256 == server.Sha, "dropped download completes: " + error);
                expect(server.Ranges.Exists(r => r.StartsWith("bytes=") && !r.StartsWith("bytes=0-")), "resume used a Range request");

                // Version change during a resume restarts cleanly.
                server.SetZip(FixtureZip("v4")); server.Version = "v4"; server.DropAfter = 100_000; server.ChangeVersionAfterDrop = true;
                error = Run(async () => result = await Downloader(server, cache).FetchAsync(server.Code, null, CancellationToken.None));
                expect(error == null && result.Sha256 == server.Sha && ExportDownloader.Sha256File(result.Path) == server.Sha, "file changed mid-download restarts and yields the new version: " + error);

                // Server errors are retried.
                server.SetZip(FixtureZip("v5")); server.Version = "v5"; server.FailDownloads = 2;
                error = Run(async () => result = await Downloader(server, cache).FetchAsync(server.Code, null, CancellationToken.None));
                expect(error == null && result.Version == "v5", "503s are retried: " + error);

                // Stall is detected and retried.
                server.SetZip(FixtureZip("v6")); server.Version = "v6"; server.StallOnce = true;
                error = Run(async () => result = await Downloader(server, cache, stallMs: 800).FetchAsync(server.Code, null, CancellationToken.None));
                expect(error == null && result.Version == "v6", "stalled transfer is retried: " + error);

                // Wrong checksum fails and leaves no file behind.
                server.SetZip(FixtureZip("v7")); server.Version = "v7"; server.ReportedSha = new string('a', 64);
                error = Run(async () => result = await Downloader(server, cache).FetchAsync(server.Code, null, CancellationToken.None));
                expect(error != null && error.Contains("checksum"), "checksum mismatch is reported: " + error);
                server.ReportedSha = null;

                // Link errors.
                server.Status = 410;
                error = Run(async () => await Downloader(server, cache).FetchAsync(server.Code, null, CancellationToken.None));
                expect(error != null && error.Contains("expired"), "410 explains the link expired: " + error);
                server.Status = 404;
                error = Run(async () => await Downloader(server, cache).FetchAsync(server.Code, null, CancellationToken.None));
                expect(error != null && error.Contains("not found"), "404 explains the link is unknown: " + error);
                server.Status = 200;

                // Direct URLs, and payloads that are not exports.
                error = Run(async () => result = await Downloader(server, cache).FetchAsync(server.Root + "direct/export.zip", null, CancellationToken.None));
                expect(error == null && File.Exists(result.Path), "direct zip URL: " + error);
                error = Run(async () => await Downloader(server, cache).FetchAsync(server.Root + "direct/notzip.zip", null, CancellationToken.None));
                expect(error != null && error.Contains("not a Zimension export"), "an HTML page is rejected: " + error);
                error = Run(async () => await Downloader(server, cache).FetchAsync("not a link at all", null, CancellationToken.None));
                expect(error != null && error.Contains("not a link"), "garbage input explains itself");

                // Cancellation.
                server.SetZip(FixtureZip("v8")); server.Version = "v8"; server.StallOnce = true;
                var cts = new CancellationTokenSource(300);
                error = Run(async () => await Downloader(server, cache).FetchAsync(server.Code, null, cts.Token));
                expect(error != null && (error.Contains("Cancel") || error.Contains("cancel")), "cancel stops the download: " + error);
            }
            finally
            {
                ExportDownloader.ClearCache(cache);
            }
        }
    }

    sealed class SyncProgress : IProgress<ExportDownloader.Progress>
    {
        readonly List<ExportDownloader.Progress> m_List;
        public SyncProgress(List<ExportDownloader.Progress> list) { m_List = list; }
        public void Report(ExportDownloader.Progress value) { lock (m_List) m_List.Add(value); }
    }
}
