using UnityEngine;

namespace Zimension.Unity
{
    public static class ComponentExtensions
    {
        /// <summary>
        /// The object's component of this type, added when missing. (Never write
        /// <c>GetComponent&lt;T&gt;() ?? AddComponent&lt;T&gt;()</c>: in the Editor a missing component is a
        /// "fake null" object that C#'s ?? does not treat as null.)
        /// </summary>
        public static T GetOrAdd<T>(this GameObject go) where T : Component
        {
            return go.TryGetComponent<T>(out var existing) ? existing : go.AddComponent<T>();
        }
    }
}
