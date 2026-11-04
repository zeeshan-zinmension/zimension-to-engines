using UnityEngine;

namespace Zimension.Unity
{
    /// <summary>
    /// Keyboard/mouse reads through the Input Manager. The project setup selects "Both" input
    /// handling, so these work whether or not the Input System package is added later. A project
    /// switched to the Input System only would throw from UnityEngine.Input: every read is guarded
    /// so the game keeps running (with a single warning) instead of failing each frame.
    /// </summary>
    public static class ZimensionInput
    {
        static bool s_Failed;

        static bool Guard(System.Func<bool> read)
        {
            if (s_Failed) return false;
            try { return read(); }
            catch (System.InvalidOperationException)
            {
                s_Failed = true;
                Debug.LogWarning("[Zimension] Input Manager reads are disabled. Set Project Settings > Player > Active Input Handling to 'Both' (Zimension > Setup Project does this).");
                return false;
            }
        }

        static float GuardAxis(System.Func<float> read)
        {
            if (s_Failed) return 0;
            try { return read(); }
            catch (System.InvalidOperationException) { s_Failed = true; return 0; }
        }

        public static bool Key(KeyCode key) => Guard(() => Input.GetKey(key));
        public static bool KeyDown(KeyCode key) => Guard(() => Input.GetKeyDown(key));
        public static bool KeyUp(KeyCode key) => Guard(() => Input.GetKeyUp(key));
        public static bool MouseDown(int button) => Guard(() => Input.GetMouseButtonDown(button));
        public static bool Mouse(int button) => Guard(() => Input.GetMouseButton(button));
        public static float MouseX => GuardAxis(() => Input.GetAxisRaw("Mouse X"));
        public static float MouseY => GuardAxis(() => Input.GetAxisRaw("Mouse Y"));
        public static float Scroll => GuardAxis(() => Input.mouseScrollDelta.y);
        public static Vector3 MousePosition { get { if (s_Failed) return Vector3.zero; try { return Input.mousePosition; } catch (System.InvalidOperationException) { s_Failed = true; return Vector3.zero; } } }

        /// <summary>WASD / arrow keys as a planar vector (x: right, y: forward).</summary>
        public static Vector2 Move
        {
            get
            {
                float x = 0, y = 0;
                if (Key(KeyCode.W) || Key(KeyCode.UpArrow)) y += 1;
                if (Key(KeyCode.S) || Key(KeyCode.DownArrow)) y -= 1;
                if (Key(KeyCode.D) || Key(KeyCode.RightArrow)) x += 1;
                if (Key(KeyCode.A) || Key(KeyCode.LeftArrow)) x -= 1;
                var v = new Vector2(x, y);
                return v.sqrMagnitude > 1 ? v.normalized : v;
            }
        }
    }
}
