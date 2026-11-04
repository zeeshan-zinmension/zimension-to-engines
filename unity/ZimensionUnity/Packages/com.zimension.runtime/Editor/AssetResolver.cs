using System;
using System.Collections.Generic;
using System.IO;
using Zimension.Core;

namespace Zimension.Editor
{
    /// <summary>
    /// Finds a saved reference's packaged file inside the export, using the names Engine.generateApp
    /// (Engine.js exportWorld → generateApp) writes:
    /// - item model: <c>{file._id}{file.name}</c>, LOD levels <c>{file._id}lod-{n}{file.name}</c>;
    /// - avatar character: <c>{file._id}{file.name}</c>;
    /// - item/world sound: <c>sound{sound._id}{sound.fileName}</c>; voice JSON: <c>voice{sound._id}{sound.fileName}</c>;
    /// - CDN assets (rigs, environment, ground, title screen, custom settings): the URL path after the host;
    /// - library materials, animation sources and direct sounds: rewritten to <c>./static/files/{name}</c>.
    /// It never downloads anything: unresolved references are reported, not fetched.
    /// </summary>
    public sealed class AssetResolver
    {
        static readonly string[] CdnHosts =
        {
            "https://drxkpji2afm3z.cloudfront.net/",
            "https://zimension-thumbnail.s3.ap-south-1.amazonaws.com/",
        };
        static readonly string[] LocalPrefixes = { "./static/files/", "static/files/", "../../app-data/", "./", };

        readonly BundleSource m_Bundle;

        public AssetResolver(BundleSource bundle) { m_Bundle = bundle; }

        /// <summary>An item or character file record; the lowest LOD level present wins.</summary>
        public string ModelFile(JsonNode file)
        {
            if (file == null || !file.IsObject) return null;
            string id = file.Str("_id"), name = file.Str("name");
            if (id.Length > 0 && name.Length > 0)
            {
                var hit = m_Bundle.Find(id + name);
                if (hit != null) return hit;
                for (int level = 1; level <= 4; level++)
                {
                    hit = m_Bundle.Find(id + "lod-" + level + name);
                    if (hit != null) return hit;
                }
            }
            var url = file.Str("url");
            var byUrl = Url(url);
            if (byUrl != null) return byUrl;
            if (id.Length > 0)
            {
                var ext = Path.GetExtension(name);
                if (ext.Length > 0 && m_Bundle.Find(id + ext) != null) return m_Bundle.Find(id + ext);
            }
            return name.Length > 0 ? m_Bundle.Find(name) : null;
        }

        public string Sound(JsonNode sound)
        {
            if (sound == null || !sound.IsObject) return null;
            string id = sound.Str("_id"), fileName = sound.Str("fileName");
            if (id.Length > 0 || fileName.Length > 0)
            {
                var hit = m_Bundle.Find("sound" + id + fileName);
                if (hit != null) return hit;
            }
            return Url(sound.Str("url"));
        }

        public string Voice(JsonNode sound)
        {
            if (sound == null || !sound.IsObject || sound.Str("voiceDataUrl").Length == 0) return null;
            string id = sound.Str("_id"), fileName = sound.Str("fileName");
            var hit = m_Bundle.Find("voice" + id + fileName);
            return hit ?? Url(sound.Str("voiceDataUrl"));
        }

        /// <summary>A URL or packaged path: local export paths, CDN path tails, then the bare file name.</summary>
        public string Url(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var clean = url.Split('?', '#')[0];
            foreach (var prefix in LocalPrefixes)
            {
                if (!clean.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var hit = m_Bundle.Find(clean.Substring(prefix.Length));
                if (hit != null) return hit;
            }
            foreach (var host in CdnHosts)
            {
                if (!clean.StartsWith(host, StringComparison.Ordinal)) continue;
                var tail = Uri.UnescapeDataString(clean.Substring(host.Length));
                var hit = m_Bundle.Find(tail) ?? m_Bundle.Find(clean.Substring(host.Length));
                if (hit != null) return hit;
            }
            if (clean.StartsWith("http://", StringComparison.Ordinal) || clean.StartsWith("https://", StringComparison.Ordinal))
            {
                try
                {
                    var path = new Uri(clean).AbsolutePath.TrimStart('/');
                    var hit = m_Bundle.Find(Uri.UnescapeDataString(path)) ?? m_Bundle.Find(path);
                    if (hit != null) return hit;
                }
                catch (UriFormatException) { }
            }
            var direct = m_Bundle.Find(clean);
            if (direct != null) return direct;
            var file = Path.GetFileName(clean);
            return string.IsNullOrEmpty(file) ? null : m_Bundle.Find(file);
        }

        public static bool IsRemote(string url) =>
            !string.IsNullOrEmpty(url) && (url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal));
    }
}
