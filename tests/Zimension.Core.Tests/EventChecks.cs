// Event runtime checks against synthetic worlds: logic flows, variables, conditions, switch,
// delays, local scope, collectables, ranges, interactions and transitions (Engine.js semantics).
// A C# port of the native player's tests/EventChecks.cpp, so the Unity core is held to the same
// regression cases as the native runtime it was ported from.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Zimension.Core;

static class Program
{
    static int s_Failures;
    static int s_Checks;

    static void Expect(bool ok, string what)
    {
        s_Checks++;
        if (!ok) { s_Failures++; Console.Error.WriteLine("FAIL: " + what); }
    }

    sealed class Host : EventHost
    {
        public Dictionary<int, bool> Visible = new Dictionary<int, bool>();
        public Dictionary<int, Matrix4x4> Moved = new Dictionary<int, Matrix4x4>();
        public Dictionary<string, string> Variables = new Dictionary<string, string>();
        public List<string> Messages = new List<string>(), Ui = new List<string>(), Builtins = new List<string>(), Order = new List<string>();
        public List<int> Used = new List<int>();
        public HashSet<int> Contacts = new HashSet<int>();
        public string PromptText = ""; public bool Pickup;
        public List<(int item, int card)> Animations = new List<(int, int)>();
        public List<(int item, int card, bool play)> Sounds = new List<(int, int, bool)>();
        public int Collects;
        public override bool PlayerTouches(int item) => Contacts.Contains(item);
        public override void OnWorldEvent(string name, int scope) { Ui.Add(name); Order.Add(name); }
        public override void UseGameplay(int item) { Used.Add(item); Order.Add("use:" + item); }
        public override void VariableChanged(string name, string value) => Variables[name] = value;
        public override void Message(string text) => Messages.Add(text);
        public override void Prompt(string text, bool pickup) { PromptText = text; Pickup = pickup; }
        public override void Builtin(string name) => Builtins.Add(name);
        public override void ItemVisible(int item, bool v) => Visible[item] = v;
        public override void ItemMoved(int item, Matrix4x4 local) => Moved[item] = local;
        public override void Animation(int item, int card, bool play) { if (play) Animations.Add((item, card)); }
        public override void Sound(int item, int card, bool play) => Sounds.Add((item, card, play));
        public override void Collected(int item, bool inventory) => Collects++;
    }

    static bool Traced(EventRuntime r, string text) => r.TraceLines.Any(line => line.Contains(text));
    static EventWorld World(string json) => EventParsing.ParseWorld(JsonNode.Parse(json));
    static bool Item(EventItem item, string json, ICollection<string> owners = null) => EventParsing.ParseItem(item, JsonNode.Parse(json), owners ?? new List<string>());
    static int Count(List<string> list, string name) => list.Count(x => x == name);

    const string WorldJson = @"{
  ""events"": [""add"", ""check"", ""win"", ""lose"", ""delayed"", ""open"", ""sw"", ""door_done"", ""loop"", ""multiply"", ""collected""],
  ""variables"": [
    {""name"": ""score"", ""dataType"": ""number"", ""initialValue"": 0},
    {""name"": ""flag"", ""dataType"": ""boolean"", ""initialValue"": false},
    {""name"": ""coins"", ""dataType"": ""number"", ""initialValue"": 0}
  ],
  ""eventBroadcasters"": [
    {""name"": ""Start"", ""startEvent"": ""on_start"", ""actionIndex"": 0, ""triggerEvents"": [""add"", ""add""], ""showMessage"": ""Welcome"", ""link"": """"},
    {""name"": ""Add"", ""startEvent"": ""add"", ""actionIndex"": 1, ""targetVariable"": {""name"": ""score"", ""dataType"": ""number""},
     ""values"": [{""type"": ""Value"", ""value"": ""5""}], ""arthematicOperators"": [""+""]},
    {""name"": ""Check"", ""startEvent"": ""check"", ""actionIndex"": 2, ""trueEvent"": ""win"", ""falseEvent"": ""lose"",
     ""conditions"": [
       {""targetVariable"": {""name"": ""score""}, ""logicOperator"": "">="", ""variableValueMode"": ""Value"", ""variableValue"": ""10""},
       {""targetVariable"": {""name"": ""flag""}, ""logicOperator"": ""="", ""variableValueMode"": ""Value"", ""variableValue"": ""false""},
       {""targetVariable"": {""name"": ""score""}, ""logicOperator"": ""="", ""variableValueMode"": ""Value"", ""variableValue"": ""-1""}],
     ""conditionOperators"": [""AND"", ""OR""]},
    {""name"": ""Delayed"", ""startEvent"": ""delayed"", ""actionIndex"": 0, ""triggerEvents"": [""check""], ""delay"": 1.5},
    {""name"": ""Switch"", ""startEvent"": ""sw"", ""actionType"": ""Switch Case"", ""switchVariable"": {""name"": ""score"", ""dataType"": ""number""},
     ""cases"": [{""value"": ""3"", ""event"": ""lose""}, {""value"": ""10"", ""event"": ""win""}], ""defaultEvent"": ""lose""},
    {""name"": ""Ping"", ""startEvent"": ""@ping"", ""ownerItemId"": ""door"", ""actionIndex"": 1, ""targetVariable"": {""name"": ""@count"", ""dataType"": ""number""},
     ""values"": [{""type"": ""Value"", ""value"": 1}], ""arthematicOperators"": [""+""]},
    {""name"": ""Loop"", ""startEvent"": ""loop"", ""actionIndex"": 0, ""triggerEvents"": [""loop""]},
    {""name"": ""Multiply"", ""startEvent"": ""multiply"", ""actionIndex"": 1, ""targetVariable"": {""name"": ""score"", ""dataType"": ""number""},
     ""values"": [{""type"": ""Value"", ""value"": ""2""}], ""arthematicOperators"": [""×""]},
    {""name"": ""Unlisted"", ""startEvent"": ""not_listed"", ""actionIndex"": 0, ""triggerEvents"": [""win""]}
  ]
}";
    const string Door = @"{""_id"": ""door"", ""name"": ""Door"", ""enableInteraction"": true, ""interactionName"": ""Open"",
  ""interactionTriggers"": [""@ping"", ""open""], ""interactionTime"": 2,
  ""variables"": [{""name"": ""@count"", ""dataType"": ""number"", ""initialValue"": 0}],
  ""states"": [{""transformation"": [1,0,0,0, 0,1,0,0, 0,0,1,0, 2,0,0,1]}],
  ""transitions"": [{""name"": ""Open"", ""steps"": [{""stateIndex"": 0, ""duration"": 1, ""movement"": ""Linear""}, {""stateIndex"": 1, ""duration"": ""1"", ""movement"": ""Linear""}],
    ""playmode"": ""event"", ""event"": ""open"", ""loop"": false, ""transitionFinishedEvent"": ""door_done"", ""transitionStopEvent"": """"}]}";
    const string Coin = @"{""_id"": ""coin"", ""name"": ""Coin"", ""collectable"": true, ""collectCountVariable"": {""name"": ""coins"", ""dataType"": ""number""},
  ""onCollectEvent"": ""add""}";
    const string Prize = @"{""_id"": ""prize"", ""name"": ""Prize"", ""spawnEvent"": ""win""}";
    const string Plain = @"{""_id"": ""rock"", ""name"": ""Rock""}";

    static int Main()
    {
        try
        {
            Values();
            MainWorld();
            Targets();
            Contacts();
            Guides();
            ItemPairs();
            Paths();
            SceneCarry();
            Waypoints();
            Extras();
            CoordinateChecks();
            BundleChecks.Run(Expect);
            FixtureChecks.Run(Expect);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Error: " + error);
            return 1;
        }
        if (s_Failures > 0) { Console.Error.WriteLine($"{s_Failures} of {s_Checks} event checks failed"); return 1; }
        Console.WriteLine($"Event checks passed ({s_Checks}): values, flows, conditions, switch, delays, local scope, collect, interaction, transition, reset; " +
                          "unified targets, live bounds, solid surface triggers, contact collection, vehicle proxy ranges, diagnostics, paths, scene carry, waypoints, coordinates.");
        return 0;
    }

    static void Values()
    {
        // Value semantics (util.services performArithmetic / performComparisonOperation, JS String()).
        Expect(EventValue.Display(EventValue.Of(10.0)) == "10" && EventValue.Display(EventValue.Of(0.5)) == "0.5" && EventValue.Display(EventValue.Of(-0.0)) == "0", "number display");
        Expect(EventValue.Compare(EventValue.Of("5"), EventValue.Of(5.0), "=") && !EventValue.Compare(EventValue.Of("5"), EventValue.Of(5.0), "==="), "loose and strict equality");
        Expect(EventValue.Compare(EventValue.Of(true), EventValue.Of(1.0), "=") && EventValue.Compare(EventValue.Of("b"), EventValue.Of("a"), ">"), "boolean and string comparison");
        Expect(EventValue.Arithmetic(EventValue.Of("4"), EventValue.Of("2.5abc"), "-").Number == 1.5, "parseFloat arithmetic");
        Expect(EventValue.Arithmetic(EventValue.Of(3.0), EventValue.Of(1.0), "?").Number == 0, "unknown operator zeroes");
        Expect(EventValue.Display(EventValue.Of(1e-7)) == "1e-7" && EventValue.Display(EventValue.Of(0.1 + 0.2)) == "0.30000000000000004", "JavaScript number formatting");
        Expect(double.IsNaN(EventValue.ToNumber(EventValue.Of("12px"))) && EventValue.ParseFloat(EventValue.Of("12px")) == 12, "Number vs parseFloat");
    }

    static void MainWorld()
    {
        var world = World(WorldJson);
        var owners = new List<string> { "door" };
        foreach (var (json, center) in new[] { (Door, 0.0f), (Coin, 5.0f), (Prize, 20.0f) })
        {
            var item = new EventItem();
            Expect(Item(item, json, owners), "item has event behaviour");
            item.Local = item.Parent = Matrix4x4.Identity;
            item.Low = new Vector3(center - 0.5f, 0, -0.5f); item.High = new Vector3(center + 0.5f, 1, 0.5f);
            item.States.Insert(0, item.Local);
            world.Items.Add(item);
        }
        { var plain = new EventItem(); Expect(!Item(plain, Plain, owners), "plain item has no event behaviour"); }
        Expect(world.Flows.Count == 9 && world.Items[0].Transitions.Count == 1 && world.Items[2].Hidden, "parse");

        var host = new Host(); var runtime = new EventRuntime(); runtime.Load(world, host);
        Expect(host.Visible[2] == false && host.Variables["score"] == "0", "load state reaches the host");
        runtime.Start();
        Expect(host.Variables["score"] == "10", "on_start -> add twice");
        Expect(host.Messages.Count > 0 && host.Messages.Last() == "Welcome", "flow message");
        Expect(host.Ui.Count > 0 && host.Ui.First() == "on_start", "UI hears world events");
        // Built-ins (player_health, ...) become variables conditions can read; the HUD hears only changes.
        runtime.BuiltIn("player_health", EventValue.Of(100.0)); host.Variables.Remove("player_health");
        runtime.BuiltIn("player_health", EventValue.Of(100.0));
        Expect(!host.Variables.ContainsKey("player_health"), "unchanged built-in is not re-sent");
        runtime.BuiltIn("player_health", EventValue.Of(72.5));
        Expect(host.Variables["player_health"] == "72.5" && runtime.Variable("player_health") != null, "built-in change reaches the HUD and the runtime");

        runtime.TriggerEvent("check");
        Expect(host.Visible[2], "AND/OR condition true -> win spawns the prize");
        host.Visible[2] = false;
        runtime.TriggerEvent("sw");
        Expect(host.Visible[2], "switch case 10 -> win");
        host.Visible[2] = false;
        runtime.TriggerEvent("not_listed");
        Expect(!host.Visible[2], "unlisted events have no triggers");

        // Delays, and clear_delay_events cancelling them.
        var before = runtime.EventsRun;
        runtime.TriggerEvent("delayed"); runtime.Update(1.0f, new Vector3(100, 0, 100));
        Expect(runtime.EventsRun == before + 1, "delay not yet elapsed");
        runtime.Update(0.6f, new Vector3(100, 0, 100));
        Expect(runtime.EventsRun > before + 1, "delayed check ran");
        runtime.TriggerEvent("delayed"); runtime.TriggerEvent("clear_delay_events");
        var cleared = runtime.EventsRun; runtime.Update(2, new Vector3(100, 0, 100));
        Expect(runtime.EventsRun == cleared, "clear_delay_events cancels pending events");

        runtime.TriggerEvent("multiply");
        Expect(host.Variables["score"] == "20", "multiply glyph");
        runtime.TriggerEvent("loop");
        Expect(Traced(runtime, "Event loop stopped"), "recursion guard");

        // Auto-pickup collectable: counter, hidden, On Collect.
        runtime.Update(0.016f, new Vector3(5, 0, 0));
        Expect(host.Variables["coins"] == "1" && host.Variables["score"] == "25" && !host.Visible[1] && host.Collects == 1, "collect (counter, hide, collect sound, On Collect)");

        // Interaction: prompt, local flow on the owner, transition, cooldown.
        runtime.Update(0.016f, new Vector3(0, 0, 0));
        Expect(host.PromptText == "Open" && !host.Pickup, "interaction prompt");
        runtime.Interact();
        Expect(runtime.Variable("@count", 0) != null && EventValue.Display(runtime.Variable("@count", 0).Value) == "1", "local variable via owned flow");
        Expect(host.PromptText.Length == 0, "prompt hidden during cooldown");
        runtime.Update(0.5f, new Vector3(0, 0, 0));
        Expect(host.Moved.ContainsKey(0) && Math.Abs(host.Moved[0].M41 - 1.0f) < 0.02f, "transition halfway");
        runtime.Update(0.6f, new Vector3(0, 0, 0));
        Expect(Math.Abs(host.Moved[0].M41 - 2.0f) < 1e-4f && Traced(runtime, "door_done"), "transition finished event");
        // The range bounds moved with the door (x 1.5..2.5).
        runtime.Update(1.0f, new Vector3(2, 0, 0));
        Expect(host.PromptText == "Open", "prompt returns after interactionTime, at the moved door");
        runtime.Update(0.016f, new Vector3(50, 0, 50));
        Expect(host.PromptText.Length == 0, "leaving range hides the prompt");

        // Builtins skip world triggers; reset_world restores the load state.
        runtime.TriggerEvent("player_dead");
        Expect(host.Builtins.Count > 0 && host.Builtins.Last() == "player_dead", "builtin");
        runtime.Reset();
        Expect(host.Variables["score"] == "0" && !host.Visible[2] && host.Visible[1], "reset");
    }

    static void Targets()
    {
        // One entry-ordered overlap target owns both the prompt and F, regardless of distance/type.
        var targets = World(@"{""events"":[""open"",""armed""]}");
        foreach (var json in new[]
        {
            @"{""_id"":""truck"",""name"":""Truck"",""collision"":""object"",""vehicleSettings"":{""enable"":true}}",
            @"{""_id"":""rifle"",""name"":""Rifle"",""collision"":""object"",""fireWeaponSettings"":{""enable"":true}}",
            @"{""_id"":""door"",""name"":""Door"",""enableInteraction"":true,""interactionName"":""Open Door"",""interactionTriggers"":[""open""],""interactionTime"":2}",
            @"{""_id"":""crate"",""name"":""Crate"",""collision"":""object"",""pickable"":true}",
            @"{""_id"":""fuse"",""name"":""Fuse"",""collectable"":true,""collectSettings"":{""autoPickup"":false},""fireWeaponSettings"":{""enable"":true},""enableInteraction"":true,""interactionTriggers"":[""open""]}",
            @"{""_id"":""armed"",""name"":""Interactive rifle"",""enableInteraction"":true,""interactionName"":""Arm"",""interactionTriggers"":[""armed""],""fireWeaponSettings"":{""enable"":true}}",
            @"{""_id"":""inactive"",""name"":""Inactive gun"",""active"":false,""fireWeaponSettings"":{""enable"":true}}",
            @"{""_id"":""custom"",""name"":""Custom truck"",""vehicleSettings"":{""enable"":true,""interactionLabel"":""Drive Truck"",""exitLabel"":""Leave Truck""},""collectable"":true,""collectSettings"":{""autoPickup"":false},""enableInteraction"":true,""interactionTriggers"":[""open""]}",
        })
        {
            var item = new EventItem();
            Expect(Item(item, json), "gameplay target retained by ParseItem");
            item.Local = item.Parent = Matrix4x4.Identity; item.Low = new Vector3(50, 0, -0.3f); item.High = new Vector3(51, 1.5f, 0.3f);
            targets.Items.Add(item);
        }
        { var disabled = new EventItem(); Expect(!Item(disabled, @"{""_id"":""off"",""vehicleSettings"":{""enable"":false},""fireWeaponSettings"":{""enable"":false},""pickable"":false}"), "disabled gameplay settings are not targets"); }
        var host = new Host(); var r = new EventRuntime(); r.Load(targets, host);
        void Range(int item, bool inside) => r.MoveBounds(item, new Vector3(inside ? -0.3f : 50.0f, 0, -0.3f), new Vector3(inside ? 0.3f : 51.0f, 1.5f, 0.3f));
        void Tick(float seconds = 0.016f) => r.Update(seconds, Vector3.Zero);
        Tick(); r.Interact(); Expect(host.PromptText.Length == 0 && host.Used.Count == 0, "no overlap means no prompt and no gameplay action");
        Range(0, true); Tick(); Expect(host.PromptText == "Enter Vehicle" && !host.Pickup && r.InteractionItem == 0, "vehicle default interaction prompt");
        r.Interact(); Expect(host.Used.SequenceEqual(new[] { 0 }), "F uses the targeted vehicle");
        r.GameplayState(0, -1, -1); r.Update(0.016f, new Vector3(100, 0, 100));
        Expect(host.PromptText == "Exit Vehicle" && !host.Pickup && r.InteractionItem == 0, "mounted exit prompt survives leaving overlap bounds");
        r.Interact(); Expect(host.Used.Last() == 0, "mounted F uses the mounted vehicle");
        r.GameplayState(-1, -1, -1); Range(0, false); Range(1, true); Tick();
        Expect(host.PromptText == "Rifle" && host.Pickup && r.InteractionItem == 1, "weapon pickup prompt");
        r.Interact(); Expect(host.Used.Last() == 1, "F uses only the targeted gun");
        Range(2, true); Tick(); Expect(host.PromptText == "Open Door" && !host.Pickup && r.InteractionItem == 2, "newer door takes target from overlapping gun");
        var uses = host.Used.Count; r.Interact();
        Expect(host.Used.Count == uses && host.Ui.Last() == "open", "door target opens with F without equipping the nearby gun");
        Tick(2.1f); Range(1, false); Tick(); Range(1, true); Tick();
        Expect(r.InteractionItem == 1 && host.PromptText == "Rifle", "reentry makes the gun newest instead of fixed type priority");
        r.GameplayState(-1, 1, -1); Tick(); Expect(r.InteractionItem == 2 && host.PromptText == "Open Door", "equipped gun is excluded from target selection");
        Range(2, false); Range(3, true); r.GameplayState(-1, 1, 3); Tick(); Expect(host.PromptText.Length == 0, "held prop is excluded");
        r.GameplayState(-1, 1, -1); Tick(); Expect(host.PromptText == "Crate" && host.Pickup, "prop pickup prompt after release");
        r.Interact(); Expect(host.Used.Last() == 3, "F uses the targeted prop");
        Range(3, false); Tick(); Expect(host.PromptText.Length == 0, "live prop bounds leave the player box");
        Range(4, true); Tick(); Expect(host.PromptText == "Fuse" && host.Pickup, "manual collectible pickup prompt");
        int beforeUses = host.Used.Count, beforeEvents = host.Ui.Count; r.Interact();
        Expect(host.Collects == 1 && host.Used.Count == beforeUses && host.Ui.Count == beforeEvents, "manual collect precedes interaction triggers and weapon pickup");
        Range(5, true); Tick(); host.Order.Clear(); r.Interact();
        Expect(host.Order.SequenceEqual(new[] { "armed", "use:5" }), "interaction triggers precede weapon pickup on the same target");
        Range(5, false); Range(6, true); Tick(); Expect(host.PromptText.Length == 0, "inactive gun is never targeted");
        Range(7, true); Tick(); Expect(host.PromptText == "Drive Truck" && !host.Pickup, "custom vehicle label");
        var collects = host.Collects; host.Order.Clear(); r.Interact();
        Expect(host.Order.SequenceEqual(new[] { "use:7" }) && host.Collects == collects, "vehicle use precedes manual collect and interaction triggers");
        r.GameplayState(7, -1, -1); Expect(host.PromptText == "Leave Truck" && !host.Pickup, "custom exit label");
        r.Reset(); Tick(); Expect(r.InteractionItem < 0 && host.PromptText.Length == 0, "reset clears mounted/equipped/held state and restores load bounds");
    }

    static void Contacts()
    {
        // Solid contact triggers must survive the physical solver's separation from a surface.
        var contacts = World(@"{""events"":[""touch"",""leave"",""collected""]}");
        var platform = new EventItem();
        Expect(Item(platform, @"{""_id"":""platform"",""name"":""Scaled platform"",""collision"":""object"",""onEnterRangeEvent"":""touch"",""onLeaveRangeEvent"":""leave""}"), "solid touch trigger retained");
        platform.Local = platform.Parent = Matrix4x4.Identity; platform.Low = new Vector3(-4, 0, -4); platform.High = new Vector3(4, 2, 4);
        contacts.Items = new List<EventItem> { platform };
        var host = new Host(); var contact = new EventRuntime(); contact.Load(contacts, host);
        contact.Update(0, Vector3.Zero); Expect(Count(host.Ui, "touch") == 0, "object bounds alone do not fire mesh contact events"); host.Contacts.Add(0);
        contact.Update(0.016f, new Vector3(3.5f, 2.0001f, 0)); contact.Update(0.016f, new Vector3(3.5f, 2.0001f, 0));
        Expect(Count(host.Ui, "touch") == 1, "standing on solid object enters once despite contact skin");
        host.Contacts.Clear(); contact.Update(0.016f, new Vector3(3.5f, 2.05f, 0)); Expect(Count(host.Ui, "leave") == 1, "contact padding does not reach a separate surface"); host.Contacts.Add(0);
        contact.MoveBounds(0, new Vector3(10, 0, -4), new Vector3(18, 2, 4)); contact.Update(0.016f, new Vector3(17.5f, 2.0001f, 0));
        Expect(Count(host.Ui, "touch") == 2, "live translated/scaled bounds replace the load bounds");
        var debug = contact.GetDebugBounds();
        Expect(debug.Count == 1 && debug[0].InRange && Math.Abs(debug[0].High.X - 18.02f) < 0.0001f, "diagnostics expose the padded bounds actually tested");
        var (playerLow, playerHigh) = contact.PlayerBounds;
        Expect(Math.Abs(playerLow.Y - 2.0001f) < 0.0001f && Math.Abs(playerHigh.Y - 3.8001f) < 0.0001f, "diagnostics expose the actual avatar box");
        host.Contacts.Clear(); contact.Update(0.016f, new Vector3(30, 0, 0)); host.Contacts.Add(0);
        contact.Update(0.016f, new Vector3(16, 0, -1), new Vector3(19, 1, 1)); contact.Update(0.016f, new Vector3(16, 0, -1), new Vector3(19, 1, 1));
        Expect(Count(host.Ui, "touch") == 3, "vehicle proxy reaches a trigger beyond the avatar seat and enters only once");
        contact.GameplayState(0, -1, -1); contact.Update(0.016f, new Vector3(16, 0, -1), new Vector3(19, 1, 1));
        Expect(Count(host.Ui, "touch") == 3 && contact.GetDebugBounds()[0].Excluded, "mounted vehicle is excluded from its own touch triggers");

        var coin = new EventItem(); Item(coin, @"{""_id"":""coin"",""collectable"":true,""onCollectEvent"":""collected""}");
        coin.Local = coin.Parent = Matrix4x4.Identity; coin.Low = new Vector3(-1, 0, -1); coin.High = new Vector3(1, 1, 1);
        contacts.Items = new List<EventItem> { coin };
        contact.Load(contacts, host); contact.Update(0.016f, new Vector3(0, 1.0001f, 0));
        Expect(host.Collects == 1 && !contact.ItemActive(0), "automatic collection includes physical surface contact");
    }

    static void Guides()
    {
        // Out of the Trap's guides used the conversation itself as their enable event.
        var guides = World(@"{""events"":[""talk_first"",""talk_next"",""unlock""]}");
        foreach (var json in new[]
        {
            @"{""_id"":""next"",""name"":""Next mouse"",""collision"":""box"",""enableInteraction"":true,""interactionTime"":10,""interactionName"":""Talk"",""interactionEnableEvent"":""talk_next"",""interactionTriggers"":[""talk_next""],""wayPoint"":{""enable"":true,""event"":""talk_first"",""deactivateEvent"":""talk_next""}}",
            @"{""_id"":""first"",""name"":""Mr. Nibbles"",""collision"":""box"",""enableInteraction"":true,""interactionTime"":10,""interactionName"":""Talk"",""interactionEnableEvent"":""talk_first"",""interactionTriggers"":[""talk_first""],""wayPoint"":{""enable"":true,""event"":""on_start"",""deactivateEvent"":""talk_first""}}",
            @"{""_id"":""locked"",""name"":""Locked interaction"",""enableInteraction"":true,""interactionName"":""Locked"",""interactionEnableEvent"":""unlock"",""interactionTriggers"":[""talk_first""],""wayPoint"":{""enable"":true,""event"":""on_start"",""deactivateEvent"":""talk_first""}}",
        })
        {
            var guide = new EventItem(); Expect(Item(guide, json), "guide parsed");
            guide.Local = guide.Parent = Matrix4x4.Identity; guide.Low = new Vector3(-1, 0, -1); guide.High = new Vector3(1, 2, 1);
            guides.Items.Add(guide);
        }
        Expect(guides.Items[0].EnableEvent == "talk_first" && guides.Items[1].EnableEvent == "on_start", "self-gated conversation follows its waypoint activation");
        Expect(guides.Items[2].EnableEvent == "unlock", "an explicit external lock is preserved");
        var host = new Host(); var g = new EventRuntime(); g.Load(guides, host); g.Update(0, Vector3.Zero);
        Expect(g.InteractionItem < 0, "guides stay locked before activation");
        g.Start(); g.Update(0, Vector3.Zero); Expect(g.InteractionItem == 1 && host.PromptText == "Talk", "first mouse is usable on start while later guides stay locked");
        g.Interact(); Expect(Count(host.Ui, "talk_first") == 1, "F starts the first conversation");
        Expect(g.Waypoint == 0, "retiring a guide does not erase the next waypoint regardless of item order");
        g.Update(0.016f, Vector3.Zero); Expect(g.InteractionItem == 0, "the next conversation becomes available after the first");
        g.Interact(); Expect(Count(host.Ui, "talk_next") == 1, "F starts the next conversation");
        g.Reset(); g.Update(0, Vector3.Zero); Expect(g.InteractionItem < 0, "reset restores guide gates");
    }

    static void ItemPairs()
    {
        // Item-to-item triggers: a crate (named by its source id) meeting a zone fires the crate's enter once, then leave.
        var pair = World(@"{""events"":[""in_zone"",""out_zone"",""drop_zone""]}");
        var crate = new EventItem(); var zone = new EventItem();
        Expect(Item(crate, @"{""_id"":""crate"",""itemInteractionTriggers"":[{""id"":""zone-src"",""onEnterEvent"":""in_zone"",""onLeaveEvent"":""out_zone""}]}"), "item triggers script their owner");
        Expect(Item(zone, @"{""_id"":""zone"",""sourceWorldItemId"":""zone-src"",""disappearEvent"":""drop_zone""}"), "zone parsed");
        crate.Low = new Vector3(0, 0, 0); crate.High = new Vector3(1, 1, 1); zone.Low = new Vector3(5, 0, 0); zone.High = new Vector3(7, 1, 1);
        foreach (var item in new[] { crate, zone }) { item.Local = item.Parent = Matrix4x4.Identity; item.States = new List<Matrix4x4> { Matrix4x4.Identity }; }
        pair.Items = new List<EventItem> { crate, zone };
        var host = new Host(); var pairs = new EventRuntime(); pairs.Load(pair, host); pairs.Start();
        var far = new Vector3(100, 0, 100);
        pairs.Update(0.016f, far); Expect(Count(host.Ui, "in_zone") == 0, "apart: no enter");
        pairs.MoveBounds(0, new Vector3(5.5f, 0, 0), new Vector3(6.5f, 1, 1)); pairs.Update(0.016f, far); pairs.Update(0.016f, far);
        Expect(Count(host.Ui, "in_zone") == 1, "boxes meeting fire On Enter once");
        pairs.MoveBounds(0, new Vector3(0, 0, 0), new Vector3(1, 1, 1)); pairs.Update(0.016f, far);
        Expect(Count(host.Ui, "out_zone") == 1, "boxes parting fire On Leave");
        pairs.TriggerEvent("drop_zone"); pairs.MoveBounds(0, new Vector3(5.5f, 0, 0), new Vector3(6.5f, 1, 1)); pairs.Update(0.016f, far);
        Expect(Count(host.Ui, "in_zone") == 1, "a removed target never triggers");
    }

    static void Paths()
    {
        // Follow-path transition: 5 m/s along a straight 10 m spline, +Z turned along it, finished once at the end.
        var paths = World(@"{""events"":[""arrived""]}");
        var curve = SplineCurve.Parse(JsonNode.Parse(@"{""points"":[{""x"":0,""y"":0,""z"":0},{""x"":10,""y"":0,""z"":0}]}"));
        curve.Measure();
        paths.Paths["road"] = new EventPath { Curve = curve, World = Matrix4x4.Identity };
        var cart = new EventItem();
        Item(cart, @"{""_id"":""cart"",""transitions"":[{""mode"":""path"",""pathItemId"":""road"",""pathSpeed"":5,""playmode"":""auto"",""transitionFinishedEvent"":""arrived""}]}");
        cart.Local = cart.Parent = Matrix4x4.Identity; cart.States.Insert(0, Matrix4x4.Identity); paths.Items = new List<EventItem> { cart };
        var host = new Host(); var runner = new EventRuntime(); runner.Load(paths, host); runner.Start();
        var far = new Vector3(100, 0, 100);
        for (int f = 0; f < 60; f++) runner.Update(1.0f / 60, far);
        var at = host.Moved[0];
        Expect(Math.Abs(at.M41 - 5) < 0.05f && Math.Abs(at.M43) < 0.01f, "path transition travels at its speed");
        Expect(Math.Abs(at.M31 - 1) < 0.01f, "aligned path transition faces along the curve (+Z on the tangent)");
        for (int f = 0; f < 90; f++) runner.Update(1.0f / 60, far);
        Expect(Count(host.Ui, "arrived") == 1 && Math.Abs(host.Moved[0].M41 - 10) < 0.01f, "path transition ends at the far end and fires its finished event once");
    }

    static void SceneCarry()
    {
        // Scene switch carry: variables and delayed world events survive a reload; resuming skips on_start.
        var w = World(@"{""events"":[""later"",""tick""],""variables"":[{""name"":""keys"",""dataType"":""number"",""initialValue"":0}],
              ""eventBroadcasters"":[{""startEvent"":""on_start"",""actionType"":""Change Variable"",""actionIndex"":1,""targetVariable"":{""name"":""keys""},""values"":[{""type"":""Value"",""value"":""1""}],""arthematicOperators"":[""+""]},
                                   {""startEvent"":""tick"",""actionType"":""Trigger Events"",""actionIndex"":0,""triggerEvents"":[""later""],""delay"":1}]}");
        var before = new Host(); var old = new EventRuntime(); old.Load(w, before); old.Start(); old.TriggerEvent("tick");
        Expect(old.Variable("keys") != null && EventValue.Display(old.Variable("keys").Value) == "1", "on_start ran once");
        var carried = old.TakeCarry();
        var after = new Host(); var fresh = new EventRuntime(); fresh.Load(w, after); fresh.Restore(carried); fresh.Start(false);
        Expect(EventValue.Display(fresh.Variable("keys").Value) == "1" && after.Variables["keys"] == "1", "carried variable restored without a second on_start");
        fresh.Update(1.1f, new Vector3(100, 0, 100));
        Expect(Count(after.Ui, "later") == 1, "delayed world event carried across the switch");
    }

    static void Waypoints()
    {
        // Waypoints: Activate Waypoint marks its item, Deactivate clears it, reset forgets it.
        var marks = World(@"{""events"":[""go"",""stop""]}");
        var flag = new EventItem();
        Expect(Item(flag, @"{""_id"":""flag"",""wayPoint"":{""enable"":true,""event"":""go"",""deactivateEvent"":""stop""}}"), "waypoint scripts its item");
        flag.Local = flag.Parent = Matrix4x4.Identity; flag.States = new List<Matrix4x4> { Matrix4x4.Identity }; marks.Items = new List<EventItem> { flag };
        var host = new Host(); var marker = new EventRuntime(); marker.Load(marks, host); marker.Start();
        Expect(marker.Waypoint < 0, "no waypoint before its event");
        marker.TriggerEvent("go"); Expect(marker.Waypoint == 0, "activate marks the item");
        marker.TriggerEvent("stop"); Expect(marker.Waypoint < 0, "deactivate clears it");
        marker.TriggerEvent("go"); marker.Reset(); Expect(marker.Waypoint < 0, "reset clears the waypoint");
    }

    static void Extras()
    {
        // Additions over the native layer: sequenced cards, sound finished events, local scope isolation,
        // hidden items receiving later show events, and no duplicated listeners after reset.
        var w = World(@"{""events"":[""go"",""done"",""show"",""hide""]}");
        var speaker = new EventItem();
        Item(speaker, @"{""_id"":""speaker"",""name"":""Speaker"",""sounds"":[{""url"":""a.mp3""}],
            ""soundTriggers"":[{""playmode"":""event"",""event"":""go"",""soundIndex"":0,""sequencerAt"":0.5,""soundFinishedEvent"":""done""}]}");
        var ghost = new EventItem();
        Item(ghost, @"{""_id"":""ghost"",""name"":""Ghost"",""spawnEvent"":""show"",""disappearEvent"":""hide""}");
        foreach (var item in new[] { speaker, ghost }) { item.Local = item.Parent = Matrix4x4.Identity; item.Low = new Vector3(-1); item.High = new Vector3(1); }
        w.Items = new List<EventItem> { speaker, ghost };
        var host = new Host(); var r = new EventRuntime(); r.Load(w, host); r.Start();
        var far = new Vector3(100, 0, 100);
        host.Sounds.Clear();
        r.TriggerEvent("go");
        Expect(!host.Sounds.Any(s => s.play), "sequencerAt delays the sound card");
        r.Update(0.3f, far); r.TriggerEvent("go"); r.Update(0.3f, far);
        Expect(!host.Sounds.Any(s => s.play), "a re-trigger restarts the sequenced timer");
        r.Update(0.25f, far);
        Expect(host.Sounds.Count(s => s.play) == 1, "sequenced sound plays once");
        r.SoundTrackFinished(0, 0);
        Expect(Count(host.Ui, "done") == 1, "sound finished event (by track)");
        r.SoundTrackFinished(0, 1);
        Expect(Count(host.Ui, "done") == 1, "another track has no finished event");
        Expect(host.Visible[1] == false, "spawn-event item starts hidden");
        r.TriggerEvent("show"); Expect(host.Visible[1], "hidden item receives its show event");
        r.TriggerEvent("hide"); r.TriggerEvent("show"); Expect(host.Visible[1], "item can be hidden and shown again");
        r.Reset(); r.Start(); host.Ui.Clear();
        r.TriggerEvent("go"); r.Update(0.6f, far);
        Expect(host.Sounds.Count(s => s.play) == 2, "reset does not duplicate listeners");
    }

    static void CoordinateChecks()
    {
        // three.js column-major elements of a translation (1,2,3) and a 90 degree Y rotation.
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(Math.PI / 2));
        var three = Coordinates.Compose(new Vector3(1, 2, 3), q, new Vector3(2, 1, 1));
        var unity = Coordinates.ThreeToUnity(three);
        Coordinates.Decompose(unity, out var p, out var r, out var s);
        var expectedRotation = Coordinates.ThreeToUnityRotation(q);
        Expect(Vector3.Distance(p, new Vector3(-1, 2, 3)) < 1e-5f, "mirrored position");
        Expect(Math.Abs(Math.Abs(Quaternion.Dot(r, expectedRotation)) - 1) < 1e-5f, "mirrored rotation");
        Expect(Vector3.Distance(s, new Vector3(2, 1, 1)) < 1e-5f, "scale unchanged");
        // A point transformed in three space then mirrored equals the mirrored point transformed in Unity space.
        var point = new Vector3(0.3f, -0.7f, 1.1f);
        var a = Coordinates.ThreeToUnityPoint(Vector3.Transform(point, three));
        var b = Vector3.Transform(Coordinates.ThreeToUnityPoint(point), unity);
        Expect(Vector3.Distance(a, b) < 1e-5f, "basis change commutes with transforms");
        var elements = JsonNode.Parse("[1,0,0,0, 0,1,0,0, 0,0,1,0, 4,5,6,1]");
        Expect(Coordinates.TryMatrixFromThreeElements(elements, out var t) && t.Translation == new Vector3(4, 5, 6), "three elements load as row-vector matrix");
        // three Euler XYZ (x=90deg) maps +Y to +Z.
        var ex = Coordinates.EulerToQuaternion((float)(Math.PI / 2), 0, 0);
        var mapped = Vector3.Transform(Vector3.UnitY, ex);
        Expect(Vector3.Distance(mapped, Vector3.UnitZ) < 1e-5f, "Euler rotation direction");
    }
}
