using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Zimension.Core;

namespace Zimension.Editor.Conversion
{
    /// <summary>
    /// What the converter must put on one item so the generated logic can drive it. Built by
    /// <see cref="LogicGenerator"/>; the converter creates exactly these components, with these
    /// Animator state names, and the wiring step assigns the generated fields to them.
    /// </summary>
    public sealed class ItemPlan
    {
        public int Index;
        public EventItem Item;
        public string Id = "", Field = "";
        public bool Interactable, Zone, Collectable, Weapon, Vehicle, Health, Locals;
        /// <summary>Per animation card: the model Animator state it plays (null for procedural cards).</summary>
        public List<string> AnimationStates = new List<string>();
        /// <summary>Per animation card: true for a built-in procedural animation (a ProceduralMotion component).</summary>
        public List<bool> Procedural = new List<bool>();
        public string AnimationRest = "Rest";
        public bool ModelAnimator => AnimationStates.Any(s => s != null);
        /// <summary>Per transition: its state on the item's transition Animator.</summary>
        public List<string> TransitionStates = new List<string>();
        public string TransitionRest = "Rest";
        /// <summary>Per sound card: whether its source reports "finished".</summary>
        public List<bool> SoundFinished = new List<bool>();
        public bool AnimationEvents, TransitionEvents;
    }

    /// <summary>A generated [SerializeField] the wiring step assigns after Unity compiles the script.</summary>
    [Serializable]
    public sealed class FieldBinding
    {
        public string field;
        /// <summary>Item id, or "@game" / "@hud" / "@player" / "@worldsounds".</summary>
        public string itemId;
        /// <summary>Object, Interactable, PlayerZone, Collectable, Weapon, Vehicle, Health, ModelAnimator, ModelEvents, TransitionAnimator, TransitionEvents, Sound, SoundFinished, Procedural, Contact, WorldSound, WorldSoundFinished.</summary>
        public string kind;
        public int index;
    }

    public sealed class LogicInput
    {
        public EventWorld World;
        public string WorldName = "World", ClassName = "WorldLogic", Namespace = "ZimensionWorlds";
        /// <summary>Scene switches: event name to Unity scene name.</summary>
        public List<KeyValuePair<string, string>> SceneSwitches = new List<KeyValuePair<string, string>>();
        public string GeneratorVersion = "";
    }

    public sealed class LogicOutput
    {
        public string Code = "";
        public string UserCode = "";
        public List<ItemPlan> Items = new List<ItemPlan>();
        public List<FieldBinding> Fields = new List<FieldBinding>();
        /// <summary>Things that were converted approximately or left as TODOs.</summary>
        public List<string> Notes = new List<string>();
        /// <summary>World sound cards that report "finished".</summary>
        public List<bool> WorldSoundFinished = new List<bool>();
    }

    /// <summary>
    /// Turns a world's events, logic flows, variables and item cards into a readable C# MonoBehaviour
    /// (a partial class deriving from Zimension.Kit.ZimensionLogic). The event semantics follow the
    /// web engine (and EventRuntime, its tested port): an event runs its listeners in registration
    /// order (each item's sounds, spawn/remove, waypoint, interaction locks, animations, transitions,
    /// then world sounds, then logic flows); nested events run in place; "@" events and variables
    /// belong to the object in scope; built-in events go to the GameManager.
    /// </summary>
    public static class LogicGenerator
    {
        static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            "Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy", "OnGUI", "OnValidate", "Reset",
            "BeginPlay", "OnStart", "Enter", "Exit", "On", "RunIn", "After", "AfterOnce", "ClearDelays", "Show", "Hide", "Message", "OpenLink",
            "Builtin", "PlaySound", "StopSound", "PlayAnimation", "StopAnimation", "PlayTransition", "StopTransition", "PlayMotion", "StopMotion",
            "SetWaypoint", "ClearWaypoint", "Changed", "LocalNumber", "LocalBool", "LocalText", "SetLocal", "Num", "Bool", "Text", "Game", "Hud",
            "Player", "Scope", "VariableChanged", "CarryVariables", "RestoreCarried", "WriteVariables", "ReadVariables", "Connect", "OnAwake",
            "Invoke", "GetComponent", "Destroy", "print", "StartCoroutine", "StopCoroutine", "Equals", "GetHashCode", "ToString", "GetType",
            "MaxDepth", "name", "tag", "enabled", "transform", "gameObject", "isActiveAndEnabled", "useGUILayout", "hideFlags", "CancelInvoke",
        };

        static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default",
            "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto",
            "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out",
            "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
            "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "virtual", "void", "volatile", "while", "var", "value", "get", "set",
        };

        // ---- Names -------------------------------------------------------------------------------

        static IEnumerable<string> Words(string s)
        {
            var word = new StringBuilder();
            foreach (var ch in s ?? "")
            {
                if (char.IsLetterOrDigit(ch) && ch < 128) word.Append(ch);
                else if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
            }
            if (word.Length > 0) yield return word.ToString();
        }

        public static string Pascal(string s)
        {
            var sb = new StringBuilder();
            foreach (var w in Words(s)) sb.Append(char.ToUpperInvariant(w[0])).Append(w.Substring(1));
            var r = sb.ToString();
            if (r.Length == 0) r = "Unnamed";
            if (char.IsDigit(r[0])) r = "_" + r;
            return r;
        }

        public static string Camel(string s)
        {
            var p = Pascal(s);
            if (p[0] == '_') return p;
            var r = char.ToLowerInvariant(p[0]) + p.Substring(1);
            return Keywords.Contains(r) ? "@" + r : r;
        }

        sealed class Names
        {
            readonly HashSet<string> m_Used = new HashSet<string>(StringComparer.Ordinal);
            public Names() { foreach (var r in Reserved) m_Used.Add(r); foreach (var k in Keywords) m_Used.Add(k); }
            public string Take(string wanted)
            {
                var name = wanted.TrimStart('@');
                if (m_Used.Add(name)) return wanted.StartsWith("@") && Keywords.Contains(name) ? name + "_" : name;
                for (int i = 2; ; i++) if (m_Used.Add(name + i)) return name + i;
            }
            public void Reserve(string name) => m_Used.Add(name);
        }

        static string Literal(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var ch in s ?? "")
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4")); else sb.Append(ch); break;
                }
            }
            return sb.Append('"').ToString();
        }

        static string Float(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            return ((float)v).ToString("R", CultureInfo.InvariantCulture) + "f";
        }

        static string Comment(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("*/", "* /");

        // ---- Model -------------------------------------------------------------------------------

        enum VarType { Number, Boolean, Text }

        sealed class WorldVar
        {
            public string Name, Property, Field;
            public VarType Type;
            public EventValue Initial;
        }

        static VarType TypeOf(string type)
        {
            var t = (type ?? "").ToLowerInvariant();
            return t == "number" ? VarType.Number : t == "boolean" || t == "bool" ? VarType.Boolean : VarType.Text;
        }

        static string CsType(VarType t) => t == VarType.Number ? "float" : t == VarType.Boolean ? "bool" : "string";

        static string Typed(EventValue v, VarType t)
        {
            switch (t)
            {
                case VarType.Number:
                {
                    double n = EventValue.ParseFloat(v);
                    if (v.Kind == EventValue.ValueKind.Boolean) n = v.Boolean ? 1 : 0;
                    return Float(JsonNode.IsFinite(n) ? n : 0);
                }
                case VarType.Boolean:
                {
                    var b = EventValue.AsBoolean(v);
                    return b.Kind == EventValue.ValueKind.Boolean && b.Boolean ? "true" : "false";
                }
                default: return Literal(v.IsDefined ? EventValue.Display(v) : "");
            }
        }

        sealed class Gen
        {
            public LogicInput Input;
            public EventWorld World;
            public LogicOutput Output = new LogicOutput();
            public Names Names = new Names();
            public Dictionary<string, WorldVar> Vars = new Dictionary<string, WorldVar>(StringComparer.Ordinal);
            public List<WorldVar> VarOrder = new List<WorldVar>();
            public Dictionary<string, string> EventMethods = new Dictionary<string, string>(StringComparer.Ordinal);
            /// <summary>Global event to its listener statements, in registration order.</summary>
            public Dictionary<string, List<string>> Global = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            /// <summary>(item, "@name") to statements.</summary>
            public Dictionary<(int, string), List<string>> Local = new Dictionary<(int, string), List<string>>();
            public Dictionary<(int, string), string> LocalMethods = new Dictionary<(int, string), string>();
            public Dictionary<string, string> LocalDispatchers = new Dictionary<string, string>(StringComparer.Ordinal);
            public List<string> FlowMethods = new List<string>();
            public StringBuilder Flows = new StringBuilder();
            public List<string> Connect = new List<string>();
            public List<string> AutoStart = new List<string>();
            public Dictionary<int, ItemPlan> Plans = new Dictionary<int, ItemPlan>();
            public HashSet<string> Registered = new HashSet<string>(StringComparer.Ordinal);
            public List<string> WorldSoundFields = new List<string>();
            public List<string> FieldDecls = new List<string>();
        }

        static bool IsLocal(string name) => name.StartsWith("@", StringComparison.Ordinal);

        static void Add(Gen g, string rawName, int item, string statement)
        {
            var name = EventValue.Trim(rawName);
            if (name.Length == 0 || string.IsNullOrEmpty(statement)) return;
            if (IsLocal(name))
            {
                if (item < 0) return;
                if (!g.Local.TryGetValue((item, name), out var list)) g.Local[(item, name)] = list = new List<string>();
                list.Add(statement);
                return;
            }
            if (!g.Registered.Contains(name)) return; // Engine.addTriggerToEvent binds only the world's events
            if (!g.Global.TryGetValue(name, out var listeners)) g.Global[name] = listeners = new List<string>();
            listeners.Add(statement);
        }

        // ---- Entry point -------------------------------------------------------------------------

        public static LogicOutput Generate(LogicInput input)
        {
            var g = new Gen { Input = input, World = input.World ?? new EventWorld() };
            var world = g.World;
            foreach (var e in world.Events) if (e.Length > 0) g.Registered.Add(e);
            foreach (var e in EventRuntime.AlwaysRegistered) g.Registered.Add(e);

            // Variables: the world's, then collect counters the world does not define.
            void AddVar(EventVariable v)
            {
                if (v == null || string.IsNullOrEmpty(v.Name) || IsLocal(v.Name) || g.Vars.ContainsKey(v.Name)) return;
                var prop = g.Names.Take(Pascal(v.Name));
                var wv = new WorldVar { Name = v.Name, Property = prop, Field = "m_" + prop, Type = TypeOf(v.Type), Initial = v.Initial };
                g.Vars[v.Name] = wv; g.VarOrder.Add(wv);
            }
            foreach (var v in world.Variables) AddVar(v);
            foreach (var item in world.Items) if (item.Counter.Name.Length > 0) AddVar(new EventVariable { Name = item.Counter.Name, Type = "number", Initial = item.Counter.Initial });

            // Event methods for every world event (empty ones too: they are places to add code).
            foreach (var e in g.Registered)
            {
                if (e == "on_start" || EventRuntime.Builtins.Contains(e)) continue;
                g.EventMethods[e] = g.Names.Take(Pascal(e));
            }

            // Items: fields and listeners.
            for (int i = 0; i < world.Items.Count; i++) PlanItem(g, i);
            for (int c = 0; c < world.Sounds.Count; c++)
            {
                var card = world.Sounds[c];
                var field = g.Names.Take("worldSound" + c);
                g.WorldSoundFields.Add(field);
                g.FieldDecls.Add($"        [SerializeField] AudioSource {field};");
                g.Output.Fields.Add(new FieldBinding { field = field, itemId = "@worldsounds", kind = "WorldSound", index = c });
                g.Output.WorldSoundFinished.Add(false);
                if (card.Playmode == "event") Add(g, card.Event, -1, Sequenced(card, $"world:{c}", $"PlaySound({field}, false);"));
                Add(g, card.StopEvent, -1, $"StopSound({field});");
                if (card.Playmode == "auto") g.AutoStart.Add($"PlaySound({field}, false);");
            }
            // World sound "finished" (the last card on a track with a finished event names it).
            for (int c = 0; c < world.Sounds.Count; c++)
            {
                var track = world.Sounds[c].Sound;
                string finished = "";
                foreach (var other in world.Sounds) if (other.Sound == track && EventValue.Trim(other.FinishedEvent).Length > 0) finished = EventValue.Trim(other.FinishedEvent);
                if (finished.Length == 0) continue;
                g.Output.WorldSoundFinished[c] = true;
                var done = g.Names.Take("worldSoundDone" + c);
                g.FieldDecls.Add($"        [SerializeField] SoundFinished {done};");
                g.Output.Fields.Add(new FieldBinding { field = done, itemId = "@worldsounds", kind = "WorldSoundFinished", index = c });
                g.Connect.Add($"if ({done}) On({done}.onFinished, null, () => {{ {Calls(g, finished, -1)} }});");
            }

            // Logic flows.
            for (int f = 0; f < world.Flows.Count; f++)
            {
                var flow = world.Flows[f];
                int owner = -1;
                if (flow.Owner.Length > 0)
                {
                    owner = world.Items.FindIndex(it => it.Id == flow.Owner);
                    if (owner < 0) { g.Output.Notes.Add($"Logic flow '{flow.Name}' belongs to an item that is not in the world; skipped."); continue; }
                }
                var method = g.Names.Take("Flow" + (f + 1) + Pascal(string.IsNullOrEmpty(flow.Name) ? flow.Start : flow.Name));
                WriteFlow(g, flow, method, owner);
                var call = owner >= 0 ? $"RunIn({g.Plans[owner].Field}, {method});" : $"{method}();";
                Add(g, flow.Start, owner, call);
            }

            // Scene switches.
            foreach (var pair in input.SceneSwitches)
                if (!string.IsNullOrEmpty(pair.Key)) Add(g, pair.Key, -1, $"if (Game) Game.LoadScene({Literal(pair.Value)});");

            // Local dispatchers: an "@event" raised where the object is only known at run time.
            foreach (var key in g.Local.Keys)
            {
                g.LocalMethods[key] = g.Names.Take(Pascal(key.Item2) + "On" + Pascal(g.Plans[key.Item1].Field));
                if (!g.LocalDispatchers.ContainsKey(key.Item2)) g.LocalDispatchers[key.Item2] = g.Names.Take("Local" + Pascal(key.Item2));
            }

            Emit(g);
            return g.Output;
        }

        static string Sequenced(EventCard card, string key, string statement) =>
            card.SequencerAt > 0 ? $"AfterOnce({Literal(key)}, {Float(card.SequencerAt)}, () => {statement.TrimEnd(';')});" : statement;

        // ---- Items -------------------------------------------------------------------------------

        /// <summary>The model Animator's state per animation card (null for built-in procedural cards) and its resting state.</summary>
        public static List<string> AnimationStates(EventItem item, out string rest)
        {
            var states = new List<string>();
            var used = new HashSet<string>(StringComparer.Ordinal) { "Rest" };
            for (int c = 0; c < item.Animations.Count; c++)
            {
                var card = item.Animations[c];
                if (card.Source.Str("animationSource") == "default") { states.Add(null); continue; }
                var wanted = card.Source.Str("animationName");
                if (string.IsNullOrEmpty(wanted)) wanted = "Animation " + (int)card.Source.Num("animationIndex", c);
                var state = wanted;
                for (int k = 2; !used.Add(state); k++) state = $"{wanted} ({k})";
                states.Add(state);
            }
            rest = "Rest";
            for (int c = 0; c < item.Animations.Count; c++)
                if (states[c] != null && item.Animations[c].Playmode == "auto") { rest = states[c]; break; }
            return states;
        }

        /// <summary>The transition Animator's state per transition and its resting state.</summary>
        public static List<string> TransitionStates(EventItem item, out string rest)
        {
            var states = new List<string>();
            var used = new HashSet<string>(StringComparer.Ordinal) { "Rest" };
            for (int t = 0; t < item.Transitions.Count; t++)
            {
                var wanted = item.Transitions[t].Card.Source.Str("name");
                if (string.IsNullOrEmpty(wanted)) wanted = "Transition " + (t + 1);
                var state = wanted;
                for (int k = 2; !used.Add(state); k++) state = $"{wanted} ({k})";
                states.Add(state);
            }
            rest = "Rest";
            for (int t = 0; t < item.Transitions.Count; t++)
                if (item.Transitions[t].Card.Playmode == "auto") { rest = states[t]; break; }
            return states;
        }

        static void PlanItem(Gen g, int i)
        {
            var item = g.World.Items[i];
            var j = item.Source;
            var plan = new ItemPlan { Index = i, Item = item, Id = item.Id };
            var baseName = Camel(string.IsNullOrEmpty(item.Name) ? "item" : item.Name);
            plan.Field = g.Names.Take(baseName);
            g.Plans[i] = plan;
            g.Output.Items.Add(plan);
            string f = plan.Field;
            string interactionField = null, motionAnimator = null;
            var interactionBody = new List<string>();
            var motionFields = new Dictionary<int, string>();
            void Field(string type, string name, string kind, int index = 0)
            {
                g.FieldDecls.Add($"        [SerializeField] {type} {name};");
                g.Output.Fields.Add(new FieldBinding { field = name, itemId = item.Id, kind = kind, index = index });
            }
            g.FieldDecls.Add("");
            g.FieldDecls.Add($"        [Header({Literal(item.Name)})]");
            Field("GameObject", f, "Object");

            // Sounds: one AudioSource per sound card.
            for (int c = 0; c < item.Sounds.Count; c++)
            {
                var card = item.Sounds[c];
                var field = g.Names.Take(f + "Sound" + c);
                Field("AudioSource", field, "Sound", c);
                if (card.Playmode == "event") Add(g, card.Event, i, Sequenced(card, $"sound:{item.Id}:{c}", $"PlaySound({field});"));
                Add(g, card.StopEvent, i, $"StopSound({field});");
                if (card.Playmode == "auto" && item.Active && !item.Hidden) g.AutoStart.Add($"PlaySound({field});");
                plan.SoundFinished.Add(false);
            }
            for (int c = 0; c < item.Sounds.Count; c++)
            {
                var track = item.Sounds[c].Sound;
                string finished = "";
                foreach (var other in item.Sounds) if (other.Sound == track && EventValue.Trim(other.FinishedEvent).Length > 0) finished = EventValue.Trim(other.FinishedEvent);
                if (finished.Length == 0) continue;
                plan.SoundFinished[c] = true;
                var done = g.Names.Take(f + "SoundDone" + c);
                Field("SoundFinished", done, "SoundFinished", c);
                g.Connect.Add($"if ({done}) On({done}.onFinished, {f}, () => {{ {Calls(g, finished, i)} }});");
            }
            Add(g, item.SpawnEvent, i, $"Show({f});");
            Add(g, item.DisappearEvent, i, $"Hide({f});");
            Add(g, item.WaypointEvent, i, $"if ({f}) SetWaypoint({f}.transform);");
            Add(g, item.WaypointDeactivateEvent, i, $"if ({f}) ClearWaypoint({f}.transform);");

            if (item.Interaction)
            {
                plan.Interactable = true;
                var field = g.Names.Take(f + "Interaction");
                Field("Interactable", field, "Interactable");
                Add(g, item.EnableEvent, i, $"if ({field}) {field}.EnableInteraction();");
                Add(g, item.DisableEvent, i, $"if ({field}) {field}.DisableInteraction();");
                interactionField = field;
                foreach (var e in item.InteractionTriggers) { var call = Calls(g, e, i); if (call.Length > 0) interactionBody.Add(call); }
            }

            // Animations: model clips on the item's Animator, built-in ones as ProceduralMotion.
            string animator = null, animEvents = null;
            plan.AnimationStates = AnimationStates(item, out var animationRest);
            plan.AnimationRest = animationRest;
            foreach (var st in plan.AnimationStates) plan.Procedural.Add(st == null);
            if (plan.ModelAnimator)
            {
                animator = g.Names.Take(f + "Animator");
                Field("Animator", animator, "ModelAnimator");
            }
            if (item.Animations.Any(a => EventValue.Trim(a.FinishedEvent).Length > 0 && a.Source.Str("animationSource") != "default"))
            {
                plan.AnimationEvents = true;
                animEvents = g.Names.Take(f + "AnimationEvents");
                Field("AnimationEvents", animEvents, "ModelEvents");
            }
            int proceduralIndex = 0;
            for (int c = 0; c < item.Animations.Count; c++)
            {
                var card = item.Animations[c];
                if (plan.Procedural[c])
                {
                    var motion = g.Names.Take(f + "Motion" + c);
                    motionFields[c] = motion;
                    Field("ProceduralMotion", motion, "Procedural", proceduralIndex++);
                    if (card.Playmode == "event") Add(g, card.Event, i, Sequenced(card, $"anim:{item.Id}:{c}", $"PlayMotion({motion});"));
                    Add(g, card.StopEvent, i, $"StopMotion({motion});");
                    continue;
                }
                var state = plan.AnimationStates[c];
                if (card.Playmode == "event") Add(g, card.Event, i, Sequenced(card, $"anim:{item.Id}:{c}", $"PlayAnimation({animator}, {Literal(state)});"));
                Add(g, card.StopEvent, i, $"StopAnimation({animator}, {Literal(plan.AnimationRest)});");
                if (EventValue.Trim(card.FinishedEvent).Length > 0)
                    g.Connect.Add($"if ({animEvents}) On({animEvents}.For({Literal(state)}), {f}, () => {{ {Calls(g, card.FinishedEvent, i)} }});");
            }

            // Transitions: movement clips on a second Animator on the item itself.
            if (item.Transitions.Count > 0)
            {
                motionAnimator = g.Names.Take(f + "Transitions");
                Field("Animator", motionAnimator, "TransitionAnimator");
                plan.TransitionStates = TransitionStates(item, out var transitionRest);
                plan.TransitionRest = transitionRest;
                string motionEvents = null;
                if (item.Transitions.Any(t => EventValue.Trim(t.Card.FinishedEvent).Length > 0))
                {
                    plan.TransitionEvents = true;
                    motionEvents = g.Names.Take(f + "TransitionEvents");
                    Field("AnimationEvents", motionEvents, "TransitionEvents");
                }
                for (int t = 0; t < item.Transitions.Count; t++)
                {
                    var card = item.Transitions[t].Card;
                    var state = plan.TransitionStates[t];
                    if (card.Playmode == "event") Add(g, card.Event, i, Sequenced(card, $"move:{item.Id}:{t}", $"PlayTransition({motionAnimator}, {Literal(state)});"));
                    Add(g, card.StopEvent, i, $"StopTransition({motionAnimator});");
                    if (EventValue.Trim(card.FinishedEvent).Length > 0)
                        g.Connect.Add($"if ({motionEvents}) On({motionEvents}.For({Literal(state)}), {f}, () => {{ {Calls(g, card.FinishedEvent, i)} }});");
                }
            }

            if (item.EnterEvent.Length > 0 || item.LeaveEvent.Length > 0)
            {
                plan.Zone = true;
                var zone = g.Names.Take(f + "Zone");
                Field("PlayerZone", zone, "PlayerZone");
                if (item.EnterEvent.Length > 0) g.Connect.Add($"if ({zone}) On({zone}.onPlayerEnter, {f}, () => {{ {Calls(g, item.EnterEvent, i)} }});");
                if (item.LeaveEvent.Length > 0) g.Connect.Add($"if ({zone}) On({zone}.onPlayerExit, {f}, () => {{ {Calls(g, item.LeaveEvent, i)} }});");
            }

            if (item.Collectable)
            {
                plan.Collectable = true;
                var collect = g.Names.Take(f + "Collectable");
                Field("Collectable", collect, "Collectable");
                var body = new List<string>();
                // collectCountVariable: (value || 0) + 1.
                if (item.Counter.Name.Length > 0 && g.Vars.TryGetValue(item.Counter.Name, out var counter))
                    body.Add(counter.Type == VarType.Number ? $"{counter.Property} += 1;" : counter.Type == VarType.Boolean ? $"{counter.Property} = true;" : $"{counter.Property} = Text(Num({counter.Property}) + 1);");
                var call = Calls(g, item.CollectEvent, i);
                if (call.Length > 0) body.Add(call);
                if (body.Count > 0) g.Connect.Add($"if ({collect}) On({collect}.onCollected, {f}, () => {{ {string.Join(" ", body)} }});");
            }

            for (int t = 0; t < item.ItemTriggers.Count; t++)
            {
                var trigger = item.ItemTriggers[t];
                var contact = g.Names.Take(f + "Contact" + t);
                Field("ItemContact", contact, "Contact", t);
                if (EventValue.Trim(trigger.EnterEvent).Length > 0) g.Connect.Add($"if ({contact}) On({contact}.onTouch, {f}, () => {{ {Calls(g, trigger.EnterEvent, i)} }});");
                if (EventValue.Trim(trigger.LeaveEvent).Length > 0) g.Connect.Add($"if ({contact}) On({contact}.onSeparate, {f}, () => {{ {Calls(g, trigger.LeaveEvent, i)} }});");
            }

            if (item.Weapon)
            {
                plan.Weapon = true;
                var weapon = g.Names.Take(f + "Weapon");
                Field("Weapon", weapon, "Weapon");
                var w = j.Obj("fireWeaponSettings");
                if (w.Str("onPickupEvent").Length > 0) g.Connect.Add($"if ({weapon}) On({weapon}.onPickup, {f}, () => {{ {Calls(g, w.Str("onPickupEvent"), i)} }});");
                if (w.Str("onDropEvent").Length > 0) g.Connect.Add($"if ({weapon}) On({weapon}.onDrop, {f}, () => {{ {Calls(g, w.Str("onDropEvent"), i)} }});");
            }

            if (item.Vehicle)
            {
                plan.Vehicle = true;
                var vehicle = g.Names.Take(f + "Vehicle");
                Field("Vehicle", vehicle, "Vehicle");
                var v = j.Obj("vehicleSettings");
                if (v.Str("onEnterEvent").Length > 0) g.Connect.Add($"if ({vehicle}) On({vehicle}.onEnter, {f}, () => {{ {Calls(g, v.Str("onEnterEvent"), i)} }});");
                if (v.Str("onExitEvent").Length > 0) g.Connect.Add($"if ({vehicle}) On({vehicle}.onExit, {f}, () => {{ {Calls(g, v.Str("onExitEvent"), i)} }});");
            }

            var damage = j.Obj("damageSetting");
            if (damage.Truthy("enable"))
            {
                plan.Health = true;
                var health = g.Names.Take(f + "Health");
                Field("Health", health, "Health");
                if (damage.Str("onHit").Length > 0) g.Connect.Add($"if ({health}) On({health}.onHit, {f}, () => {{ {Calls(g, damage.Str("onHit"), i)} }});");
                if (damage.Str("onDie").Length > 0) g.Connect.Add($"if ({health}) On({health}.onDie, {f}, () => {{ {Calls(g, damage.Str("onDie"), i)} }});");
                Add(g, damage.Str("reduceHealthEvent"), i, $"if ({health}) {health}.Damage({Float(damage.Float("reduceHealthAmount", 10))});");
                Add(g, damage.Str("increaseHealthEvent"), i, $"if ({health}) {health}.Heal({Float(damage.Float("increaseHealthAmount", 10))});");
            }

            plan.Locals = item.Variables.Count > 0;

            // The interaction: its trigger events, then the cards that play "on interaction".
            if (interactionField != null)
            {
                for (int c = 0; c < item.Animations.Count; c++)
                {
                    if (item.Animations[c].Playmode != "interaction") continue;
                    interactionBody.Add(plan.Procedural[c] ? $"PlayMotion({motionFields[c]});" : $"PlayAnimation({animator}, {Literal(plan.AnimationStates[c])});");
                }
                for (int t = 0; t < item.Transitions.Count; t++)
                    if (item.Transitions[t].Card.Playmode == "interaction") interactionBody.Add($"PlayTransition({motionAnimator}, {Literal(plan.TransitionStates[t])});");
                for (int c = 0; c < item.Sounds.Count; c++)
                    if (item.Sounds[c].Playmode == "interaction") interactionBody.Add($"PlaySound({g.Output.Fields.First(x => x.itemId == item.Id && x.kind == "Sound" && x.index == c).field});");
                if (interactionBody.Count > 0)
                    g.Connect.Add($"if ({interactionField}) On({interactionField}.onInteract, {f}, () => {{ {string.Join(" ", interactionBody)} }});");
            }
        }

        // ---- Calls -------------------------------------------------------------------------------

        /// <summary>The statement raising an event from a context (item index, or -1 when the object is only known at run time).</summary>
        static string Calls(Gen g, string rawName, int context)
        {
            var name = EventValue.Trim(rawName);
            if (name.Length == 0) return "";
            if (IsLocal(name)) return $"__LOCAL__{context}__{name}__;";
            if (EventRuntime.Builtins.Contains(name)) return $"Builtin({Literal(name)});";
            if (name == "on_start") return "OnStart();";
            if (!g.Registered.Contains(name)) return $"/* \"{Comment(name)}\" is not one of the world's events */";
            return $"{g.EventMethods[name]}();";
        }

        /// <summary>Local calls are resolved after every item's listeners are known.</summary>
        static string ResolveLocals(Gen g, string code)
        {
            var sb = new StringBuilder();
            int at = 0;
            while (true)
            {
                int start = code.IndexOf("__LOCAL__", at, StringComparison.Ordinal);
                if (start < 0) { sb.Append(code, at, code.Length - at); break; }
                sb.Append(code, at, start - at);
                int mid = code.IndexOf("__", start + 9, StringComparison.Ordinal);
                int end = code.IndexOf("__;", mid + 2, StringComparison.Ordinal);
                int context = int.Parse(code.Substring(start + 9, mid - start - 9), CultureInfo.InvariantCulture);
                var name = code.Substring(mid + 2, end - mid - 2);
                if (context >= 0 && g.LocalMethods.TryGetValue((context, name), out var method)) sb.Append(method).Append("();");
                else if (context < 0 && g.LocalDispatchers.TryGetValue(name, out var dispatcher)) sb.Append(dispatcher).Append("();");
                else sb.Append($"/* \"{Comment(name)}\" has no listeners on this object */");
                at = end + 3;
            }
            return sb.ToString();
        }

        // ---- Variables ---------------------------------------------------------------------------

        sealed class VarRef
        {
            public string Read, Name;
            public VarType Type;
            public bool Local, Writable = true;
        }

        static VarRef Variable(Gen g, string name, int context)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (IsLocal(name))
            {
                EventVariable def = null;
                if (context >= 0) def = g.World.Items[context].Variables.FirstOrDefault(v => v.Name == name);
                if (def == null) def = g.World.Items.SelectMany(it => it.Variables).FirstOrDefault(v => v.Name == name);
                if (def == null) return null;
                var type = TypeOf(def.Type);
                var read = type == VarType.Number ? $"LocalNumber({Literal(name)})" : type == VarType.Boolean ? $"LocalBool({Literal(name)})" : $"LocalText({Literal(name)})";
                return new VarRef { Read = read, Name = name, Type = type, Local = true };
            }
            if (g.Vars.TryGetValue(name, out var wv)) return new VarRef { Read = wv.Property, Name = wv.Property, Type = wv.Type };
            if (name == "player_health") return new VarRef { Read = "(Player ? Mathf.Round(Player.health) : 0f)", Type = VarType.Number, Writable = false };
            return null;
        }

        static string Convert(string expression, VarType from, VarType to)
        {
            if (from == to) return expression;
            return to == VarType.Number ? $"Num({expression})" : to == VarType.Boolean ? $"Bool({expression})" : $"Text({expression})";
        }

        static string Assign(VarRef target, string value) =>
            target.Local ? $"SetLocal({Literal(target.Name)}, {value});" : $"{target.Name} = {value};";

        // ---- Flows -------------------------------------------------------------------------------

        static void WriteFlow(Gen g, LogicFlow flow, string method, int owner)
        {
            var body = new List<string>();
            int ctx = owner; // -1: the scope is whatever object raised the event
            switch (flow.Kind)
            {
                case LogicFlow.FlowKind.TriggerEvents:
                {
                    var calls = flow.Events.Select(e => Calls(g, e, ctx)).Where(c => c.Length > 0).ToList();
                    if (flow.Delay > 0 && calls.Count > 0) body.Add($"After({Float(flow.Delay)}, () => {{ {string.Join(" ", calls)} }});");
                    else body.AddRange(calls);
                    if (EventValue.Trim(flow.Link).Length > 0) body.Add($"OpenLink({Literal(EventValue.Trim(flow.Link))});");
                    if (EventValue.Trim(flow.Message).Length > 0) body.Add($"Message({Literal(EventValue.Trim(flow.Message))});");
                    break;
                }
                case LogicFlow.FlowKind.ChangeVariable:
                {
                    var target = Variable(g, flow.Target, ctx);
                    if (!flow.HasValue) { body.Add($"// '{Comment(flow.Name)}' has no value set in Zimension."); break; }
                    if (target == null || !target.Writable) { body.Add($"// Variable '{Comment(flow.Target)}' does not exist in this world."); g.Output.Notes.Add($"Logic flow '{flow.Name}' changes unknown variable '{flow.Target}'."); break; }
                    string value; VarType valueType;
                    if (flow.Literal) { value = Typed(flow.Value, target.Type == VarType.Number ? VarType.Number : target.Type); valueType = target.Type; }
                    else
                    {
                        var source = Variable(g, flow.Source, ctx);
                        if (source == null) { value = target.Type == VarType.Number ? "0f" : target.Type == VarType.Boolean ? "false" : "\"\""; valueType = target.Type; }
                        else { value = source.Read; valueType = source.Type; }
                    }
                    if (target.Type == VarType.Number)
                    {
                        var v = Convert(value, valueType, VarType.Number);
                        string expr;
                        switch (flow.Op ?? "")
                        {
                            case "+": expr = $"{target.Read} + {v}"; break;
                            case "-": expr = $"{target.Read} - {v}"; break;
                            case "*": case "x": case "X": case "×": case "Ã—": expr = $"{target.Read} * {v}"; break;
                            case "/": case "÷": case "Ã·": expr = $"{target.Read} / {v}"; break;
                            case "!": expr = $"({v} != 0 ? 0f : 1f)"; break;
                            case "=": expr = v; break;
                            default: expr = "0f"; body.Add($"// Zimension sets the number to 0 when no operator is chosen ('{Comment(flow.Op)}')."); break;
                        }
                        body.Add(Assign(target, expr));
                    }
                    else body.Add(Assign(target, Convert(value, valueType, target.Type)));
                    break;
                }
                case LogicFlow.FlowKind.CheckCondition:
                {
                    var terms = flow.Conditions.Select(c => Condition(g, c, ctx)).ToList();
                    // AND binds tighter than OR.
                    var groups = new List<List<string>> { new List<string>() };
                    for (int k = 0; k < terms.Count; k++)
                    {
                        groups[groups.Count - 1].Add(terms[k]);
                        if (k < flow.OrJoins.Count && flow.OrJoins[k] && k + 1 < terms.Count) groups.Add(new List<string>());
                    }
                    var condition = terms.Count == 0 ? "false" : string.Join(" || ", groups.Where(x => x.Count > 0).Select(x => x.Count > 1 && groups.Count > 1 ? "(" + string.Join(" && ", x) + ")" : string.Join(" && ", x)));
                    var yes = Calls(g, flow.TrueEvent, ctx); var no = Calls(g, flow.FalseEvent, ctx);
                    body.Add($"if ({condition})");
                    body.Add(yes.Length > 0 ? "    " + yes : "    { }");
                    if (no.Length > 0) { body.Add("else"); body.Add("    " + no); }
                    break;
                }
                case LogicFlow.FlowKind.SwitchCase:
                {
                    var v = Variable(g, flow.SwitchVariable, ctx);
                    if (v == null) { body.Add($"// Switch variable '{Comment(flow.SwitchVariable)}' does not exist in this world."); break; }
                    var type = flow.SwitchType == "number" ? VarType.Number : flow.SwitchType == "boolean" || flow.SwitchType == "bool" ? VarType.Boolean : VarType.Text;
                    var read = Convert(v.Read, v.Type, type);
                    if (type == VarType.Text) read = $"{read}.Trim()";
                    bool first = true;
                    foreach (var pair in flow.Cases)
                    {
                        var lit = type == VarType.Text ? Literal(EventValue.Trim(EventValue.Display(pair.Key))) : Typed(pair.Key, type);
                        var call = Calls(g, pair.Value, ctx);
                        body.Add($"{(first ? "if" : "else if")} ({read} == {lit})");
                        body.Add("    " + (call.Length > 0 ? call : "{ }"));
                        first = false;
                    }
                    var fallback = Calls(g, flow.DefaultEvent, ctx);
                    if (fallback.Length > 0) { if (!first) body.Add("else"); body.Add((first ? "" : "    ") + fallback); }
                    break;
                }
                default:
                    body.Add($"// TODO: '{Comment(flow.Name)}' ({Comment(string.IsNullOrEmpty(flow.ActionType) ? "player control" : flow.ActionType)}) switches the player's control mode; there is no Unity equivalent yet.");
                    body.Add($"Debug.LogWarning({Literal($"[Zimension] Logic flow '{flow.Name}' ({flow.ActionType}) is not converted yet.")}, this);");
                    g.Output.Notes.Add($"Logic flow '{flow.Name}' ({flow.ActionType}) is a TODO in the generated script.");
                    break;
            }
            var sb = g.Flows;
            sb.AppendLine();
            var summary = flow.Kind == LogicFlow.FlowKind.TriggerEvents ? "Trigger events" : flow.Kind == LogicFlow.FlowKind.ChangeVariable ? "Change variable" :
                flow.Kind == LogicFlow.FlowKind.CheckCondition ? "Check conditions" : flow.Kind == LogicFlow.FlowKind.SwitchCase ? "Switch case" : flow.ActionType;
            sb.AppendLine($"        /// <summary>Zimension logic \"{Comment(string.IsNullOrEmpty(flow.Name) ? flow.Start : flow.Name)}\": {summary}, on \"{Comment(flow.Start)}\"{(owner >= 0 ? $", owned by {Comment(g.World.Items[owner].Name)}" : "")}.</summary>");
            sb.AppendLine($"        void {method}()");
            sb.AppendLine("        {");
            foreach (var line in body) sb.AppendLine("            " + line);
            sb.AppendLine("        }");
        }

        static string Ops(string op)
        {
            switch (op)
            {
                case "=": case "==": case "===": return "==";
                case "!=": case "!==": return "!=";
                case "<": case ">": case "<=": case ">=": return op;
                default: return null;
            }
        }

        static string Condition(Gen g, LogicCondition c, int ctx)
        {
            var target = Variable(g, c.Variable, ctx);
            if (target == null) return "false";
            string value; VarType valueType;
            if (c.Literal) { value = null; valueType = target.Type; }
            else
            {
                var other = Variable(g, c.Other, ctx);
                if (other == null) return "false";
                value = other.Read; valueType = other.Type;
            }
            var op = Ops(c.Op);
            if (op == null) return "false";
            // An untyped (text) variable compared with a boolean-looking value compares as booleans.
            bool asBool = target.Type == VarType.Boolean || (c.Literal ? EventValue.LooksBoolean(c.Value) && target.Type == VarType.Text : valueType == VarType.Boolean);
            if (asBool)
            {
                if (op != "==" && op != "!=") return "false";
                var left = Convert(target.Read, target.Type, VarType.Boolean);
                var right = c.Literal ? Typed(c.Value, VarType.Boolean) : Convert(value, valueType, VarType.Boolean);
                return $"{left} {op} {right}";
            }
            if (target.Type == VarType.Number)
            {
                var right = c.Literal ? Typed(c.Value, VarType.Number) : Convert(value, valueType, VarType.Number);
                return $"{target.Read} {op} {right}";
            }
            var text = c.Literal ? Literal(c.Value.IsDefined ? EventValue.Display(c.Value) : "") : Convert(value, valueType, VarType.Text);
            if (op == "==" || op == "!=") return $"{target.Read} {op} {text}";
            return $"string.CompareOrdinal({target.Read}, {text}) {op} 0";
        }

        // ---- Output ------------------------------------------------------------------------------

        static void Emit(Gen g)
        {
            var input = g.Input;
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine($"// Converted from the Zimension world \"{Comment(input.WorldName)}\"{(input.GeneratorVersion.Length > 0 ? " by Zimension for Unity " + input.GeneratorVersion : "")}.");
            sb.AppendLine($"// Re-importing the world rewrites this file. Put your own code in {input.ClassName}.cs, the other half of this partial class.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("#pragma warning disable 0169, 0414, 0649 // scene references are assigned by the importer");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using Zimension.Kit;");
            sb.AppendLine();
            sb.AppendLine($"namespace {input.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine($"    /// The logic of \"{Comment(input.WorldName)}\": one method per Zimension event, a property per world variable,");
            sb.AppendLine("    /// and Awake() connecting the scene's components (interactions, zones, collectables, weapons, vehicles,");
            sb.AppendLine("    /// animation and sound endings) to those methods.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    public partial class {input.ClassName} : ZimensionLogic");
            sb.AppendLine("    {");
            sb.AppendLine("        /// <summary>Identifies this generation; the importer waits for Unity to compile it before connecting the scene.</summary>");
            sb.AppendLine("        public const string GeneratedHash = \"__HASH__\";");
            sb.AppendLine();
            sb.AppendLine("        // ---- Scene objects (assigned on import) ----");
            foreach (var line in g.FieldDecls) sb.AppendLine(line);
            if (g.WorldSoundFields.Count > 0) sb.AppendLine();

            if (g.VarOrder.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        // ---- World variables ----");
                sb.AppendLine("        [Header(\"Variables\")]");
                foreach (var v in g.VarOrder)
                    sb.AppendLine($"        [SerializeField, Tooltip({Literal("Zimension variable \"" + v.Name + "\"")})] {CsType(v.Type)} {v.Field} = {Typed(v.Initial, v.Type)};");
                foreach (var v in g.VarOrder)
                {
                    sb.AppendLine();
                    sb.AppendLine($"        public {CsType(v.Type)} {v.Property}");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            get => {v.Field};");
                    sb.AppendLine($"            set {{ {v.Field} = value; Changed({Literal(v.Name)}, value); }}");
                    sb.AppendLine("        }");
                }
                sb.AppendLine();
                sb.AppendLine("        protected override void WriteVariables(Dictionary<string, string> into)");
                sb.AppendLine("        {");
                foreach (var v in g.VarOrder) sb.AppendLine($"            into[{Literal(v.Name)}] = Text({v.Field});");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        protected override void ReadVariables(Dictionary<string, string> from)");
                sb.AppendLine("        {");
                foreach (var v in g.VarOrder)
                {
                    var conv = v.Type == VarType.Number ? "Num(s)" : v.Type == VarType.Boolean ? "Bool(s)" : "s";
                    sb.AppendLine($"            if (from.TryGetValue({Literal(v.Name)}, out var {v.Field}Value)) {{ var s = {v.Field}Value; {v.Field} = {conv}; }}");
                }
                sb.AppendLine("        }");
            }

            sb.AppendLine();
            sb.AppendLine("        partial void OnAwake();");
            sb.AppendLine();
            sb.AppendLine("        void Awake()");
            sb.AppendLine("        {");
            foreach (var line in g.Connect) sb.AppendLine("            " + ResolveLocals(g, line));
            sb.AppendLine("            RestoreCarried();");
            sb.AppendLine("            OnAwake();");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public override void BeginPlay()");
            sb.AppendLine("        {");
            foreach (var line in g.AutoStart) sb.AppendLine("            " + line);
            sb.AppendLine("            base.BeginPlay();");
            sb.AppendLine("        }");

            void EventBody(string eventName, List<string> statements)
            {
                sb.AppendLine("        {");
                sb.AppendLine($"            if (!Enter({Literal(eventName)})) return;");
                sb.AppendLine("            try");
                sb.AppendLine("            {");
                foreach (var s in statements) sb.AppendLine("                " + ResolveLocals(g, s));
                sb.AppendLine("            }");
                sb.AppendLine("            finally { Exit(); }");
                sb.AppendLine("        }");
            }

            sb.AppendLine();
            sb.AppendLine("        // ---- Events ----");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>Zimension \"on_start\": play has begun.</summary>");
            sb.AppendLine("        protected override void OnStart()");
            EventBody("on_start", g.Global.TryGetValue("on_start", out var start) ? start : new List<string>());
            foreach (var pair in g.EventMethods)
            {
                sb.AppendLine();
                sb.AppendLine($"        /// <summary>Zimension event \"{Comment(pair.Key)}\".</summary>");
                sb.AppendLine($"        public void {pair.Value}()");
                EventBody(pair.Key, g.Global.TryGetValue(pair.Key, out var list) ? list : new List<string>());
            }

            if (g.LocalMethods.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        // ---- Object events (\"@\" names belong to one object) ----");
                foreach (var pair in g.LocalMethods)
                {
                    var plan = g.Plans[pair.Key.Item1];
                    sb.AppendLine();
                    sb.AppendLine($"        /// <summary>\"{Comment(pair.Key.Item2)}\" on {Comment(plan.Item.Name)}.</summary>");
                    sb.AppendLine($"        public void {pair.Value}()");
                    var statements = new List<string> { $"if (Scope != {plan.Field}) {{ RunIn({plan.Field}, {pair.Value}); return; }}" };
                    statements.AddRange(g.Local[pair.Key]);
                    EventBody(pair.Key.Item2, statements);
                }
                foreach (var pair in g.LocalDispatchers)
                {
                    sb.AppendLine();
                    sb.AppendLine($"        /// <summary>\"{Comment(pair.Key)}\" on whichever object raised the current event.</summary>");
                    sb.AppendLine($"        void {pair.Value}()");
                    sb.AppendLine("        {");
                    foreach (var local in g.LocalMethods.Where(l => l.Key.Item2 == pair.Key))
                        sb.AppendLine($"            if (Scope && Scope == {g.Plans[local.Key.Item1].Field}) {{ {local.Value}(); return; }}");
                    sb.AppendLine("        }");
                }
            }

            if (g.Flows.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        // ---- Logic flows ----");
                sb.Append(ResolveLocals(g, g.Flows.ToString()));
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");
            g.Output.Code = sb.ToString();

            var user = new StringBuilder();
            user.AppendLine("using UnityEngine;");
            user.AppendLine("using Zimension.Kit;");
            user.AppendLine();
            user.AppendLine($"namespace {input.Namespace}");
            user.AppendLine("{");
            user.AppendLine("    /// <summary>");
            user.AppendLine($"    /// Your half of {input.ClassName}. Re-importing the world never touches this file.");
            user.AppendLine("    /// Add fields, methods and Unity messages here; call the generated event methods (they are");
            user.AppendLine("    /// public, and also show up in UnityEvent pickers in the Inspector).");
            user.AppendLine("    /// </summary>");
            user.AppendLine($"    public partial class {input.ClassName}");
            user.AppendLine("    {");
            user.AppendLine("        partial void OnAwake()");
            user.AppendLine("        {");
            user.AppendLine("            // Runs at the end of Awake, after the scene objects are connected.");
            user.AppendLine("        }");
            user.AppendLine("    }");
            user.AppendLine("}");
            g.Output.UserCode = user.ToString();
        }
    }
}
