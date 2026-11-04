using System;
using System.Collections.Generic;
using System.Numerics;

namespace Zimension.Core
{
    /// <summary>A world or item ("@") variable.</summary>
    public sealed class EventVariable
    {
        public string Name = "", Type = "";
        public EventValue Initial, Value;

        public EventVariable Clone() => new EventVariable { Name = Name, Type = Type, Initial = Initial, Value = Value };
        public EventValue Current => Value.IsDefined ? Value : Initial;

        /// <summary>Engine.normalizeVariableValue.</summary>
        public EventValue Normalize(EventValue v)
        {
            if (!v.IsDefined) return Initial;
            var type = (Type ?? "").ToLowerInvariant();
            if (type == "number")
            {
                double n = EventValue.ToNumber(v);
                if (JsonNode.IsFinite(n)) return EventValue.Of(n);
                double fallback = EventValue.ToNumber(Initial);
                return EventValue.Of(JsonNode.IsFinite(fallback) ? fallback : 0.0);
            }
            if (type == "boolean" || type == "bool")
            {
                if (v.Kind == EventValue.ValueKind.Boolean) return v;
                if (v.Kind == EventValue.ValueKind.Number) return EventValue.Of(v.Number != 0);
                var s = EventValue.Trim(v.Text).ToLowerInvariant();
                return EventValue.Of(s == "true" || s == "1");
            }
            return v;
        }

        /// <summary>A saved {name, dataType|type, initialValue[, value]} entry.</summary>
        public static EventVariable FromJson(JsonNode j, bool localValue)
        {
            var v = new EventVariable { Name = j.Str("name") };
            v.Type = j.Str("dataType");
            if (string.IsNullOrEmpty(v.Type)) v.Type = j.Str("type");
            v.Initial = EventValue.FromJson(j["initialValue"]);
            v.Value = localValue && j.Has("value") ? v.Normalize(EventValue.FromJson(j["value"])) : v.Initial;
            return v;
        }
    }

    public sealed class LogicCondition
    {
        public string Variable = "", Op = "", Other = "";
        public bool Literal = true;
        public EventValue Value;
    }

    /// <summary>One world.eventBroadcasters entry, classified as Engine.resolveEventBroadcasterType does.</summary>
    public sealed class LogicFlow
    {
        public enum FlowKind { TriggerEvents, ChangeVariable, CheckCondition, SwitchCase, Unsupported }
        public FlowKind Kind = FlowKind.Unsupported;
        public string Start = "", Owner = "", Name = "", ActionType = "";
        // Trigger Events
        public List<string> Events = new List<string>();
        public double Delay;
        public string Message = "", Link = "";
        // Change Variable
        public string Target = "", Source = "", Op = "";
        public bool Literal = true, HasValue;
        public EventValue Value;
        // Check Conditions
        public List<LogicCondition> Conditions = new List<LogicCondition>();
        public List<bool> OrJoins = new List<bool>();
        public string TrueEvent = "", FalseEvent = "";
        // Switch Case
        public string SwitchVariable = "", SwitchType = "", DefaultEvent = "";
        public List<KeyValuePair<EventValue, string>> Cases = new List<KeyValuePair<EventValue, string>>();
        /// <summary>The saved flow, for player-control and other host-run actions.</summary>
        public JsonNode Source_ = JsonNode.Undefined;
    }

    /// <summary>Animation, sound and transition cards on an item, and the world's sound cards.</summary>
    public sealed class EventCard
    {
        public string Playmode = "", Event = "", StopEvent = "", FinishedEvent = "";
        public float SequencerAt, Volume = 1;
        public int Sound;
        public bool Loop;
        public JsonNode Source = JsonNode.Undefined;

        internal static EventCard FromJson(JsonNode j, string stopKey, string finishedKey)
        {
            var c = new EventCard
            {
                Playmode = j.Str("playmode"),
                Event = j.Str("event"),
                StopEvent = j.Str(stopKey),
                FinishedEvent = j.Str(finishedKey),
                SequencerAt = Math.Max(0f, EventParsing.Seconds(j["sequencerAt"], 0)),
                Loop = j.Truthy("loop"),
                Volume = Math.Clamp(EventParsing.Seconds(j["volume"], 1), 0f, 100f),
                Sound = j["soundIndex"].IsNumber ? (int)j["soundIndex"].AsNumber : 0,
                Source = j,
            };
            return c;
        }
    }

    public sealed class EventTransition
    {
        public EventCard Card = new EventCard();
        public List<int> States = new List<int>();
        public List<float> Durations = new List<float>();
        public List<bool> Smooth = new List<bool>();
        /// <summary>"Reverse to start": a ping-pong.</summary>
        public bool Reverse;
        // Follow-path mode (Engine.startPathTransition).
        public bool Path, Align = true, PathReverse;
        public string PathItem = "";
        public float PathSpeed = 2;
    }

    /// <summary>A spline item's curve and placement, for follow-path transitions.</summary>
    public sealed class EventPath
    {
        public SplineCurve Curve;
        public Matrix4x4 World = Matrix4x4.Identity;
    }

    /// <summary>itemInteractionTriggers: this item's box meeting another item's box.</summary>
    public sealed class ItemTrigger
    {
        public string Target = "", EnterEvent = "", LeaveEvent = "";
    }

    public sealed class EventItem
    {
        public string Id = "", SourceId = "", Name = "", Collision = "";
        /// <summary>Item transform is Local * Parent (row-vector), in whatever space the host works in.</summary>
        public Matrix4x4 Local = Matrix4x4.Identity, Parent = Matrix4x4.Identity;
        public Vector3 Low, High;
        /// <summary>Saved visibility (false: an invisible collider); Hidden: waits for its spawn event.</summary>
        public bool Visible = true, Hidden, Active = true;
        public string SpawnEvent = "", DisappearEvent = "", EnterEvent = "", LeaveEvent = "", CollectEvent = "";
        public string InteractionName = "", EnableEvent = "", DisableEvent = "";
        public bool Collectable, AutoPickup, Interaction, Inventory, Vehicle, Weapon, Pickable;
        public string VehicleLabel = "Enter Vehicle", ExitLabel = "Exit Vehicle";
        public float InteractionTime;
        public EventVariable Counter = new EventVariable();
        public List<string> InteractionTriggers = new List<string>();
        public List<ItemTrigger> ItemTriggers = new List<ItemTrigger>();
        public string WaypointEvent = "", WaypointDeactivateEvent = "";
        public List<EventVariable> Variables = new List<EventVariable>();
        /// <summary>[0] is the item's own transform.</summary>
        public List<Matrix4x4> States = new List<Matrix4x4>();
        public List<EventTransition> Transitions = new List<EventTransition>();
        public List<EventCard> Animations = new List<EventCard>();
        public List<EventCard> Sounds = new List<EventCard>();
        public List<string> SoundFiles = new List<string>();
        /// <summary>The saved item, for host features beyond the event layer.</summary>
        public JsonNode Source = JsonNode.Undefined;
    }

    public sealed class EventWorld
    {
        public List<string> Events = new List<string>();
        public Dictionary<string, EventPath> Paths = new Dictionary<string, EventPath>(StringComparer.Ordinal);
        public List<LogicFlow> Flows = new List<LogicFlow>();
        public List<EventVariable> Variables = new List<EventVariable>();
        public List<EventItem> Items = new List<EventItem>();
        public List<EventCard> Sounds = new List<EventCard>();
        public List<string> SoundFiles = new List<string>();
        /// <summary>Features the world uses that this layer does not run.</summary>
        public List<string> Unsupported = new List<string>();
        public bool Empty => Flows.Count == 0 && Items.Count == 0 && Sounds.Count == 0;
    }

    /// <summary>Saved world/item JSON to the event model (native Events.cpp parseEventWorld / parseEventItem).</summary>
    public static class EventParsing
    {
        internal static float Seconds(JsonNode j, float fallback)
        {
            if (j.IsNumber) { float n = (float)j.AsNumber; return float.IsNaN(n) || float.IsInfinity(n) ? fallback : n; }
            if (j.IsString)
            {
                double n = EventValue.ParseFloat(EventValue.Of(j.AsString));
                return JsonNode.IsFinite(n) ? (float)n : fallback;
            }
            return fallback;
        }

        static string Trim(string s) => EventValue.Trim(s);

        public static EventWorld ParseWorld(JsonNode j)
        {
            if (j == null || !j.IsObject) throw new FormatException("World event data is not a JSON object");
            var world = new EventWorld();
            foreach (var e in j.Strings("events")) world.Events.Add(Trim(e));
            foreach (var v in j["variables"].Items)
                if (v.IsObject && !string.IsNullOrEmpty(v.Str("name"))) world.Variables.Add(EventVariable.FromJson(v, false));
            foreach (var f in j["eventBroadcasters"].Items)
            {
                if (!f.IsObject) continue;
                world.Flows.Add(ParseFlow(f, world.Unsupported));
            }
            foreach (var s in j["soundTriggers"].Items)
                if (s.IsObject) world.Sounds.Add(EventCard.FromJson(s, "soundStopEvent", "soundFinishedEvent"));
            foreach (var s in j["sounds"].Items) world.SoundFiles.Add(s.IsObject ? s.Str("url") : "");
            void Uses(string key, string what)
            {
                var v = j[key];
                if ((v.IsArray || v.IsObject) && v.Count > 0) world.Unsupported.Add(what);
            }
            Uses("animationSequences", "Animation sequences");
            Uses("environmentEffects", "Environment effects");
            Uses("objectives", "Objectives");
            return world;
        }

        public static LogicFlow ParseFlow(JsonNode f, List<string> unsupported = null)
        {
            var flow = new LogicFlow
            {
                Start = Trim(f.Str("startEvent")),
                Owner = f.Str("ownerItemId"),
                Name = f.Str("name"),
                ActionType = f.Str("actionType"),
                Source_ = f,
            };
            int index = f["actionIndex"].IsNumber ? (int)f["actionIndex"].AsNumber : -1;
            var actionType = flow.ActionType;
            if (actionType == "Switch Case") flow.Kind = LogicFlow.FlowKind.SwitchCase;
            else if (actionType == "Switch Control" || actionType == "Return to Previous Control" || index == 3 || index == 4) flow.Kind = LogicFlow.FlowKind.Unsupported;
            else if (index == 0) flow.Kind = LogicFlow.FlowKind.TriggerEvents;
            else if (index == 1) flow.Kind = LogicFlow.FlowKind.ChangeVariable;
            else if (index == 2) flow.Kind = LogicFlow.FlowKind.CheckCondition;
            else if (actionType == "Trigger Events") flow.Kind = LogicFlow.FlowKind.TriggerEvents;
            else if (actionType == "Change Variable") flow.Kind = LogicFlow.FlowKind.ChangeVariable;
            else if (actionType == "Check Condition" || actionType == "Check Conditions") flow.Kind = LogicFlow.FlowKind.CheckCondition;

            flow.Events = f.Strings("triggerEvents");
            // A delay only applies when it is a number (Engine.executeEventTriggers).
            if (f["delay"].IsNumber) flow.Delay = Math.Max(0.0, f["delay"].AsNumber);
            flow.Message = f.Str("showMessage");
            flow.Link = f.Str("link");
            flow.Target = f.Obj("targetVariable").Str("name");
            var values = f["values"];
            if (values.IsArray && values.Count > 0 && values[0].IsObject)
            {
                flow.HasValue = true;
                flow.Literal = values[0].Str("type") == "Value";
                flow.Value = EventValue.FromJson(values[0]["value"]);
                flow.Source = values[0].Obj("variable").Str("name");
            }
            var ops = f["arthematicOperators"];
            if (ops.IsArray && ops.Count > 0 && ops[0].IsString) flow.Op = ops[0].AsString;
            foreach (var c in f["conditions"].Items)
            {
                if (!c.IsObject) { flow.Conditions.Add(new LogicCondition()); continue; }
                flow.Conditions.Add(new LogicCondition
                {
                    Variable = c.Obj("targetVariable").Str("name"),
                    Op = c.Str("logicOperator"),
                    Literal = c.Str("variableValueMode") == "Value",
                    Value = EventValue.FromJson(c["variableValue"]),
                    Other = c.Obj("existingVariableValue").Str("name"),
                });
            }
            var joins = f["conditionOperators"];
            if (joins.IsArray)
                foreach (var op in joins.Items) flow.OrJoins.Add(op.IsString && op.AsString == "OR");
            else
                for (int i = 1; i < flow.Conditions.Count; i++) flow.OrJoins.Add(f.Str("conditionJoinOperator") == "OR");
            flow.TrueEvent = f.Str("trueEvent");
            flow.FalseEvent = f.Str("falseEvent");
            var sv = f.Obj("switchVariable");
            flow.SwitchVariable = sv.Str("name");
            flow.SwitchType = sv.Str("dataType").ToLowerInvariant();
            if (string.IsNullOrEmpty(flow.SwitchType)) flow.SwitchType = sv.Str("type").ToLowerInvariant();
            foreach (var c in f["cases"].Items)
                if (c.IsObject) flow.Cases.Add(new KeyValuePair<EventValue, string>(EventValue.FromJson(c["value"]), c.Str("event")));
            flow.DefaultEvent = f.Str("defaultEvent");
            if (flow.Kind == LogicFlow.FlowKind.Unsupported)
                unsupported?.Add($"Logic flow '{flow.Name}' ({(string.IsNullOrEmpty(actionType) ? "player control" : actionType)})");
            return flow;
        }

        /// <summary>The item an itemInteractionTriggers entry names (id, _id, targetWorldItemId or targetWorldItem._id/id).</summary>
        public static string ItemTriggerTarget(JsonNode t)
        {
            if (t == null || !t.IsObject) return "";
            string Id(JsonNode v, string key)
            {
                var m = v[key];
                return m.IsString ? m.AsString : m.IsNumber ? EventValue.DisplayNumber(m.AsNumber) : "";
            }
            foreach (var key in new[] { "id", "_id", "targetWorldItemId" })
            {
                var v = Id(t, key);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            var target = t["targetWorldItem"];
            foreach (var key in new[] { "_id", "id" })
            {
                var v = Id(target, key);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return "";
        }

        /// <summary>
        /// A saved world item; returns false when it has no event behaviour. Placement (Local,
        /// Parent, bounds) is left for the loader. States parsed here are in three space; the Unity
        /// importer replaces them with converted poses. <paramref name="owners"/> are item ids
        /// owning logic flows.
        /// </summary>
        public static bool ParseItem(EventItem item, JsonNode j, ICollection<string> owners)
        {
            if (j == null || !j.IsObject) return false;
            item.Source = j;
            item.Id = j.Str("_id");
            item.SourceId = j.Str("sourceWorldItemId");
            item.Name = j.Str("name");
            item.Collision = j.Str("collision");
            item.Active = !j["active"].IsBool || j["active"].AsBool;
            item.Visible = !(j["visible"].IsBool && !j["visible"].AsBool);
            item.SpawnEvent = j.Str("spawnEvent");
            item.DisappearEvent = j.Str("disappearEvent");
            item.Hidden = j.Str("spawnType") == "event" || !string.IsNullOrEmpty(item.SpawnEvent);
            item.EnterEvent = j.Str("onEnterRangeEvent");
            item.LeaveEvent = j.Str("onLeaveRangeEvent");
            item.Collectable = j.Truthy("collectable");
            var vehicle = j.Obj("vehicleSettings");
            item.Vehicle = vehicle.Truthy("enable");
            item.Weapon = j.Obj("fireWeaponSettings").Truthy("enable");
            item.Pickable = j.Truthy("pickable");
            var label = Trim(vehicle.Str("interactionLabel"));
            if (label.Length > 0) item.VehicleLabel = label;
            label = Trim(vehicle.Str("exitLabel"));
            if (label.Length > 0) item.ExitLabel = label;
            var collect = j.Obj("collectSettings");
            item.AutoPickup = collect.IsObject && collect.Has("autoPickup") ? collect.Truthy("autoPickup") : item.Collectable;
            item.Inventory = collect.IsObject && collect.Str("collectType") == "inventory";
            item.CollectEvent = j.Str("onCollectEvent");
            var counter = j.Obj("collectCountVariable");
            if (counter.IsObject && !string.IsNullOrEmpty(counter.Str("name"))) item.Counter = EventVariable.FromJson(counter, true);
            item.Interaction = j.Truthy("enableInteraction");
            if (item.Interaction)
            {
                item.InteractionName = j.Str("interactionName");
                item.InteractionTriggers = j.Strings("interactionTriggers");
                item.InteractionTime = Math.Max(0f, Seconds(j["interactionTime"], 0));
                item.EnableEvent = j.Str("interactionEnableEvent");
                item.DisableEvent = j.Str("interactionDisableEvent");
            }
            foreach (var v in j["variables"].Items)
                if (v.IsObject && !string.IsNullOrEmpty(v.Str("name"))) item.Variables.Add(EventVariable.FromJson(v, true));
            var w = j.Obj("wayPoint");
            if (w.Truthy("enable")) { item.WaypointEvent = w.Str("event"); item.WaypointDeactivateEvent = w.Str("deactivateEvent"); }
            // Legacy guided NPCs can name their own conversation as the enable event, leaving no
            // way to start it. Their waypoint already supplies the preceding event: use that gate
            // while preserving ordinary explicit interaction locks.
            if (item.Interaction && item.EnableEvent.Length > 0 && item.WaypointEvent.Length > 0 &&
                item.EnableEvent != item.WaypointEvent && item.EnableEvent == item.WaypointDeactivateEvent &&
                item.InteractionTriggers.Contains(item.EnableEvent))
                item.EnableEvent = item.WaypointEvent;
            {   // registerWorldItemInteractionTriggers: itemInteractionTriggers, or the older collisionWithItemList.
                var list = j["itemInteractionTriggers"].IsArray && j["itemInteractionTriggers"].Count > 0 ? j["itemInteractionTriggers"] : j["collisionWithItemList"];
                foreach (var t in list.Items)
                {
                    if (!t.IsObject) continue;
                    var target = ItemTriggerTarget(t);
                    if (target.Length > 0) item.ItemTriggers.Add(new ItemTrigger { Target = target, EnterEvent = t.Str("onEnterEvent"), LeaveEvent = t.Str("onLeaveEvent") });
                }
            }
            foreach (var s in j["states"].Items)
            {
                if (!s.IsObject) continue;
                item.States.Add(StateMatrix(s)); // the loader puts the base state in front
            }
            foreach (var t in j["transitions"].Items)
            {
                if (!t.IsObject) continue;
                var transition = new EventTransition { Card = EventCard.FromJson(t, "transitionStopEvent", "transitionFinishedEvent"), Reverse = t.Truthy("reset") };
                if (t.Str("mode") == "path")
                {
                    transition.Path = true;
                    transition.PathItem = t.Str("pathItemId");
                    transition.PathSpeed = Math.Max(0.01f, Seconds(t["pathSpeed"], 2));
                    if (!(transition.PathSpeed > 0.01f)) transition.PathSpeed = 2;
                    transition.Align = !(t["alignToPath"].IsBool && !t["alignToPath"].AsBool);
                    transition.PathReverse = t.Truthy("pathReverse");
                    item.Transitions.Add(transition);
                    continue;
                }
                foreach (var step in t["steps"].Items)
                {
                    if (!step.IsObject) continue;
                    var index = step["stateIndex"];
                    transition.States.Add(index.IsNumber ? Math.Max(0, (int)index.AsNumber) : 0);
                    transition.Durations.Add(Seconds(step["duration"], 1));
                    transition.Smooth.Add(step.Str("movement") == "Smooth");
                }
                item.Transitions.Add(transition);
            }
            bool animationEvents = false;
            if (j.Truthy("allowAnimations"))
            {
                foreach (var a in j["animations"].Items)
                {
                    var c = a.IsObject ? EventCard.FromJson(a, "animationStopEvent", "animationFinishedEvent") : new EventCard();
                    if (c.Playmode == "event" && Trim(c.Event) == "on_start") c.Playmode = "auto"; // started with the Play-on-Start cards
                    animationEvents = animationEvents || c.Playmode == "event" || c.Playmode == "interaction" || c.StopEvent.Length > 0 || c.FinishedEvent.Length > 0;
                    item.Animations.Add(c);
                }
            }
            foreach (var s in j["soundTriggers"].Items)
                if (s.IsObject) item.Sounds.Add(EventCard.FromJson(s, "soundStopEvent", "soundFinishedEvent"));
            foreach (var s in j["sounds"].Items) item.SoundFiles.Add(s.IsObject ? s.Str("url") : "");
            bool owner = owners != null && (owners.Contains(item.Id) || (item.SourceId.Length > 0 && owners.Contains(item.SourceId)));
            return owner || item.Hidden || item.DisappearEvent.Length > 0 || item.EnterEvent.Length > 0 || item.LeaveEvent.Length > 0 || item.Collectable ||
                   item.Interaction || item.Vehicle || item.Weapon || item.Pickable || item.Transitions.Count > 0 || animationEvents || item.Sounds.Count > 0 ||
                   item.Variables.Count > 0 || item.ItemTriggers.Count > 0 || item.WaypointEvent.Length > 0 || item.WaypointDeactivateEvent.Length > 0;
        }

        /// <summary>A saved transition state in three space: its matrix, or legacy position/rotation/scale.</summary>
        public static Matrix4x4 StateMatrix(JsonNode s)
        {
            if (Coordinates.TryMatrixFromThreeElements(s["transformation"], out var m)) return m;
            // Legacy states: centimetre-ish editor units, as WorldItem.setUpStates converts them.
            var p = s["position"]; var r = s["rotation"]; var sc = s["scale"];
            float C(JsonNode v, string key)
            {
                var n = v[key];
                if (n.IsUndefined) n = v["_" + key];
                return n.IsNumber ? (float)n.AsNumber : 0f;
            }
            var rotation = Coordinates.EulerToQuaternion(C(r, "x"), C(r, "y"), C(r, "z"));
            return Coordinates.Compose(
                new Vector3(C(p, "x"), C(p, "y"), C(p, "z")) * (0.01f / 2.2f),
                rotation,
                new Vector3(C(sc, "x"), C(sc, "y"), C(sc, "z")) / 2.2f);
        }
    }
}
