#!/usr/bin/env python3
"""Unity rules a C# compiler does not check. Exit code 1 on any problem.
- Every MonoBehaviour / StateMachineBehaviour / ScriptableObject lives in a file of its own name
  (Unity cannot find the script otherwise: "Can't find monoscript for class").
- No `GetComponent<T>() ?? ...`: a missing component is a fake null in the Editor that ?? keeps.
- No APIs Unity 6 made obsolete-as-error (the reference assemblies here are Unity 2021, which allow them)."""
import os, re, sys
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "unity", "ZimensionUnity", "Packages", "com.zimension.runtime")
BASES = ("MonoBehaviour", "StateMachineBehaviour", "ScriptableObject", "PlayerTarget", "ZimensionLogic", "EditorWindow")
problems = []
for dirpath, _, files in os.walk(ROOT):
    for name in files:
        if not name.endswith(".cs"): continue
        path = os.path.join(dirpath, name); text = open(path, encoding="utf-8").read()
        for m in re.finditer(r"class\s+(\w+)\s*:\s*([\w.]+)", text):
            if m.group(2).split(".")[-1] in BASES and m.group(1) != name[:-3]:
                problems.append(f"{os.path.relpath(path, ROOT)}: {m.group(1)} must be in {m.group(1)}.cs")
        for i, line in enumerate(text.splitlines(), 1):
            if re.search(r"GetComponent(InChildren|InParent)?<\w+>\([^)]*\)\s*\?\?", line):
                problems.append(f"{os.path.relpath(path, ROOT)}:{i}: GetComponent ?? (use GetOrAdd / TryGetComponent)")
# Obsolete (errors or noisy warnings) in Unity 6.x.
OBSOLETE = {r"\.GetInstanceID\(": "GetInstanceID (use RuntimeHelpers.GetHashCode / EntityId)", r"InstanceIDToObject\(": "InstanceIDToObject",
            r"\bFindObjectOfType\b": "FindObjectOfType (use FindAnyObjectByType)", r"\bFindObjectsOfType\b": "FindObjectsOfType (use FindObjectsByType)",
            r"AppDomain\.CurrentDomain\.GetAssemblies\(": "AppDomain.GetAssemblies (use TypeCache / CompilationPipeline)"}
for dirpath, _, files in os.walk(ROOT):
    for name in files:
        if not name.endswith(".cs"): continue
        path = os.path.join(dirpath, name)
        for i, line in enumerate(open(path, encoding="utf-8").read().splitlines(), 1):
            for pattern, what in OBSOLETE.items():
                if re.search(pattern, line): problems.append(f"{os.path.relpath(path, ROOT)}:{i}: {what}")
for p in problems: print(p)
print(f"Unity rules: {len(problems)} problem(s)")
sys.exit(1 if problems else 0)
