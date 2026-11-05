// The Unity-native conversion: samples/event-fixture's events, flows and cards become a C# logic
// script whose methods do what the event runtime does (checked by reading the generated code), and
// a world with "@" events, delays and conditions produces the expected structure.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zimension.Core;
using Zimension.Editor.Conversion;

static class GeneratorChecks
{
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

    static LogicOutput Generate(JsonNode master)
    {
        var world = EventParsing.ParseWorld(master.Obj("world"));
        var owners = new HashSet<string>(world.Flows.Select(f => f.Owner).Where(o => o.Length > 0));
        foreach (var json in master["worldItems"].Items)
        {
            var item = new EventItem();
            if (EventParsing.ParseItem(item, json, owners)) world.Items.Add(item);
        }
        return LogicGenerator.Generate(new LogicInput { World = world, WorldName = master.Obj("world").Str("name"), ClassName = "FixtureLogic" });
    }

    static int Count(string text, string part) { int n = 0, at = 0; while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { n++; at += part.Length; } return n; }

    public static void Run(Action<bool, string> expect)
    {
        expect(LogicGenerator.Pascal("open_door") == "OpenDoor" && LogicGenerator.Pascal("zombie attack") == "ZombieAttack" && LogicGenerator.Pascal("3d") == "_3d", "event names become PascalCase methods");
        expect(LogicGenerator.Camel("Scifi Tube Specimen_4") == "scifiTubeSpecimen4" && LogicGenerator.Camel("class") == "@class", "item names become camelCase fields");

        var path = FindFixture();
        if (path != null)
        {
            var output = Generate(JsonNode.Parse(File.ReadAllText(path)));
            var code = output.Code;
            expect(code.Contains("public partial class FixtureLogic : ZimensionLogic"), "generator: partial class on ZimensionLogic");
            expect(code.Contains("protected override void OnStart()"), "generator: on_start becomes OnStart");
            expect(code.Contains("public void OpenDoor()"), "generator: world events become public methods");
            expect(code.Contains(".onInteract, ") && code.Contains("On("), "generator: the interaction is connected in Awake");
            expect(code.Contains("After(1f, () =>"), "generator: the delayed flow becomes After(seconds)");
            expect(code.Contains("PlayTransition("), "generator: transition cards play the transition Animator");
            expect(code.Contains(".onCollected, "), "generator: the collectable is connected");
            expect(output.Fields.Any(f => f.kind == "Collectable") && output.Fields.Any(f => f.kind == "TransitionAnimator"), "generator: field bindings for the converter");
            expect(Count(code, "{") == Count(code, "}"), "generator: balanced braces");
            expect(output.UserCode.Contains("partial void OnAwake()"), "generator: the user half implements OnAwake");
        }

        // A world with item-scoped events, delays, conditions, switch and arithmetic.
        var world = JsonNode.Parse(@"{""world"":{""name"":""Checks"",""events"":[""start"",""win"",""lose"",""pick""],
            ""variables"":[{""name"":""score"",""dataType"":""number"",""initialValue"":0},{""name"":""door_open"",""dataType"":""boolean"",""initialValue"":false},{""name"":""mode"",""dataType"":""string"",""initialValue"":""easy""}],
            ""eventBroadcasters"":[
              {""startEvent"":""start"",""actionIndex"":1,""targetVariable"":{""name"":""score""},""values"":[{""type"":""Value"",""value"":5}],""arthematicOperators"":[""+""]},
              {""startEvent"":""start"",""actionIndex"":2,""conditions"":[{""targetVariable"":{""name"":""score""},""logicOperator"":"">="",""variableValue"":""5"",""variableValueMode"":""Value""},{""targetVariable"":{""name"":""door_open""},""logicOperator"":""="",""variableValue"":""true"",""variableValueMode"":""Value""}],""conditionOperators"":[""OR""],""trueEvent"":""win"",""falseEvent"":""lose""},
              {""startEvent"":""pick"",""actionType"":""Switch Case"",""switchVariable"":{""name"":""mode"",""dataType"":""string""},""cases"":[{""value"":""easy"",""event"":""win""}],""defaultEvent"":""lose""},
              {""startEvent"":""@tick"",""ownerItemId"":""a1"",""actionIndex"":0,""triggerEvents"":[""@tick"",""reset_player""],""delay"":2}]},
            ""worldItems"":[{""_id"":""a1"",""name"":""Clock"",""type"":""item"",""enableInteraction"":true,""interactionName"":""Wind"",""interactionTriggers"":[""@tick""]}]}");
        var o = Generate(world);
        var c = o.Code;
        expect(c.Contains("public float Score") && c.Contains("public bool DoorOpen") && c.Contains("public string Mode"), "generator: typed variable properties");
        expect(c.Contains("Score = Score + 5f;"), "generator: arithmetic change variable");
        expect(c.Contains("if (Score >= 5f || DoorOpen == true)"), "generator: conditions with OR");
        expect(c.Contains("if (Mode.Trim() == \"easy\")") && c.Contains("Lose();"), "generator: switch case with default");
        expect(c.Contains("public void TickOnClock()") && c.Contains("After(2f, () => { TickOnClock(); Builtin(\"reset_player\"); });"), "generator: item events, delay and built-ins");
        expect(c.Contains("On(clockInteraction.onInteract, clock, () => { TickOnClock(); });"), "generator: interaction raises the item's own event");
        expect(Count(c, "{") == Count(c, "}"), "generator: balanced braces (scoped world)");
    }
}
