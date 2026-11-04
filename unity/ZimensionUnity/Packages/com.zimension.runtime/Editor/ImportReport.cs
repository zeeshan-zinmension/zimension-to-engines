using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Zimension.Core;

namespace Zimension.Editor
{
    /// <summary>
    /// The machine-readable result of an import (export-report.json): every asset with role, path,
    /// size and SHA-256, and every finding classified as blocking (the project cannot run as saved),
    /// approximated (runs, differently from the web) or unsupported/optional, with item id, name,
    /// saved field and reason.
    /// </summary>
    public sealed class ImportReport
    {
        public enum Level { Blocking, Approximated, Unsupported, Optional }

        public sealed class Finding
        {
            public Level Level;
            public string ItemId = "", ItemName = "", Field = "", Reason = "";
        }

        public sealed class Asset
        {
            public string Role = "", Source = "", Path = "", Sha256 = "";
            public long Bytes;
        }

        public string ExporterVersion = ZimensionImporter.Version;
        public string SchemaVersion = "1";
        public string WorldId = "", WorldName = "", Bundle = "";
        public DateTime StartedUtc = DateTime.UtcNow;
        public readonly List<Finding> Findings = new List<Finding>();
        public readonly List<Asset> Assets = new List<Asset>();
        public readonly List<string> Scenes = new List<string>();
        public int Items, ItemsPlaced;

        public bool HasBlocking => Findings.Any(f => f.Level == Level.Blocking);

        public void Add(Level level, string reason, string field = "", JsonNode item = null)
        {
            var f = new Finding
            {
                Level = level, Reason = reason, Field = field ?? "",
                ItemId = item != null ? item.Str("_id") : "", ItemName = item != null ? item.Str("name") : "",
            };
            foreach (var existing in Findings)
                if (existing.Level == f.Level && existing.Reason == f.Reason && existing.ItemId == f.ItemId && existing.Field == f.Field) return;
            Findings.Add(f);
        }

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Imported '{WorldName}' ({WorldId}) with exporter {ExporterVersion}: {ItemsPlaced}/{Items} items placed, {Assets.Count} assets, {Scenes.Count} scene(s).");
            foreach (var level in new[] { Level.Blocking, Level.Unsupported, Level.Approximated, Level.Optional })
            {
                var group = Findings.Where(f => f.Level == level).ToList();
                if (group.Count == 0) continue;
                sb.AppendLine($"{level} ({group.Count}):");
                foreach (var reason in group.GroupBy(f => f.Reason).Take(40))
                {
                    var names = reason.Select(f => f.ItemName).Where(n => !string.IsNullOrEmpty(n)).Distinct().Take(4).ToList();
                    sb.AppendLine($"  - {reason.Key}{(names.Count > 0 ? " [" + string.Join(", ", names) + (reason.Count() > names.Count ? ", ..." : "") + "]" : "")}");
                }
            }
            return sb.ToString();
        }

        public JsonNode ToJson()
        {
            var root = JsonNode.NewObject();
            root.Set("exporterVersion", new JsonNode(ExporterVersion));
            root.Set("schemaVersion", new JsonNode(SchemaVersion));
            root.Set("runtimeVersion", new JsonNode(ZimensionImporter.Version));
            root.Set("worldId", new JsonNode(WorldId));
            root.Set("worldName", new JsonNode(WorldName));
            root.Set("bundle", new JsonNode(System.IO.Path.GetFileName(Bundle ?? "")));
            root.Set("importedAtUtc", new JsonNode(StartedUtc.ToString("o")));
            root.Set("items", new JsonNode(Items));
            root.Set("itemsPlaced", new JsonNode(ItemsPlaced));
            var scenes = JsonNode.NewArray();
            foreach (var s in Scenes) scenes.Add(new JsonNode(s));
            root.Set("scenes", scenes);
            var assets = JsonNode.NewArray();
            foreach (var a in Assets)
            {
                var o = JsonNode.NewObject();
                o.Set("role", new JsonNode(a.Role));
                o.Set("source", new JsonNode(a.Source));
                o.Set("path", new JsonNode(a.Path));
                o.Set("bytes", new JsonNode(a.Bytes));
                o.Set("sha256", new JsonNode(a.Sha256));
                assets.Add(o);
            }
            root.Set("assets", assets);
            var findings = JsonNode.NewArray();
            foreach (var f in Findings)
            {
                var o = JsonNode.NewObject();
                o.Set("level", new JsonNode(f.Level.ToString().ToLowerInvariant()));
                o.Set("itemId", new JsonNode(f.ItemId));
                o.Set("itemName", new JsonNode(f.ItemName));
                o.Set("field", new JsonNode(f.Field));
                o.Set("reason", new JsonNode(f.Reason));
                findings.Add(o);
            }
            root.Set("findings", findings);
            return root;
        }
    }
}
