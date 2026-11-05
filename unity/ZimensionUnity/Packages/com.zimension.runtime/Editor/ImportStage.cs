using System.IO;
using UnityEngine;

namespace Zimension.Editor
{
    /// <summary>
    /// Crash safety for imports: the step being worked on is written to
    /// Library/ZimensionImportStage.txt as it starts and removed when the import ends. If Unity
    /// stops in the middle (a native crash in a model, avatar or terrain step), the next import
    /// knows where, says so, and skips that kind of step.
    /// </summary>
    internal static class ImportStage
    {
        static readonly string File_ = Path.GetFullPath("Library/ZimensionImportStage.txt");

        /// <summary>The step a previous import was on when Unity stopped, or null.</summary>
        public static string Previous()
        {
            try { return File.Exists(File_) ? File.ReadAllText(File_).Trim() : null; } catch { return null; }
        }

        public static void Mark(string stage)
        {
            try { File.WriteAllText(File_, stage ?? ""); } catch { }
        }

        public static void End()
        {
            try { if (File.Exists(File_)) File.Delete(File_); } catch { }
        }

        public static void Warn(string previous) =>
            Debug.LogWarning($"[Zimension] The previous import stopped while working on: {previous}. That step is skipped this time; please send this line with your report.");
    }
}
