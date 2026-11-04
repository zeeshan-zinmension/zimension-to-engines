using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Zimension.Core;
using Zimension.Unity;

namespace Zimension.Editor
{
    /// <summary>
    /// Link imports: download (off the main thread) then import (on it), and remember each world's
    /// link for Pull latest. Links are capabilities, so they are stored per machine in
    /// Library/ZimensionDownloads/links.json: never in Assets (version control) or in builds.
    /// </summary>
    public static class ZimensionLinkImport
    {
        public static string CacheRoot => Path.Combine(Directory.GetCurrentDirectory(), "Library", "ZimensionDownloads");
        static string LinksPath => Path.Combine(CacheRoot, "links.json");

        public static string ApiBase
        {
            get => EditorPrefs.GetString("Zimension.ApiBase", ExportDownloader.DefaultApiBase);
            set => EditorPrefs.SetString("Zimension.ApiBase", string.IsNullOrWhiteSpace(value) ? ExportDownloader.DefaultApiBase : value.Trim().TrimEnd('/'));
        }

        public sealed class Job
        {
            public string Input;
            public Task<ExportDownloader.Result> Download;
            public CancellationTokenSource Cancel = new CancellationTokenSource();
            readonly object m_Lock = new object();
            ExportDownloader.Progress m_Progress = new ExportDownloader.Progress { Stage = "Starting", Total = -1 };
            public ExportDownloader.Progress Progress { get { lock (m_Lock) return m_Progress; } }
            public void Report(ExportDownloader.Progress p) { lock (m_Lock) m_Progress = p; }
            public bool Done => Download != null && Download.IsCompleted;
        }

        sealed class Reporter : IProgress<ExportDownloader.Progress>
        {
            readonly Job m_Job;
            public Reporter(Job job) { m_Job = job; }
            public void Report(ExportDownloader.Progress value) => m_Job.Report(value);
        }

        public static Job Start(string input)
        {
            var job = new Job { Input = input };
            var downloader = new ExportDownloader(new ExportDownloader.Options { ApiBase = ApiBase, CacheRoot = CacheRoot });
            job.Download = downloader.FetchAsync(input, new Reporter(job), job.Cancel.Token);
            return job;
        }

        /// <summary>The download's failure as a sentence (null when it succeeded).</summary>
        public static string ErrorOf(Job job)
        {
            if (job.Download.IsCanceled) return "Cancelled.";
            if (!job.Download.IsFaulted) return null;
            var e = job.Download.Exception?.GetBaseException();
            if (e is OperationCanceledException) return "Cancelled.";
            return e is ExportDownloader.DownloadException ? e.Message : "Download failed: " + e?.Message;
        }

        /// <summary>Import a finished download (main thread) and remember its link.</summary>
        public static ZimensionImporter.Result Finish(Job job, ZimensionImporter.Options options)
        {
            var download = job.Download.Result;
            var result = ZimensionImporter.Import(download.Path, options);
            if (result.Project)
            {
                result.Project.sourceVersion = download.Version;
                result.Project.sourceSha256 = download.Sha256;
                EditorUtility.SetDirty(result.Project);
                AssetDatabase.SaveAssets();
                if (!download.IsLocal && !string.IsNullOrEmpty(download.SourceLink))
                    Remember(result.Project.worldId, download.SourceLink, download.Version, download.WorldName);
            }
            return result;
        }

        /// <summary>Synchronous fetch and import, for batch mode.</summary>
        public static ZimensionImporter.Result ImportBlocking(string input, ZimensionImporter.Options options)
        {
            var job = Start(input);
            try { job.Download.Wait(); }
            catch (AggregateException) { }
            var error = ErrorOf(job);
            if (error != null) return new ZimensionImporter.Result { Success = false, Error = error };
            return Finish(job, options);
        }

        // ---- Remembered links (Library, per machine) ---------------------------------------------

        public sealed class Remembered
        {
            public string WorldId, Link, Version, WorldName, ImportedUtc;
        }

        public static List<Remembered> All()
        {
            var list = new List<Remembered>();
            try
            {
                if (!File.Exists(LinksPath) || !JsonNode.TryParse(File.ReadAllText(LinksPath), out var root)) return list;
                foreach (var pair in root.Members)
                    list.Add(new Remembered
                    {
                        WorldId = pair.Key, Link = pair.Value.Str("link"), Version = pair.Value.Str("version"),
                        WorldName = pair.Value.Str("worldName"), ImportedUtc = pair.Value.Str("importedUtc"),
                    });
            }
            catch (IOException) { }
            return list;
        }

        public static Remembered Find(string worldId) => All().FirstOrDefault(r => r.WorldId == worldId);

        static void Remember(string worldId, string link, string version, string worldName)
        {
            if (string.IsNullOrEmpty(worldId)) return;
            Directory.CreateDirectory(CacheRoot);
            JsonNode root = File.Exists(LinksPath) && JsonNode.TryParse(File.ReadAllText(LinksPath), out var parsed) && parsed.IsObject ? parsed : JsonNode.NewObject();
            var entry = JsonNode.NewObject();
            entry.Set("link", new JsonNode(link));
            entry.Set("version", new JsonNode(version ?? ""));
            entry.Set("worldName", new JsonNode(worldName ?? ""));
            entry.Set("importedUtc", new JsonNode(DateTime.UtcNow.ToString("o")));
            root.Set(worldId, entry);
            File.WriteAllText(LinksPath, root.Dump(pretty: true));
        }

        public static void Forget(string worldId)
        {
            if (!File.Exists(LinksPath) || !JsonNode.TryParse(File.ReadAllText(LinksPath), out var root) || !root.IsObject) return;
            var kept = JsonNode.NewObject();
            foreach (var pair in root.Members) if (pair.Key != worldId) kept.Set(pair.Key, pair.Value);
            File.WriteAllText(LinksPath, kept.Dump(pretty: true));
        }
    }
}
