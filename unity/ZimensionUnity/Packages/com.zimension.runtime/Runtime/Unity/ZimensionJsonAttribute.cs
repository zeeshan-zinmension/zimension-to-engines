using UnityEngine;

namespace Zimension.Unity
{
    /// <summary>
    /// Marks a string field that holds saved Zimension data (JSON). The Inspector shows it as one
    /// collapsed line with its size; expanding it shows a read-only, formatted preview and a Copy button.
    /// </summary>
    public sealed class ZimensionJsonAttribute : PropertyAttribute
    {
        public readonly string Label;
        public ZimensionJsonAttribute(string label = null) { Label = label; }
    }
}
