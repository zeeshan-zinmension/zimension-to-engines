using System;
using System.Collections.Generic;
using System.Numerics;

namespace Zimension.Core
{
    /// <summary>
    /// What the event runtime asks of its engine: rendering, collision, audio, animation and UI.
    /// The Unity host implements it with GameObjects and components; tests implement it with
    /// recorders. Item and card numbers index <see cref="EventWorld.Items"/> and their card lists.
    /// </summary>
    public abstract class EventHost
    {
        /// <summary>Every world event that reaches the dispatcher (custom HUD/overlay triggers, scene switches, Unity listeners).</summary>
        public virtual void OnWorldEvent(string name, int scopeItem) { }
        public virtual void VariableChanged(string name, string value) { }
        /// <summary>A flow's showMessage; empty clears.</summary>
        public virtual void Message(string text) { }
        /// <summary>Interaction / pickup prompt; empty hides.</summary>
        public virtual void Prompt(string text, bool pickup) { }
        /// <summary>reset_player, player_dead, health, fades, freeze, ...</summary>
        public virtual void Builtin(string name) { }
        public virtual void Link(string url) { }
        /// <summary>A collectable was picked up (playCollectSound).</summary>
        public virtual void Collected(int item, bool inventory) { }
        /// <summary>Use this specific vehicle, weapon or prop.</summary>
        public virtual void UseGameplay(int item) { }
        /// <summary>Actual player capsule/mesh contact with a solid item.</summary>
        public virtual bool PlayerTouches(int item) => false;
        public virtual void ItemVisible(int item, bool visible) { }
        /// <summary>A new local transform (row-vector matrix in the host's space).</summary>
        public virtual void ItemMoved(int item, Matrix4x4 local) { }
        public virtual void Animation(int item, int card, bool play) { }
        /// <summary>item -1 is the world's own sound bank.</summary>
        public virtual void Sound(int item, int card, bool play) { }
        /// <summary>A flow this layer does not run (Switch Control, Return to Previous Control): the host may.</summary>
        public virtual void UnsupportedFlow(LogicFlow flow, int scopeItem) { }
        /// <summary>The waypoint marker changed (-1: none).</summary>
        public virtual void WaypointChanged(int item) { }
        public virtual void Trace(string line) { }
    }

    /// <summary>
    /// The web's event system (Engine.executeEventTriggers / triggerEvent) for one world: world
    /// events, logic flows, variables, delays, item triggers, ranges, collectables, interactions
    /// and transitions. A C# port of the Zimension native player's Events.cpp, which carries the
    /// regression cases for these semantics; ordering, scoping and timing are kept identical.
    ///
    /// Everything runs synchronously on the caller's thread: nested events execute in place
    /// (immediate dispatch), so a variable written by a flow is visible to the next condition of
    /// the same chain and there is no command queue adding a frame of latency.
    /// </summary>
    public sealed class EventRuntime
    {
        enum Action : byte
        {
            Flow, Spawn, Remove, Transition, StopTransition, Animation, StopAnimation,
            Sound, StopSound, WorldSound, StopWorldSound, EnableInteraction, DisableInteraction, Waypoint, ClearWaypoint
        }

        struct Trigger
        {
            public Action Action;
            public int Item;
            public int Index;
            public Trigger(Action action, int item = -1, int index = 0) { Action = action; Item = item; Index = index; }
        }

        struct Moving
        {
            public int Transition, Step, From, To, Direction;
            public float Elapsed, Duration, Distance;
            public bool Smooth;
        }

        sealed class ItemState
        {
            public bool Active = true, Disabled, Busy, InRange, PlayerContact;
            public List<int> TriggerTargets = new List<int>();
            public List<bool> Touching = new List<bool>();
            public float Cooldown;
            public bool MovingNow;
            public Moving Move;
            public Matrix4x4 Local;
            public Vector3 Low, High;
            public Dictionary<string, EventVariable> Variables = new Dictionary<string, EventVariable>(StringComparer.Ordinal);
            public Dictionary<string, List<Trigger>> LocalTriggers = new Dictionary<string, List<Trigger>>(StringComparer.Ordinal);
        }

        sealed class Timer
        {
            public float Remaining;
            public List<string> Events;
            public int Item;
            public int Card = -1;
            public Trigger Action;
        }

        public const int MaxDepth = 64;

        static readonly HashSet<string> Builtins = new HashSet<string>(StringComparer.Ordinal)
        {
            "reset_player", "reset_world", "player_dead", "die_start_over", "game_over_win",
            "launch_player_up_5m", "launch_player_up_10m", "hit_damge_10", "reduce_health_10", "reduce_health_50", "increase_health_20",
            "full_health", "increase_boost_20", "full_boost", "stop_vehicle", "clear_delay_events", "scene_fade_in", "scene_fade_out",
            "save_game", "freeze_player_movement", "unfreeze_player_movement",
        };

        static readonly HashSet<string> AlwaysRegistered = new HashSet<string>(StringComparer.Ordinal) { "on_start", "online_match_started", "online_match_ended" };

        EventWorld m_World = new EventWorld();
        EventHost m_Host;
        readonly Dictionary<string, List<Trigger>> m_Triggers = new Dictionary<string, List<Trigger>>(StringComparer.Ordinal);
        readonly Dictionary<string, EventVariable> m_Variables = new Dictionary<string, EventVariable>(StringComparer.Ordinal);
        readonly List<string> m_VariableOrder = new List<string>();
        ItemState[] m_Items = Array.Empty<ItemState>();
        List<Timer> m_Timers = new List<Timer>();
        readonly List<int> m_Intersects = new List<int>(); // items in range, in entry order
        int m_InteractionItem = -1, m_Waypoint = -1;
        int m_MountedItem = -1, m_EquippedItem = -1, m_HeldItem = -1;
        string m_ShownPrompt = "";
        bool m_ShownPickup;
        Vector3 m_PlayerLow, m_PlayerHigh;
        int m_Depth;
        long m_EventsRun;
        readonly Queue<string> m_Trace = new Queue<string>();
        readonly HashSet<string> m_Reported = new HashSet<string>(StringComparer.Ordinal);
        long m_Clears;

        public EventWorld World => m_World;
        public int InteractionItem => m_InteractionItem;
        /// <summary>Engine.currentWaypoint: the item an Activate Waypoint event marks on the HUD, or -1.</summary>
        public int Waypoint => m_Waypoint;
        public long EventsRun => m_EventsRun;
        public IEnumerable<string> TraceLines => m_Trace;
        public (Vector3 low, Vector3 high) PlayerBounds => (m_PlayerLow, m_PlayerHigh);
        public bool ItemActive(int item) => m_Items[item].Active;
        public IEnumerable<string> VariableNames => m_VariableOrder;

        static string Trim(string s) => EventValue.Trim(s);
        static bool IsLocal(string name) => Trim(name).StartsWith("@", StringComparison.Ordinal);

        void Note(string line)
        {
            m_Trace.Enqueue(line);
            while (m_Trace.Count > 200) m_Trace.Dequeue();
            m_Host?.Trace(line);
        }

        public void Load(EventWorld world, EventHost host)
        {
            m_World = world ?? throw new ArgumentNullException(nameof(world));
            m_Host = host ?? throw new ArgumentNullException(nameof(host));
            Reset();
        }

        void SetVariableEntry(string name, EventVariable variable)
        {
            if (!m_Variables.ContainsKey(name)) m_VariableOrder.Add(name);
            m_Variables[name] = variable;
        }

        /// <summary>Back to the loaded state (reset_world): variables, items, transitions, timers.</summary>
        public void Reset()
        {
            m_Triggers.Clear(); m_Variables.Clear(); m_VariableOrder.Clear(); m_Timers.Clear(); m_Intersects.Clear(); ++m_Clears;
            m_InteractionItem = m_MountedItem = m_EquippedItem = m_HeldItem = -1;
            int previousWaypoint = m_Waypoint;
            m_Waypoint = -1; m_Depth = 0;
            if (previousWaypoint >= 0) m_Host.WaypointChanged(-1);
            if (m_ShownPrompt.Length > 0 || m_ShownPickup) m_Host.Prompt("", false);
            m_ShownPrompt = ""; m_ShownPickup = false;
            foreach (var v in m_World.Variables) SetVariableEntry(v.Name, v.Clone());
            m_Items = new ItemState[m_World.Items.Count];
            for (int i = 0; i < m_World.Items.Count; i++)
            {
                var item = m_World.Items[i];
                var state = m_Items[i] = new ItemState();
                state.Active = item.Active && !item.Hidden;
                state.Local = item.Local; state.Low = item.Low; state.High = item.High;
                state.Disabled = state.Busy = item.Interaction && Trim(item.EnableEvent).Length > 0;
                foreach (var v in item.Variables) state.Variables[v.Name] = v.Clone();
                foreach (var t in item.ItemTriggers)
                {
                    int target = -1;
                    for (int k = 0; k < m_World.Items.Count && target < 0; k++)
                    {
                        var other = m_World.Items[k];
                        if (other.Id == t.Target || (other.SourceId.Length > 0 && other.SourceId == t.Target)) target = k;
                    }
                    state.TriggerTargets.Add(target);
                    state.Touching.Add(false);
                }
                // The world's own variable of that name wins (setupVariables runs again after items load).
                if (item.Counter.Name.Length > 0 && !m_Variables.ContainsKey(item.Counter.Name)) SetVariableEntry(item.Counter.Name, item.Counter.Clone());
            }
            // Registration as in Engine: item triggers while items load, then logic flows.
            for (int i = 0; i < m_World.Items.Count; i++)
            {
                var item = m_World.Items[i];
                for (int c = 0; c < item.Sounds.Count; c++)
                {
                    if (item.Sounds[c].Playmode == "event") Add(item.Sounds[c].Event, new Trigger(Action.Sound, i, c));
                    Add(item.Sounds[c].StopEvent, new Trigger(Action.StopSound, i, c));
                }
                Add(item.SpawnEvent, new Trigger(Action.Spawn, i));
                Add(item.DisappearEvent, new Trigger(Action.Remove, i));
                Add(item.WaypointEvent, new Trigger(Action.Waypoint, i));
                Add(item.WaypointDeactivateEvent, new Trigger(Action.ClearWaypoint, i));
                if (item.Interaction)
                {
                    Add(item.EnableEvent, new Trigger(Action.EnableInteraction, i));
                    Add(item.DisableEvent, new Trigger(Action.DisableInteraction, i));
                }
                for (int c = 0; c < item.Animations.Count; c++)
                {
                    if (item.Animations[c].Playmode == "event") Add(item.Animations[c].Event, new Trigger(Action.Animation, i, c));
                    Add(item.Animations[c].StopEvent, new Trigger(Action.StopAnimation, i, c));
                }
                for (int c = 0; c < item.Transitions.Count; c++)
                {
                    if (item.Transitions[c].Card.Playmode == "event") Add(item.Transitions[c].Card.Event, new Trigger(Action.Transition, i, c));
                    Add(item.Transitions[c].Card.StopEvent, new Trigger(Action.StopTransition, i, c));
                }
            }
            for (int c = 0; c < m_World.Sounds.Count; c++)
            {
                if (m_World.Sounds[c].Playmode == "event") Add(m_World.Sounds[c].Event, new Trigger(Action.WorldSound, -1, c));
                Add(m_World.Sounds[c].StopEvent, new Trigger(Action.StopWorldSound, -1, c));
            }
            for (int f = 0; f < m_World.Flows.Count; f++)
            {
                var flow = m_World.Flows[f];
                if (flow.Owner.Length == 0) { Add(flow.Start, new Trigger(Action.Flow, -1, f)); continue; }
                for (int i = 0; i < m_World.Items.Count; i++)
                {
                    if (m_World.Items[i].Id == flow.Owner) { Add(flow.Start, new Trigger(Action.Flow, i, f)); break; }
                }
            }
            // Bring the host to the loaded state (also after reset_world).
            for (int i = 0; i < m_World.Items.Count; i++)
            {
                var item = m_World.Items[i];
                m_Host.ItemVisible(i, m_Items[i].Active && item.Visible);
                if (item.Transitions.Count > 0) m_Host.ItemMoved(i, item.Local);
                for (int c = 0; c < item.Sounds.Count; c++) m_Host.Sound(i, c, false);
            }
            for (int c = 0; c < m_World.Sounds.Count; c++) m_Host.Sound(-1, c, false);
            foreach (var name in m_VariableOrder) m_Host.VariableChanged(name, EventValue.Display(m_Variables[name].Current));
        }

        void Add(string rawName, Trigger trigger)
        {
            var name = Trim(rawName);
            if (name.Length == 0) return;
            if (IsLocal(name))
            {
                if (trigger.Item >= 0)
                {
                    var map = m_Items[trigger.Item].LocalTriggers;
                    if (!map.TryGetValue(name, out var list)) map[name] = list = new List<Trigger>();
                    list.Add(trigger);
                }
                return;
            }
            // Engine.addTriggerToEvent only binds events the world lists (plus its built-in names).
            if (!AlwaysRegistered.Contains(name) && !m_World.Events.Contains(name)) return;
            if (!m_Triggers.TryGetValue(name, out var triggers)) m_Triggers[name] = triggers = new List<Trigger>();
            triggers.Add(trigger);
        }

        /// <summary>What a scene switch carries into the reloaded world: world variables and delayed world events.</summary>
        public sealed class Carry
        {
            public List<KeyValuePair<string, EventValue>> Variables = new List<KeyValuePair<string, EventValue>>();
            public List<KeyValuePair<float, List<string>>> Delayed = new List<KeyValuePair<float, List<string>>>();
        }

        public Carry TakeCarry()
        {
            var c = new Carry();
            foreach (var name in m_VariableOrder) c.Variables.Add(new KeyValuePair<string, EventValue>(name, m_Variables[name].Value));
            foreach (var t in m_Timers)
                if (t.Item < 0 && t.Card < 0) c.Delayed.Add(new KeyValuePair<float, List<string>>(t.Remaining, new List<string>(t.Events)));
            return c;
        }

        /// <summary>After Load(); the HUD hears each restored variable.</summary>
        public void Restore(Carry c)
        {
            if (c == null) return;
            foreach (var pair in c.Variables)
            {
                if (!m_Variables.TryGetValue(pair.Key, out var v)) continue;
                v.Value = pair.Value;
                m_Host.VariableChanged(pair.Key, EventValue.Display(pair.Value));
            }
            foreach (var pair in c.Delayed) m_Timers.Add(new Timer { Remaining = pair.Key, Events = pair.Value, Item = -1 });
        }

        /// <summary>
        /// Play started: On Start transitions and sounds, then on_start (not after a scene switch:
        /// the world has already started, only the new scene's items begin).
        /// </summary>
        public void Start(bool fireOnStart = true)
        {
            for (int i = 0; i < m_World.Items.Count; i++)
            {
                var item = m_World.Items[i];
                for (int t = 0; t < item.Transitions.Count; t++) if (item.Transitions[t].Card.Playmode == "auto") StartTransition(i, t);
                for (int c = 0; c < item.Sounds.Count; c++) if (item.Sounds[c].Playmode == "auto" && m_Items[i].Active) m_Host.Sound(i, c, true);
            }
            for (int c = 0; c < m_World.Sounds.Count; c++) if (m_World.Sounds[c].Playmode == "auto") m_Host.Sound(-1, c, true);
            if (fireOnStart) TriggerEvent("on_start");
        }

        /// <summary>triggerEvent(name, worldItem): runs every trigger now; nested events run in place.</summary>
        public void TriggerEvent(string rawName, int item = -1)
        {
            var name = Trim(rawName);
            if (name.Length == 0 || m_Host == null) return;
            // The web recurses without a limit; a looping flow there freezes the tab. Stop it here instead.
            if (m_Depth >= MaxDepth) { Note($"Event loop stopped at '{name}' ({MaxDepth} nested events)"); return; }
            m_Depth++;
            try
            {
                ++m_EventsRun;
                Note(item >= 0 && item < m_World.Items.Count ? $"{name} ({m_World.Items[item].Name})" : name);
                if (IsLocal(name))
                {
                    if (item < 0 || item >= m_Items.Length) return;
                    if (!m_Items[item].LocalTriggers.TryGetValue(name, out var local)) return;
                    foreach (var t in local.ToArray()) Run(t, item);
                    return;
                }
                if (Builtins.Contains(name))
                {
                    if (name == "clear_delay_events") { m_Timers.Clear(); ++m_Clears; return; }
                    m_Host.Builtin(name);
                    return;
                }
                m_Host.OnWorldEvent(name, item);
                if (!m_Triggers.TryGetValue(name, out var triggers)) return;
                foreach (var t in triggers.ToArray()) Run(t, item); // a trigger may change the registry (reset_world)
            }
            finally { m_Depth--; }
        }

        void Run(Trigger t, int scope)
        {
            // playSequencedEventAction: a card with "sequencerAt" runs after that many seconds, restarting its timer.
            float at = 0;
            switch (t.Action)
            {
                case Action.Transition: at = m_World.Items[t.Item].Transitions[t.Index].Card.SequencerAt; break;
                case Action.Animation: at = m_World.Items[t.Item].Animations[t.Index].SequencerAt; break;
                case Action.Sound: at = m_World.Items[t.Item].Sounds[t.Index].SequencerAt; break;
                case Action.WorldSound: at = m_World.Sounds[t.Index].SequencerAt; break;
            }
            if (at > 0)
            {
                m_Timers.RemoveAll(timer => timer.Card >= 0 && timer.Action.Action == t.Action && timer.Action.Item == t.Item && timer.Action.Index == t.Index);
                m_Timers.Add(new Timer { Remaining = at, Events = new List<string>(), Item = scope, Card = t.Index, Action = t });
                return;
            }
            RunNow(t, scope);
        }

        void RunNow(Trigger t, int scope)
        {
            switch (t.Action)
            {
                case Action.Flow:
                {
                    // A flow owned by an item runs in that item's local scope.
                    var flow = m_World.Flows[t.Index];
                    RunFlow(flow, flow.Owner.Length > 0 && t.Item >= 0 ? t.Item : scope >= 0 ? scope : t.Item);
                    break;
                }
                case Action.Spawn: Show(t.Item, true); break;
                case Action.Remove: Show(t.Item, false); break;
                case Action.Waypoint:
                    m_Waypoint = t.Item; Note("  waypoint " + m_World.Items[t.Item].Name);
                    m_Host.WaypointChanged(m_Waypoint);
                    break;
                case Action.ClearWaypoint:
                    // A conversation can activate the next guide before this item retires.
                    if (m_Waypoint == t.Item) { m_Waypoint = -1; Note("  waypoint off"); m_Host.WaypointChanged(-1); }
                    break;
                case Action.Transition: StartTransition(t.Item, t.Index); break;
                case Action.StopTransition: m_Items[t.Item].MovingNow = false; break;
                case Action.Animation: m_Host.Animation(t.Item, t.Index, true); break;
                case Action.StopAnimation: m_Host.Animation(t.Item, t.Index, false); break;
                case Action.Sound: m_Host.Sound(t.Item, t.Index, true); break;
                case Action.StopSound: m_Host.Sound(t.Item, t.Index, false); break;
                case Action.WorldSound: m_Host.Sound(-1, t.Index, true); break;
                case Action.StopWorldSound: m_Host.Sound(-1, t.Index, false); break;
                case Action.EnableInteraction: m_Items[t.Item].Disabled = m_Items[t.Item].Busy = false; break;
                case Action.DisableInteraction:
                    m_Items[t.Item].Disabled = m_Items[t.Item].Busy = true;
                    if (m_InteractionItem == t.Item && m_ShownPrompt.Length > 0) { m_ShownPrompt = ""; m_Host.Prompt("", false); }
                    break;
            }
        }

        EventVariable Lookup(string name, int item)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (IsLocal(name))
            {
                if (item < 0 || item >= m_Items.Length) return null;
                return m_Items[item].Variables.TryGetValue(name, out var local) ? local : null;
            }
            return m_Variables.TryGetValue(name, out var v) ? v : null;
        }

        /// <summary>A variable by name; "@" names resolve in the given item's scope.</summary>
        public EventVariable Variable(string name, int item = -1) => Lookup(name, item);

        /// <summary>
        /// Engine.syncDefaultRuntimeVariables: a read-only built-in (player_health, ammo_*, ...)
        /// mirrored from engine state, so conditions and HUD bindings see it; the host hears only real changes.
        /// </summary>
        public void BuiltIn(string name, EventValue v)
        {
            if (!m_Variables.TryGetValue(name, out var variable))
            {
                variable = new EventVariable { Name = name, Type = v.Kind == EventValue.ValueKind.Boolean ? "boolean" : "number", Initial = v };
                SetVariableEntry(name, variable);
            }
            else if (EventValue.Display(variable.Value) == EventValue.Display(v)) return;
            variable.Value = v;
            m_Host.VariableChanged(name, EventValue.Display(v));
        }

        /// <summary>Set a variable from outside the flows (Unity scripts, UI). Normalized like Change Variable.</summary>
        public void SetVariable(string name, EventValue v, int item = -1)
        {
            var target = Lookup(name, item);
            if (target != null) { target.Value = target.Normalize(v); v = target.Value; }
            Note($"  {name} = {EventValue.Display(v)}");
            if (!IsLocal(name)) m_Host.VariableChanged(name, EventValue.Display(v));
        }

        void RunFlow(LogicFlow flow, int scope)
        {
            switch (flow.Kind)
            {
                case LogicFlow.FlowKind.TriggerEvents:
                    if (flow.Delay > 0) m_Timers.Add(new Timer { Remaining = (float)flow.Delay, Events = new List<string>(flow.Events), Item = scope });
                    else foreach (var e in flow.Events.ToArray()) TriggerEvent(e, scope);
                    if (Trim(flow.Link).Length > 0) m_Host.Link(Trim(flow.Link));
                    if (Trim(flow.Message).Length > 0) m_Host.Message(Trim(flow.Message));
                    break;
                case LogicFlow.FlowKind.ChangeVariable:
                {
                    if (!flow.HasValue) { Note($"  '{flow.Name}' has no value"); break; }
                    var v = flow.Value;
                    if (!flow.Literal) { var source = Lookup(flow.Source, scope); v = source != null ? source.Current : EventValue.Undefined; }
                    var target = Lookup(flow.Target, scope);
                    if (target == null) { Note($"  variable '{flow.Target}' not found"); break; }
                    if ((target.Type ?? "").ToLowerInvariant() == "number")
                        v = EventValue.Arithmetic(EventValue.Of(EventValue.ParseFloat(target.Current)), EventValue.Of(EventValue.ParseFloat(v)), flow.Op);
                    SetVariable(flow.Target, v, scope);
                    break;
                }
                case LogicFlow.FlowKind.CheckCondition:
                {
                    var results = new List<bool>();
                    foreach (var c in flow.Conditions) results.Add(Evaluate(c, scope));
                    // AND binds tighter than OR.
                    bool result = false;
                    if (results.Count > 0)
                    {
                        bool group = results[0];
                        for (int i = 0; i < flow.OrJoins.Count; i++)
                        {
                            bool next = i + 1 < results.Count && results[i + 1];
                            if (flow.OrJoins[i]) { result = result || group; group = next; }
                            else group = group && next;
                        }
                        result = result || group;
                    }
                    Note($"  condition '{flow.Name}' {(result ? "true" : "false")}");
                    TriggerEvent(result ? flow.TrueEvent : flow.FalseEvent, scope);
                    break;
                }
                case LogicFlow.FlowKind.SwitchCase:
                {
                    var v = flow.SwitchVariable.Length == 0 ? null : Lookup(flow.SwitchVariable, scope);
                    if (v == null) break;
                    var now = v.Current;
                    // logicSwitch.switchValueMatches: typed comparison, first matching case wins.
                    bool Matches(EventValue c)
                    {
                        if (flow.SwitchType == "boolean" || flow.SwitchType == "bool")
                        {
                            var a = EventValue.AsBoolean(now); var b = EventValue.AsBoolean(c);
                            return a.Kind == b.Kind && EventValue.Compare(a, b, "===");
                        }
                        if (flow.SwitchType == "number")
                        {
                            double a = EventValue.ParseFloat(now), b = EventValue.ParseFloat(c);
                            return !double.IsNaN(a) && !double.IsNaN(b) && a == b;
                        }
                        var x = now.Kind == EventValue.ValueKind.Text ? EventValue.Of(Trim(now.Text)) : now;
                        var y = c.Kind == EventValue.ValueKind.Text ? EventValue.Of(Trim(c.Text)) : c;
                        return EventValue.Compare(x, y, "=");
                    }
                    var next = flow.DefaultEvent;
                    foreach (var pair in flow.Cases) if (Matches(pair.Key)) { next = pair.Value; break; }
                    TriggerEvent(next, scope);
                    break;
                }
                case LogicFlow.FlowKind.Unsupported:
                    if (m_Reported.Add("flow:" + flow.Name)) Note($"  logic flow '{flow.Name}' (player control) is handed to the host");
                    m_Host.UnsupportedFlow(flow, scope);
                    break;
            }
        }

        bool Evaluate(LogicCondition c, int scope)
        {
            if (c.Variable.Length == 0) return false;
            var v = c.Value;
            EventVariable other = null;
            if (!c.Literal)
            {
                if (c.Other.Length == 0) return false;
                other = Lookup(c.Other, scope);
                v = other != null ? other.Current : EventValue.Undefined;
            }
            var target = Lookup(c.Variable, scope);
            if (target == null) return false;
            var type = (target.Type ?? "").ToLowerInvariant();
            var otherType = other != null ? (other.Type ?? "").ToLowerInvariant() : "";
            var check = target.Current;
            bool asBool = type == "boolean" || type == "bool" ||
                          (type.Length == 0 && (otherType == "boolean" || otherType == "bool" || EventValue.LooksBoolean(v) || EventValue.LooksBoolean(check)));
            if (asBool) { v = EventValue.AsBoolean(v); check = EventValue.AsBoolean(check); }
            else if (type == "number") { v = EventValue.Of(EventValue.ParseFloat(v)); check = EventValue.Of(EventValue.ParseFloat(target.Value)); }
            return asBool && c.Op != "=" && c.Op != "!=" && c.Op != "===" ? false : EventValue.Compare(check, v, c.Op);
        }

        void StartTransition(int i, int t)
        {
            var item = m_World.Items[i]; var tr = item.Transitions[t]; var state = m_Items[i];
            if (tr.Path)
            {
                if (tr.PathItem.Length == 0 || !m_World.Paths.TryGetValue(tr.PathItem, out var path) || !(path.Curve.Length > 0))
                {
                    Note($"  path transition on {item.Name}: spline not found");
                    return;
                }
                float length = path.Curve.Length;
                state.Move = new Moving { Transition = t, Direction = tr.PathReverse ? -1 : 1, Duration = 1, Distance = tr.PathReverse ? length : 0 };
                state.MovingNow = true;
                Note($"  path transition {t} on {item.Name}");
                return;
            }
            int count = item.States.Count;
            if (count == 0 || tr.States.Count == 0) return;
            int StateAt(int step) => Math.Min(tr.States[step], count - 1);
            if (tr.States.Count == 1)
            {
                state.MovingNow = false;
                Place(i, item.States[StateAt(0)]);
                if (!tr.Card.Loop) TriggerEvent(tr.Card.FinishedEvent, i);
                return;
            }
            // The first step's duration is read with `|| 1`, later ones with `?? 1`.
            float first = tr.Durations[0];
            state.Move = new Moving
            {
                Transition = t, Step = 0, From = StateAt(0), To = StateAt(1), Direction = 1,
                Elapsed = 0, Duration = !float.IsNaN(first) && !float.IsInfinity(first) && first > 0 ? first : 1, Smooth = tr.Smooth[0],
            };
            state.MovingNow = true;
            Note($"  transition {t} on {item.Name}");
        }

        /// <summary>
        /// stepPathTransition + WorldItem.performPathTransition: arc-length travel at the run's
        /// speed, the item's +Z turned along the tangent (Object3D.lookAt) when aligned; its own
        /// rotation and scale otherwise kept. The basis formulas hold in both three and Unity space.
        /// </summary>
        void StepPath(int i, float seconds)
        {
            var item = m_World.Items[i]; var state = m_Items[i]; var m = state.Move;
            var tr = item.Transitions[m.Transition]; var path = m_World.Paths[tr.PathItem];
            float length = path.Curve.Length;
            m.Distance += seconds * tr.PathSpeed * m.Direction;
            bool passEnded = false;
            if (m.Distance >= length) { m.Distance = length; passEnded = m.Direction > 0; }
            if (m.Distance <= 0) { m.Distance = 0; passEnded = passEnded || m.Direction < 0; }
            float param = path.Curve.UToT(Math.Clamp(m.Distance / length, 0f, 1f));
            var onCurve = path.Curve.Point(param); var along = path.Curve.Tangent(param);
            var point = Vector3.Transform(onCurve, path.World);
            var home = item.States.Count == 0 ? item.Local : item.States[0];
            Coordinates.Decompose(home * item.Parent, out _, out var rotation, out var scale);
            var basis = Matrix4x4.CreateFromQuaternion(rotation);
            if (tr.Align)
            {
                var z = Vector3.TransformNormal(along, path.World);
                if (m.Direction < 0) z = -z;
                if (z.LengthSquared() > 1e-6f)
                {
                    z = Vector3.Normalize(z);
                    var x = Vector3.Cross(Vector3.UnitY, z);
                    if (x.LengthSquared() < 1e-8f) x = Vector3.UnitX;
                    x = Vector3.Normalize(x);
                    var y = Vector3.Cross(z, x);
                    basis = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
                }
            }
            var local = Matrix4x4.CreateScale(scale) * basis * Matrix4x4.CreateTranslation(point) * Coordinates.InverseOrIdentity(item.Parent);
            state.Move = m;
            Place(i, local);
            if (passEnded)
            {
                m = state.Move;
                if (tr.Reverse) m.Direction = -m.Direction;
                else if (tr.Card.Loop) m.Distance = m.Direction > 0 ? 0 : length;
                else { state.MovingNow = false; state.Move = m; TriggerEvent(tr.Card.FinishedEvent, i); return; }
                state.Move = m;
            }
        }

        void StepTransition(int i, float seconds)
        {
            var item = m_World.Items[i]; var state = m_Items[i]; var m = state.Move;
            var tr = item.Transitions[m.Transition];
            if (tr.Path) { StepPath(i, seconds); return; }
            int count = item.States.Count, steps = tr.States.Count;
            int StateAt(int step) => Math.Min(tr.States[step], count - 1);
            m.Elapsed += seconds;
            if (m.Elapsed >= m.Duration)
            {
                // WorldItem.performTransition: settle on the next step and pick the following segment.
                m.Elapsed = 0;
                int arrived = m.Step + m.Direction, next = arrived + m.Direction;
                void Segment(int from, int to)
                {
                    int owner = Math.Min(from, to);
                    m.Step = from; m.From = StateAt(from); m.To = StateAt(to);
                    float d = tr.Durations[owner];
                    m.Duration = !float.IsNaN(d) && !float.IsInfinity(d) ? Math.Max(0f, d) : 1; m.Smooth = tr.Smooth[owner];
                }
                if (next >= 0 && next < steps) Segment(arrived, next);
                else if (m.Direction == 1 && tr.Reverse && arrived > 0) { m.Direction = -1; Segment(arrived, arrived - 1); }
                else if (tr.Card.Loop) { m.Direction = 1; Segment(0, 1); }
                else
                {
                    state.MovingNow = false;
                    state.Move = m;
                    if (arrived >= 0 && arrived < steps) Place(i, item.States[StateAt(arrived)]);
                    TriggerEvent(tr.Card.FinishedEvent, i);
                    return;
                }
            }
            state.Move = m;
            float progress = m.Duration > 0 ? Math.Min(m.Elapsed / m.Duration, 1f) : 1f;
            if (m.Smooth) progress = EaseInOutCubic(progress);
            Place(i, Coordinates.Interpolate(item.States[m.From], item.States[m.To], progress));
        }

        static float EaseInOutCubic(float t) => t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2;

        void Place(int i, Matrix4x4 local)
        {
            var item = m_World.Items[i]; var state = m_Items[i];
            state.Local = local;
            // Range bounds follow the item: the load bounds moved by (load world)^-1 * (new world).
            var delta = Coordinates.InverseOrIdentity(item.Local * item.Parent) * (local * item.Parent);
            var low = new Vector3(float.MaxValue); var high = new Vector3(float.MinValue);
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) != 0 ? item.High.X : item.Low.X, (c & 2) != 0 ? item.High.Y : item.Low.Y, (c & 4) != 0 ? item.High.Z : item.Low.Z);
                var p = Vector3.Transform(corner, delta);
                low = Vector3.Min(low, p); high = Vector3.Max(high, p);
            }
            state.Low = low; state.High = high;
            m_Host.ItemMoved(i, local);
        }

        void Show(int i, bool active)
        {
            var state = m_Items[i];
            state.Active = active;
            if (!active) state.PlayerContact = false;
            m_Host.ItemVisible(i, active && m_World.Items[i].Visible);
            Note((active ? "  spawn " : "  remove ") + m_World.Items[i].Name);
            // Physics.purgeDisabledIntersects: out of the range list without a leave event.
            if (!active && state.InRange) Leave(i, false);
        }

        void Leave(int i, bool fire)
        {
            m_Items[i].InRange = false;
            if (!fire) m_Items[i].PlayerContact = false;
            m_Intersects.Remove(i);
            if (m_InteractionItem == i)
            {
                m_InteractionItem = -1;
                if (m_ShownPrompt.Length > 0 || m_ShownPickup) { m_ShownPrompt = ""; m_ShownPickup = false; m_Host.Prompt("", false); }
            }
            if (fire) TriggerEvent(m_World.Items[i].LeaveEvent, i);
        }

        void Collect(int i)
        {
            var item = m_World.Items[i];
            // collectCountVariable: (value || 0) + 1, or 0 when the world has no such variable.
            if (item.Counter.Name.Length > 0)
            {
                var counter = Lookup(item.Counter.Name, -1);
                double count = 0;
                if (counter != null) { double now = EventValue.ToNumber(counter.Current); count = (JsonNode.IsFinite(now) ? now : 0) + 1; }
                SetVariable(item.Counter.Name, EventValue.Of(count), -1);
            }
            // onCollectItem: the avatar's collect sound, removeItem (hidden, sounds stopped), then On Collect.
            m_Host.Collected(i, item.Inventory);
            for (int c = 0; c < item.Sounds.Count; c++) m_Host.Sound(i, c, false);
            Show(i, false);
            Note("  collected " + item.Name);
            TriggerEvent(item.CollectEvent, i);
        }

        bool InteractionAvailable(int i) => m_World.Items[i].Interaction && !m_Items[i].Disabled;
        bool ManualPickup(int i) => m_World.Items[i].Collectable && !m_World.Items[i].AutoPickup;
        bool Excluded(int i) => i == m_EquippedItem || i == m_HeldItem;

        float RangePadding(int i)
        {
            var item = m_World.Items[i];
            // Physical contacts keep the capsule/vehicle just outside the surface. Range events and
            // auto-pickup must still see that contact; manual actions retain their reach.
            return item.Interaction || item.Vehicle || item.Weapon || item.Pickable || ManualPickup(i) ? 0.2f : 0.02f;
        }

        public struct DebugBounds
        {
            public int Item;
            public Vector3 Low, High;
            public bool Active, InRange, Excluded, Disabled, Busy;
        }

        /// <summary>The padded bounds the range test actually uses, for collision/range diagnostics.</summary>
        public List<DebugBounds> GetDebugBounds()
        {
            var result = new List<DebugBounds>(m_Items.Length);
            for (int i = 0; i < m_Items.Length; i++)
            {
                var state = m_Items[i]; float padding = RangePadding(i);
                // Logic-only items can have no geometry and therefore no spatial bounds.
                if (state.Low.X > state.High.X || state.Low.Y > state.High.Y || state.Low.Z > state.High.Z) continue;
                result.Add(new DebugBounds
                {
                    Item = i, Low = state.Low - new Vector3(padding), High = state.High + new Vector3(padding),
                    Active = state.Active, InRange = state.InRange, Excluded = Excluded(i) || i == m_MountedItem, Disabled = state.Disabled, Busy = state.Busy,
                });
            }
            return result;
        }

        /// <summary>Live bounds of a moving item (animated door, rigid body, vehicle), replacing its load bounds.</summary>
        public void MoveBounds(int i, Vector3 low, Vector3 high)
        {
            foreach (var value in new[] { low.X, low.Y, low.Z, high.X, high.Y, high.Z })
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("Interaction bounds must be finite");
            if (low.X > high.X || low.Y > high.Y || low.Z > high.Z) throw new ArgumentException("Interaction bounds minimum exceeds maximum");
            var state = m_Items[i]; state.Low = low; state.High = high;
        }

        public void GameplayState(int mounted, int equipped, int held)
        {
            foreach (var i in new[] { mounted, equipped, held })
                if (i < -1 || i >= m_Items.Length) throw new ArgumentOutOfRangeException(nameof(mounted), "Gameplay interaction item index is out of range");
            m_MountedItem = mounted; m_EquippedItem = equipped; m_HeldItem = held;
            foreach (var i in new[] { mounted, equipped, held }) if (i >= 0 && m_Items[i].InRange) Leave(i, false);
            SelectInteraction();
        }

        /// <summary>Where the item's transform currently puts its origin.</summary>
        public Vector3 ItemOrigin(int i) => (m_Items[i].Local * m_World.Items[i].Parent).Translation;
        public Vector3 ItemCenter(int i) => (m_Items[i].Low + m_Items[i].High) / 2;
        public Matrix4x4 ItemLocal(int i) => m_Items[i].Local;

        /// <summary>Timers, transitions and range checks against the player's feet (an avatar box 0.9 x 1.8 x 0.9).</summary>
        public void Update(float seconds, Vector3 feet) =>
            Update(seconds, new Vector3(feet.X - 0.45f, feet.Y, feet.Z - 0.45f), new Vector3(feet.X + 0.45f, feet.Y + 1.8f, feet.Z + 0.45f));

        /// <summary>Timers, transitions and range checks against an explicit player box (a mounted vehicle proxy).</summary>
        public void Update(float seconds, Vector3 low, Vector3 high)
        {
            if (m_Host == null) return;
            m_PlayerLow = low; m_PlayerHigh = high;
            // Delayed logic-flow events and sequenced cards.
            if (m_Timers.Count > 0)
            {
                var due = new List<Timer>();
                foreach (var t in m_Timers) t.Remaining -= seconds;
                for (int k = 0; k < m_Timers.Count;)
                {
                    if (m_Timers[k].Remaining <= 0) { due.Add(m_Timers[k]); m_Timers.RemoveAt(k); }
                    else k++;
                }
                long generation = m_Clears;
                foreach (var t in due)
                {
                    if (m_Clears != generation) break; // clear_delay_events or reset_world from an earlier timer
                    if (t.Card >= 0) RunNow(t.Action, t.Item);
                    else foreach (var e in t.Events) TriggerEvent(e, t.Item);
                }
            }
            for (int i = 0; i < m_Items.Length; i++)
            {
                var state = m_Items[i];
                if (state.Busy && state.Cooldown > 0 && (state.Cooldown -= seconds) <= 0) { state.Cooldown = 0; state.Busy = false; }
                if (state.MovingNow) StepTransition(i, seconds);
            }
            // Physics range checks use the avatar box on foot, or the live car bounds while driving.
            for (int i = 0; i < m_Items.Length; i++)
            {
                var state = m_Items[i]; var item = m_World.Items[i];
                if (!state.Active || Excluded(i) || i == m_MountedItem) continue;
                float reach = RangePadding(i);
                bool overlap = low.X <= state.High.X + reach && high.X >= state.Low.X - reach &&
                               low.Y <= state.High.Y + reach && high.Y >= state.Low.Y - reach &&
                               low.Z <= state.High.Z + reach && high.Z >= state.Low.Z - reach;
                // An authored touch event is eligible even on a solid mesh collider.
                bool tracked = m_MountedItem >= 0 || item.Collision != "object" || item.Collectable || item.Interaction || item.Vehicle || item.Weapon ||
                               item.Pickable || item.EnterEvent.Length > 0 || item.LeaveEvent.Length > 0;
                bool meshEvents = m_MountedItem < 0 && item.Collision == "object";
                bool previousContact = state.PlayerContact;
                if (!meshEvents) state.PlayerContact = false;
                long generation = m_Clears;
                if (overlap && tracked && !state.InRange)
                {
                    state.InRange = true; m_Intersects.Add(i);
                    if (item.Collectable && item.AutoPickup) Collect(i);
                    if (m_Clears != generation) return;
                    if (!meshEvents) TriggerEvent(item.EnterEvent, i);
                }
                else if (!overlap && state.InRange) Leave(i, !meshEvents);
                if (m_Clears != generation) return;
                if (meshEvents && state.Active && (item.EnterEvent.Length > 0 || item.LeaveEvent.Length > 0))
                {
                    bool contact = overlap && m_Host.PlayerTouches(i);
                    if (contact != previousContact)
                    {
                        state.PlayerContact = contact;
                        TriggerEvent(contact ? item.EnterEvent : item.LeaveEvent, i);
                        if (m_Clears != generation) return;
                    }
                }
            }
            // Item-to-item triggers: world boxes meeting fire the source's On Enter once, parting fires On Leave.
            // Either item inactive leaves the pair as it was (Engine checkWorldItemCollisions).
            long pairs = m_Clears;
            for (int i = 0; i < m_Items.Length; i++)
            {
                for (int t = 0; t < m_Items[i].TriggerTargets.Count; t++)
                {
                    int target = m_Items[i].TriggerTargets[t];
                    if (target < 0 || !m_Items[i].Active || !m_Items[target].Active) continue;
                    var a = m_Items[i]; var b = m_Items[target];
                    bool overlap = a.Low.X <= b.High.X && a.High.X >= b.Low.X && a.Low.Y <= b.High.Y && a.High.Y >= b.Low.Y && a.Low.Z <= b.High.Z && a.High.Z >= b.Low.Z;
                    if (overlap == m_Items[i].Touching[t]) continue;
                    m_Items[i].Touching[t] = overlap;
                    var trigger = m_World.Items[i].ItemTriggers[t];
                    TriggerEvent(overlap ? trigger.EnterEvent : trigger.LeaveEvent, i);
                    if (m_Clears != pairs) return; // an event reset the world
                }
            }
            SelectInteraction();
        }

        void SelectInteraction()
        {
            // Engine's frame loop visits the entry-ordered intersects list, newest last.
            int target = m_MountedItem; bool pickup = false;
            if (m_MountedItem < 0)
            {
                foreach (var i in m_Intersects)
                {
                    if (!m_Items[i].Active || Excluded(i)) continue;
                    var item = m_World.Items[i];
                    if (item.Vehicle) { target = i; pickup = false; }
                    else if (item.Weapon || item.Pickable || ManualPickup(i)) { target = i; pickup = true; }
                    else if (InteractionAvailable(i) && !m_Items[i].Busy) { target = i; pickup = false; }
                }
            }
            m_InteractionItem = target;
            string prompt = target < 0 ? "" :
                m_MountedItem >= 0 ? m_World.Items[target].ExitLabel :
                m_World.Items[target].Vehicle ? m_World.Items[target].VehicleLabel :
                pickup ? m_World.Items[target].Name : m_World.Items[target].InteractionName;
            if (prompt != m_ShownPrompt || pickup != m_ShownPickup)
            {
                m_ShownPrompt = prompt; m_ShownPickup = pickup;
                m_Host.Prompt(prompt, pickup);
            }
        }

        /// <summary>The interact key (F): exactly one selected target.</summary>
        public void Interact()
        {
            SelectInteraction();
            if (m_InteractionItem < 0) return;
            int i = m_InteractionItem; var state = m_Items[i]; var item = m_World.Items[i];
            if (m_MountedItem >= 0) { m_Host.UseGameplay(i); return; }
            if (!state.Active || Excluded(i)) return;
            if (item.Vehicle) { m_Host.UseGameplay(i); return; }
            if (ManualPickup(i)) { Collect(i); return; }
            if (InteractionAvailable(i) && !state.Busy)
            {
                Note("interact " + item.Name);
                foreach (var e in item.InteractionTriggers.ToArray()) TriggerEvent(e, i);
                for (int c = 0; c < item.Animations.Count; c++) if (item.Animations[c].Playmode == "interaction") m_Host.Animation(i, c, true);
                for (int t = 0; t < item.Transitions.Count; t++) if (item.Transitions[t].Card.Playmode == "interaction") StartTransition(i, t);
                for (int c = 0; c < item.Sounds.Count; c++) if (item.Sounds[c].Playmode == "interaction") m_Host.Sound(i, c, true);
                // beginDelay: the prompt returns after interactionTime seconds.
                state.Busy = true; state.Cooldown = Math.Max(item.InteractionTime, 1e-4f);
            }
            // KeyF runs interaction triggers before picking the target's weapon or prop.
            if (state.Active && (item.Weapon || item.Pickable)) m_Host.UseGameplay(i);
            SelectInteraction();
        }

        /// <summary>An item animation card finished playing (one-shot): fires its finished event once.</summary>
        public void AnimationFinished(int item, int card)
        {
            if (item >= 0 && item < m_World.Items.Count && card >= 0 && card < m_World.Items[item].Animations.Count)
                TriggerEvent(m_World.Items[item].Animations[card].FinishedEvent, item);
        }

        /// <summary>A sound card finished playing (not stopped): soundFinishedEvent. item -1 is the world bank.</summary>
        public void SoundFinished(int item, int card)
        {
            if (item < 0)
            {
                if (card >= 0 && card < m_World.Sounds.Count) TriggerEvent(m_World.Sounds[card].FinishedEvent);
                return;
            }
            if (item < m_World.Items.Count && card >= 0 && card < m_World.Items[item].Sounds.Count)
                TriggerEvent(m_World.Items[item].Sounds[card].FinishedEvent, item);
        }

        /// <summary>Show or remove an item from outside the flows (Unity scripts).</summary>
        public void SetItemActive(int item, bool active)
        {
            if (item >= 0 && item < m_Items.Length) Show(item, active);
        }

        public bool IsMoving(int item) => item >= 0 && item < m_Items.Length && m_Items[item].MovingNow;
    }
}
