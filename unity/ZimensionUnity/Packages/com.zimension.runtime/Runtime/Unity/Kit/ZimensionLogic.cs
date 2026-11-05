using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Events;

namespace Zimension.Kit
{
    /// <summary>
    /// Base class of a converted world's logic script. The generated subclass has one public method
    /// per Zimension event (call them from anywhere, or from UnityEvents in the Inspector), typed
    /// properties for the world variables, and an Awake that connects the scene's components to
    /// those methods. This class supplies the small helpers those methods use.
    /// </summary>
    public abstract class ZimensionLogic : MonoBehaviour
    {
        /// <summary>Events nested deeper than this are stopped (an event loop in the world's logic).</summary>
        public const int MaxDepth = 64;

        /// <summary>The object the running event chain belongs to (for "@" local events and variables).</summary>
        protected GameObject Scope { get; private set; }

        int m_Depth;
        readonly List<Coroutine> m_Delays = new List<Coroutine>();
        readonly Dictionary<string, Coroutine> m_Sequenced = new Dictionary<string, Coroutine>();

        public GameManager Game => GameManager.Current;
        public GameHud Hud => GameHud.Current;
        public PlayerController Player => PlayerController.Current;

        /// <summary>Raised whenever a world variable changes (name, new value as text).</summary>
        public event Action<string, string> VariableChanged;

        /// <summary>Play has begun (after the start screen): runs on_start.</summary>
        public virtual void BeginPlay() => OnStart();

        /// <summary>Zimension's on_start event.</summary>
        protected virtual void OnStart() { }

        // ---- Event plumbing ----------------------------------------------------------------------

        protected bool Enter(string eventName)
        {
            if (m_Depth >= MaxDepth)
            {
                Debug.LogWarning($"[Zimension] Event loop stopped at '{eventName}' ({MaxDepth} nested events).", this);
                return false;
            }
            m_Depth++;
            return true;
        }

        protected void Exit() => m_Depth = Mathf.Max(0, m_Depth - 1);

        /// <summary>Connect a UnityEvent to an action that runs in an object's scope.</summary>
        protected void On(UnityEvent unityEvent, GameObject item, Action action)
        {
            if (unityEvent == null || action == null) return;
            unityEvent.AddListener(() => RunIn(item, action));
        }

        protected void RunIn(GameObject item, Action action)
        {
            var previous = Scope;
            Scope = item;
            try { action(); }
            finally { Scope = previous; }
        }

        /// <summary>Run an action after a delay (Zimension's flow delay), in the current scope.</summary>
        protected void After(float seconds, Action action)
        {
            var scope = Scope;
            Coroutine routine = null;
            routine = StartCoroutine(Wait(seconds, () => { m_Delays.Remove(routine); RunIn(scope, action); }));
            m_Delays.Add(routine);
        }

        /// <summary>A sequenced card: starting it again restarts its timer.</summary>
        protected void AfterOnce(string key, float seconds, Action action)
        {
            if (m_Sequenced.TryGetValue(key, out var running) && running != null) StopCoroutine(running);
            var scope = Scope;
            m_Sequenced[key] = StartCoroutine(Wait(seconds, () => { m_Sequenced.Remove(key); RunIn(scope, action); }));
        }

        static IEnumerator Wait(float seconds, Action then)
        {
            yield return new WaitForSeconds(Mathf.Max(0, seconds));
            then();
        }

        /// <summary>clear_delay_events: drop every pending delayed event.</summary>
        public void ClearDelays()
        {
            foreach (var c in m_Delays) if (c != null) StopCoroutine(c);
            foreach (var c in m_Sequenced.Values) if (c != null) StopCoroutine(c);
            m_Delays.Clear(); m_Sequenced.Clear();
        }

        // ---- Actions -----------------------------------------------------------------------------

        protected static void Show(GameObject item) { if (item) item.SetActive(true); }
        protected static void Hide(GameObject item) { if (item) item.SetActive(false); }

        protected void Message(string text) { if (Hud) Hud.ShowMessage(text); else Debug.Log("[Zimension] " + text, this); }

        protected static void OpenLink(string url)
        {
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Application.OpenURL(url);
        }

        protected void Builtin(string name)
        {
            if (Game) Game.Builtin(name);
            else Debug.LogWarning($"[Zimension] '{name}' needs a GameManager in the scene.", this);
        }

        /// <summary>Play a sound from the start; the object's other sounds stop (Zimension plays one track per item).</summary>
        protected static void PlaySound(AudioSource source, bool stopOthers = true)
        {
            if (!source) return;
            if (stopOthers && source.transform.parent)
                foreach (var other in source.transform.parent.GetComponentsInChildren<AudioSource>())
                    if (other != source) other.Stop();
            source.Stop();
            source.Play();
        }

        protected static void StopSound(AudioSource source) { if (source) source.Stop(); }

        /// <summary>Cross-fade an Animator to a state (Zimension fades animations over 0.5 s).</summary>
        protected static void PlayAnimation(Animator animator, string state, float fade = 0.5f)
        {
            if (!animator || !animator.isActiveAndEnabled) return;
            animator.speed = 1;
            animator.CrossFadeInFixedTime(state, fade, 0, 0);
        }

        /// <summary>Stop an animation: back to the Animator's resting state.</summary>
        protected static void StopAnimation(Animator animator, string restState, float fade = 0.5f)
        {
            if (!animator || !animator.isActiveAndEnabled) return;
            animator.CrossFadeInFixedTime(restState, fade, 0);
        }

        /// <summary>Start a transition (a movement clip on the object's transition Animator) from its beginning.</summary>
        protected static void PlayTransition(Animator animator, string state)
        {
            if (!animator || !animator.isActiveAndEnabled) return;
            animator.speed = 1;
            animator.Play(state, 0, 0);
        }

        /// <summary>Stop a transition where it is.</summary>
        protected static void StopTransition(Animator animator) { if (animator) animator.speed = 0; }

        protected static void PlayMotion(ProceduralMotion motion) { if (motion) motion.Play(); }
        protected static void StopMotion(ProceduralMotion motion) { if (motion) motion.Stop(); }

        protected void SetWaypoint(Transform target) => Hud?.SetWaypoint(target);
        protected void ClearWaypoint(Transform target) => Hud?.ClearWaypoint(target);

        // ---- Variables ---------------------------------------------------------------------------

        static Dictionary<string, string> s_Carry;

        /// <summary>The world variables as text (the generated class lists them).</summary>
        protected virtual void WriteVariables(Dictionary<string, string> into) { }
        protected virtual void ReadVariables(Dictionary<string, string> from) { }

        /// <summary>Keep the world variables for the next scene (Zimension carries them across scene switches).</summary>
        public void CarryVariables()
        {
            s_Carry = new Dictionary<string, string>();
            WriteVariables(s_Carry);
        }

        protected void RestoreCarried()
        {
            if (s_Carry == null) return;
            ReadVariables(s_Carry);
            s_Carry = null;
        }

        protected void Changed(string name, object value) => VariableChanged?.Invoke(name, Text(value));

        LocalVariables Locals => Scope ? Scope.GetComponent<LocalVariables>() : null;
        protected float LocalNumber(string name) => Locals ? Locals.GetNumber(name) : 0;
        protected bool LocalBool(string name) => Locals && Locals.GetBool(name);
        protected string LocalText(string name) => Locals ? Locals.GetText(name) : "";
        protected void SetLocal(string name, object value)
        {
            var locals = Locals;
            if (locals) locals.Set(name, Text(value));
            else Debug.LogWarning($"[Zimension] Local variable '{name}' has no object in scope.", this);
        }

        /// <summary>A value as a number, the way Zimension reads it (text parsed, true = 1, invalid = 0).</summary>
        public static float Num(object v)
        {
            switch (v)
            {
                case null: return 0;
                case float f: return f;
                case double d: return (float)d;
                case int i: return i;
                case bool b: return b ? 1 : 0;
                case string s:
                    return float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
                default: return 0;
            }
        }

        public static bool Bool(object v)
        {
            switch (v)
            {
                case null: return false;
                case bool b: return b;
                case float f: return f != 0;
                case double d: return d != 0;
                case int i: return i != 0;
                case string s: s = s.Trim().ToLowerInvariant(); return s == "true" || s == "1";
                default: return false;
            }
        }

        public static string Text(object v)
        {
            switch (v)
            {
                case null: return "";
                case bool b: return b ? "true" : "false";
                case float f: return f.ToString(CultureInfo.InvariantCulture);
                case double d: return d.ToString(CultureInfo.InvariantCulture);
                default: return v.ToString();
            }
        }
    }
}
