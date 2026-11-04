// Export reading and asset resolution against a synthetic Zimension Web export: Windows '\'
// separators as the web packager writes them, every naming rule of Engine.generateApp, and
// archive traversal rejection.
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Zimension.Core;
using Zimension.Editor;

static class BundleChecks
{
    static void Add(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name);
        using (var s = entry.Open()) s.Write(data, 0, data.Length);
    }

    static byte[] Glb(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        int pad = (4 - body.Length % 4) % 4;
        var chunk = new byte[body.Length + pad];
        Array.Copy(body, chunk, body.Length);
        for (int i = body.Length; i < chunk.Length; i++) chunk[i] = 0x20;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x46546C67); w.Write(2); w.Write(12 + 8 + chunk.Length);
        w.Write(chunk.Length); w.Write(0x4E4F534A); w.Write(chunk);
        return ms.ToArray();
    }

    public static void Run(Action<bool, string> expect)
    {
        var path = Path.Combine(Path.GetTempPath(), "zimension-bundle-" + Guid.NewGuid().ToString("N") + ".zip");
        const string master = @"{""world"":{""_id"":""w1"",""name"":""Fixture"",""sounds"":[{""_id"":""s9"",""fileName"":""music.mp3"",""url"":""https://cdn.example/music.mp3""}]},
            ""worldItems"":[{""_id"":""i1"",""name"":""Door"",""file"":{""_id"":""f1"",""name"":""Door.glb""},
              ""sounds"":[{""_id"":""s1"",""fileName"":""line.mp3"",""voiceDataUrl"":""https://cdn.example/line.json""}]}]}";
        using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
        {
            Add(zip, "index.html", Encoding.UTF8.GetBytes("<html></html>"));
            Add(zip, "static\\files\\masterData.json", Encoding.UTF8.GetBytes(master));
            Add(zip, "static\\files\\f1Door.glb", Glb(@"{""asset"":{""version"":""2.0""},""animations"":[{""name"":""Open""},{}]}"));
            Add(zip, "static\\files\\f1lod-2Lamp.glb", Glb(@"{""asset"":{""version"":""2.0""}}"));
            Add(zip, "static\\files\\sounds1line.mp3", new byte[] { 1, 2, 3 });
            Add(zip, "static\\files\\voices1line.mp3", Encoding.UTF8.GetBytes(@"{""mouthCues"":[]}"));
            Add(zip, "static\\files\\sounds9music.mp3", new byte[] { 4 });
            Add(zip, "static\\files\\rigs/mixamo/idle.glb", Glb(@"{""asset"":{""version"":""2.0""},""animations"":[{""name"":""Idle""}]}"));
            Add(zip, "static\\files\\anim-abc.glb", new byte[] { 9 });
            Add(zip, "static/files/../../evil.txt", new byte[] { 0 });
        }
        try
        {
            using (var bundle = BundleSource.Open(path))
            {
                var resolver = new AssetResolver(bundle);
                var root = JsonNode.Parse(bundle.MasterDataText);
                var item = root["worldItems"][0];
                expect(root.Obj("world").Str("name") == "Fixture", "masterData found under static\\files with Windows separators");
                expect(resolver.ModelFile(item.Obj("file")) == "f1Door.glb", "model: {file._id}{file.name}");
                expect(resolver.ModelFile(JsonNode.Parse(@"{""_id"":""f1"",""name"":""Lamp.glb""}")) == "f1lod-2Lamp.glb", "model: LOD level fallback");
                expect(resolver.Sound(item["sounds"][0]) == "sounds1line.mp3", "sound: sound{_id}{fileName}");
                expect(resolver.Voice(item["sounds"][0]) == "voices1line.mp3", "voice: voice{_id}{fileName}");
                expect(resolver.Sound(root.Obj("world")["sounds"][0]) == "sounds9music.mp3", "world sound");
                expect(resolver.Url("https://drxkpji2afm3z.cloudfront.net/rigs/mixamo/idle.glb") == "rigs/mixamo/idle.glb", "CDN path tail");
                expect(resolver.Url("./static/files/anim-abc.glb") == "anim-abc.glb", "rewritten local path");
                expect(resolver.Url("https://elsewhere.example/missing.png") == null, "unresolved URLs stay unresolved (never downloaded)");
                bool traversal = false;
                foreach (var n in bundle.Names) if (n.Contains("evil")) traversal = true;
                expect(!traversal, "archive traversal entries are rejected");
                expect(BundleSource.Normalize("a/../../b") == null && BundleSource.Normalize("C:/x") == null && BundleSource.Normalize("/x") == null, "unsafe paths normalize to null");
                var names = GltfInfo.AnimationNames(GltfInfo.ReadJson(bundle.ReadAllBytes("f1Door.glb")));
                expect(names.Count == 2 && names[0] == "Open" && names[1] == "animation_1", "GLB animation names by index");
            }
        }
        finally { File.Delete(path); }
    }
}
