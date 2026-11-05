using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Zimension.Kit
{
    /// <summary>
    /// An object's own variables (Zimension "@" variables), editable in the Inspector. Values are
    /// kept as text and read as numbers or booleans where the logic needs them.
    /// </summary>
    [AddComponentMenu("Zimension/Local Variables")]
    public sealed class LocalVariables : MonoBehaviour
    {
        [Serializable]
        public sealed class Variable
        {
            public string name;
            public string value;
        }

        public List<Variable> variables = new List<Variable>();

        Variable Find(string name, bool create)
        {
            foreach (var v in variables) if (v.name == name) return v;
            if (!create) return null;
            var added = new Variable { name = name, value = "" };
            variables.Add(added);
            return added;
        }

        public string GetText(string name) => Find(name, false)?.value ?? "";
        public float GetNumber(string name) => ZimensionLogic.Num(GetText(name));
        public bool GetBool(string name) => ZimensionLogic.Bool(GetText(name));
        public void Set(string name, string value) => Find(name, true).value = value ?? "";
        public void Set(string name, float value) => Set(name, value.ToString(CultureInfo.InvariantCulture));
        public void Set(string name, bool value) => Set(name, value ? "true" : "false");
    }
}
