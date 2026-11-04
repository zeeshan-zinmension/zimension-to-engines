using System;
using UnityEngine;
using UnityEngine.Events;
using Zimension.Core;

namespace Zimension.Unity
{
    /// <summary>
    /// The public scripting surface for exported projects: fire Zimension events from ordinary
    /// Unity scripts and listen to the workflows' events and variables.
    /// <code>
    /// ZimensionEngine.TriggerEvent("open_gate");               // a world event
    /// ZimensionEngine.TriggerEvent("@ping", "64f0c3...");     // an item's local event
    /// ZimensionEngine.EventFired += (name, item) => Debug.Log(name);
    /// var score = ZimensionEngine.GetVariable("score");
    /// </code>
    /// Everything runs on Unity's main thread, in order with the event runtime.
    /// </summary>
    public static class ZimensionEngine
    {
        /// <summary>(event name, scope item id or null) after the dispatcher received a world event.</summary>
        public static event Action<string, string> EventFired;
        /// <summary>(variable name, value as text) when a world variable changes.</summary>
        public static event Action<string, string> VariableChanged;

        public static ZimensionWorld Active { get; internal set; }

        public static void TriggerEvent(string name, string ownerItemId = null)
        {
            if (Active == null) { Debug.LogWarning($"[Zimension] TriggerEvent('{name}') with no running Zimension world."); return; }
            Active.TriggerEvent(name, ownerItemId);
        }

        public static string GetVariable(string name, string ownerItemId = null) => Active ? Active.GetVariable(name, ownerItemId) : null;

        public static void SetVariable(string name, string value, string ownerItemId = null)
        {
            if (Active) Active.SetVariable(name, value, ownerItemId);
        }

        public static void SetVariable(string name, double value, string ownerItemId = null)
        {
            if (Active) Active.SetVariable(name, EventValue.Of(value), ownerItemId);
        }

        public static void SetVariable(string name, bool value, string ownerItemId = null)
        {
            if (Active) Active.SetVariable(name, EventValue.Of(value), ownerItemId);
        }

        internal static void RaiseEvent(string name, string item) => EventFired?.Invoke(name, item);
        internal static void RaiseVariable(string name, string value) => VariableChanged?.Invoke(name, value);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Enter Play Mode without a domain reload keeps statics: start each run clean.
            EventFired = null;
            VariableChanged = null;
            Active = null;
            ZimensionSession.Clear();
        }
    }


    /// <summary>
    /// State that survives a scene switch: whether the world's first on_start has run, the event
    /// runtime's carried variables and delayed world events, and the player's health.
    /// </summary>
    public static class ZimensionSession
    {
        public static bool Started;
        public static EventRuntime.Carry Carry;
        public static float Health = -1;
        public static bool FadeInOnLoad;
        public static float FadeDuration = 0.35f;

        public static void Clear()
        {
            Started = false; Carry = null; Health = -1; FadeInOnLoad = false; FadeDuration = 0.35f;
        }
    }
}
