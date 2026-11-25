// -----------------------------------------------------------------------------
// Ocular — categorised logging.
//
// Info/Warn are [Conditional] so a release WebGL player carries neither the call
// nor the string concatenation that would have built its argument. That matters
// more here than on desktop: the measurement engine re-runs every frame while a
// parameter slider is being dragged, and an unguarded interpolated string per
// sample is hundreds of allocations a second on a platform with no spare GC
// headroom.
// -----------------------------------------------------------------------------

using System.Diagnostics;

namespace Ocular.Core.Diagnostics
{
    public enum LogCat
    {
        Core,
        Anatomy,
        Measure,
        Interaction,
        UI,
        Build
    }

    public static class OLog
    {
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Info(LogCat cat, string message)
            => UnityEngine.Debug.Log($"[{cat}] {message}");

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Warn(LogCat cat, string message)
            => UnityEngine.Debug.LogWarning($"[{cat}] {message}");

        // Deliberately NOT conditional. An error that only reproduces in a release
        // player is exactly the one you need the log line for.
        public static void Error(LogCat cat, string message)
            => UnityEngine.Debug.LogError($"[{cat}] {message}");
    }
}
