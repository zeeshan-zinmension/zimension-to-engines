using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Zimension.Editor
{
    /// <summary>
    /// A Zimension export bundle: the editor's Web export (.zip, or its extracted folder), or any
    /// folder/zip holding masterData.json with its packaged assets beside it (static/files/ in a
    /// web export, resources/app-data/ in a desktop export).
    ///
    /// Entries are addressed by their path relative to masterData.json's folder, with '/'
    /// separators (the web packager writes Windows '\' separators into its zips). Paths that
    /// escape that folder or are absolute are rejected (archive traversal).
    /// </summary>
    public sealed class BundleSource : IDisposable
    {
        readonly ZipArchive m_Zip;
        readonly string m_Folder;
        readonly Dictionary<string, ZipArchiveEntry> m_Entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        readonly Dictionary<string, string> m_Files = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> m_Lower = new Dictionary<string, string>(StringComparer.Ordinal);

        public string Description { get; }
        public string MasterDataText { get; }

        public IEnumerable<string> Names => m_Zip != null ? (IEnumerable<string>)m_Entries.Keys : m_Files.Keys;

        BundleSource(string description) { Description = description; }

        public static BundleSource Open(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Choose a Zimension export (.zip or folder).");
            if (Directory.Exists(path)) return new BundleSource(path, folder: true);
            if (File.Exists(path))
            {
                if (Path.GetFileName(path).Equals("masterData.json", StringComparison.OrdinalIgnoreCase))
                    return new BundleSource(Path.GetDirectoryName(path), folder: true);
                return new BundleSource(path, folder: false);
            }
            throw new FileNotFoundException("Zimension export not found: " + path);
        }

        BundleSource(string path, bool folder)
        {
            Description = path;
            if (folder)
            {
                var master = Directory.EnumerateFiles(path, "masterData.json", SearchOption.AllDirectories)
                    .OrderBy(p => p.Length).FirstOrDefault();
                if (master == null) throw new InvalidDataException("No masterData.json in " + path + ". Export the project from Zimension (Export > Web) first.");
                m_Folder = Path.GetDirectoryName(master);
                foreach (var file in Directory.EnumerateFiles(m_Folder, "*", SearchOption.AllDirectories))
                {
                    var relative = Normalize(file.Substring(m_Folder.Length).TrimStart('/', '\\'));
                    if (relative == null) continue;
                    m_Files[relative] = file;
                    m_Lower[relative.ToLowerInvariant()] = relative;
                }
                MasterDataText = File.ReadAllText(master);
                return;
            }
            m_Zip = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry masterEntry = null;
            string prefix = null;
            foreach (var entry in m_Zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!name.EndsWith("masterData.json", StringComparison.OrdinalIgnoreCase)) continue;
                var folderPart = name.Substring(0, name.Length - "masterData.json".Length);
                if (masterEntry == null || folderPart.Length < prefix.Length) { masterEntry = entry; prefix = folderPart; }
            }
            if (masterEntry == null) throw new InvalidDataException("No masterData.json in " + path + ". Export the project from Zimension (Export > Web) first.");
            foreach (var entry in m_Zip.Entries)
            {
                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) continue;
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var relative = Normalize(name.Substring(prefix.Length));
                if (relative == null) continue;
                m_Entries[relative] = entry;
                m_Lower[relative.ToLowerInvariant()] = relative;
            }
            using (var reader = new StreamReader(masterEntry.Open())) MasterDataText = reader.ReadToEnd();
        }

        /// <summary>A safe relative path, or null for traversal/absolute/empty names.</summary>
        public static string Normalize(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            relative = relative.Replace('\\', '/');
            if (relative.StartsWith("/") || relative.Contains(":")) return null;
            var parts = new List<string>();
            foreach (var part in relative.Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") return null;
                parts.Add(part);
            }
            return parts.Count == 0 ? null : string.Join("/", parts);
        }

        /// <summary>The exact entry name for a relative path (case-insensitive fallback), or null.</summary>
        public string Find(string relative)
        {
            relative = Normalize(relative);
            if (relative == null) return null;
            if (m_Zip != null ? m_Entries.ContainsKey(relative) : m_Files.ContainsKey(relative)) return relative;
            return m_Lower.TryGetValue(relative.ToLowerInvariant(), out var exact) ? exact : null;
        }

        public bool Exists(string relative) => Find(relative) != null;

        public long Length(string relative)
        {
            var name = Find(relative);
            if (name == null) return -1;
            return m_Zip != null ? m_Entries[name].Length : new FileInfo(m_Files[name]).Length;
        }

        public Stream OpenRead(string relative)
        {
            var name = Find(relative) ?? throw new FileNotFoundException("Not in the export: " + relative);
            return m_Zip != null ? m_Entries[name].Open() : File.OpenRead(m_Files[name]);
        }

        public byte[] ReadAllBytes(string relative)
        {
            using (var stream = OpenRead(relative))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        public string ReadAllText(string relative)
        {
            using (var reader = new StreamReader(OpenRead(relative))) return reader.ReadToEnd();
        }

        public void Dispose() => m_Zip?.Dispose();
    }
}
