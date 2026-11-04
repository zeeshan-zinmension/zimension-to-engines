// Plays samples/event-fixture through the core runtime with the importer's coordinate conversion:
// on_start message, coin auto-pickup (counter, score flow), F at the button (delayed door event),
// the door transition's finished event, the coins >= 1 condition and the hidden reward spawning.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Zimension.Core;

static class FixtureChecks
{
    sealed class Host : EventHost
    {
        public readonly Dictionary<int, bool> Visible = new Dictionary<int, bool>();
        public readonly Dictionary<string, string> Variables = new Dictionary<string, string>();
        public readonly List<string> Messages = new List<string>(), Events = new List<string>();
        public readonly Dictionary<int, Matrix4x4> Moved = new Dictionary<int, Matrix4x4>();
        public string PromptText = "";
        public override void ItemVisible(int item, bool visible) => Visible[item] = visible;
        public override void VariableChanged(string name, string value) => Variables[name] = value;
        public override void Message(string text) => Messages.Add(text);
        public override void OnWorldEvent(string name, int scope) => Events.Add(name);
        public override void Prompt(string text, bool pickup) => PromptText = text;
        public override void ItemMoved(int item, Matrix4x4 local) => Moved[item] = local;
    }

    static string FindFixture()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "samples", "event-fixture", "static", "files", "masterData.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static void Run(Action<bool, string> expect)
    {
        var path = FindFixture();
        expect(path != null, "samples/event-fixture exists (run samples/make_event_fixture.py)");
        if (path == null) return;
        var master = JsonNode.Parse(File.ReadAllText(path));
        var world = EventParsing.ParseWorld(master.Obj("world"));
        var owners = new HashSet<string>(world.Flows.Select(f => f.Owner).Where(o => o.Length > 0));
        var names = new List<string>();
        foreach (var json in master["worldItems"].Items)
        {
            var item = new EventItem();
            if (!EventParsing.ParseItem(item, json, owners)) continue;
            // As the importer: three -> Unity, state 0 is the item itself, bounds from the shape size.
            Coordinates.TryMatrixFromThreeElements(json["transformation"], out var three);
            item.Local = Coordinates.ThreeToUnity(three);
            var states = new List<Matrix4x4> { item.Local };
            foreach (var s in json["states"].Items) states.Add(Coordinates.ThreeToUnity(EventParsing.StateMatrix(s)));
            item.States = states;
            var dims = json.Obj("primitiveSettings").Obj("dimensions");
            float r = dims.Float("radius", 0);
            var half = new Vector3(dims.Float("width", r * 2) / 2, dims.Float("height", r * 2) / 2, dims.Float("depth", r * 2) / 2);
            item.Low = item.Local.Translation - half; item.High = item.Local.Translation + half;
            world.Items.Add(item);
            names.Add(item.Name);
        }
        int Index(string name) => names.IndexOf(name);
        expect(names.Contains("Door") && names.Contains("Gold coin") && names.Contains("Reward") && names.Contains("Red button"), "fixture items have event behaviour");
        var host = new Host(); var r2 = new EventRuntime(); r2.Load(world, host); r2.Start();
        var far = new Vector3(0, 0, 8);
        expect(host.Messages.LastOrDefault()?.StartsWith("Grab the gold coin") == true, "on_start message");
        expect(host.Visible[Index("Reward")] == false, "reward hidden at start");
        // Pressing the button first: the door opens, the condition fails (no coin).
        var button = new Vector3(-3, 0, 2); // mirrored X
        r2.Update(0.016f, button);
        expect(host.PromptText == "press the button", "button prompt in range");
        r2.Interact();
        r2.Update(1.05f, far);
        expect(host.Events.Contains("open_door"), "delayed open_door after 1 s");
        for (int f = 0; f < 140; f++) r2.Update(1f / 60, far);
        expect(host.Events.Contains("door_opened") && host.Events.Contains("need_coin") && !host.Visible[Index("Reward")], "door opened without the coin -> need_coin");
        var door = host.Moved[Index("Door")];
        expect(Math.Abs(door.M42 - 4.6f) < 0.01f && Math.Abs(door.M43 + 4f) < 0.01f, "door raised to its state (Unity space)");
        // Collect the coin (auto pickup at its mirrored position), then the button again.
        r2.Update(0.016f, new Vector3(3, 0.5f, 3));
        expect(host.Variables["coins"] == "1" && host.Variables["score"] == "10" && !host.Visible[Index("Gold coin")], "coin collected: counter, score flow, hidden");
        r2.Update(2.5f, far); // interaction cooldown
        r2.Update(0.016f, button);
        r2.Interact();
        r2.Update(1.05f, far);
        for (int f = 0; f < 140; f++) r2.Update(1f / 60, far);
        expect(host.Visible[Index("Reward")] && host.Events.Contains("reward_show"), "coins >= 1 -> reward spawns");
        expect(host.Moved.ContainsKey(Index("Moving platform")), "auto ping-pong platform moves");
    }
}
