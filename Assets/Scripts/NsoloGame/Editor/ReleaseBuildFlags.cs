using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace NsoloGame.EditorTools
{
    /// <summary>
    /// The small white "Development Build" line in the corner of the APK.
    ///
    /// It is not drawn by anything in this project — Unity stamps it onto every player built with
    /// <b>Development Build</b> ticked in File &gt; Build Settings, and it cannot be styled, moved
    /// or hidden from inside the game. The only way to remove it is to build with that box clear,
    /// which is what <see cref="ClearDevelopmentFlags"/> does in one click: the checkbox lives in
    /// Library/EditorUserBuildSettings.asset rather than in the project, so it is per-machine and
    /// easy to leave ticked from whenever profiling was last needed.
    ///
    /// The preprocessor below then makes sure a ticked box cannot go unnoticed: it fails the build
    /// rather than quietly producing a watermarked APK. Ticking the box is still perfectly allowed
    /// — the message says which menu item to use when a development build is what you actually
    /// want.
    /// </summary>
    public static class ReleaseBuildFlags
    {
        private const string AllowDevelopmentKey = "Nsolo.AllowDevelopmentBuild";

        [MenuItem("Nsolo/Build/Clear Development Build Flags", priority = 100)]
        public static void ClearDevelopmentFlags()
        {
            EditorUserBuildSettings.development = false;
            EditorUserBuildSettings.allowDebugging = false;
            EditorUserBuildSettings.connectProfiler = false;
            EditorUserBuildSettings.buildWithDeepProfilingSupport = false;
            SessionState.EraseBool(AllowDevelopmentKey);

            Debug.Log("[Nsolo] Development Build, Script Debugging, Autoconnect Profiler and Deep "
                    + "Profiling are all off. The next build will have no watermark.");
        }

        /// <summary>
        /// Lets a development build through for this editor session, for when the profiler or a
        /// device log is genuinely wanted. Forgotten automatically when the editor is closed, which
        /// is the point — the release setting is the one that survives.
        /// </summary>
        [MenuItem("Nsolo/Build/Allow a Development Build (this session)", priority = 101)]
        public static void AllowDevelopmentBuild()
        {
            SessionState.SetBool(AllowDevelopmentKey, true);
            Debug.Log("[Nsolo] Development builds allowed until this editor session ends. The APK "
                    + "will carry the \"Development Build\" watermark.");
        }

        private class Guard : IPreprocessBuildWithReport
        {
            public int callbackOrder => 0;

            public void OnPreprocessBuild(BuildReport report)
            {
                if (!EditorUserBuildSettings.development) return;
                if (SessionState.GetBool(AllowDevelopmentKey, false)) return;

                throw new BuildFailedException(
                    "Development Build is ticked, so this APK would carry the white "
                  + "\"Development Build\" watermark. Run Nsolo > Build > Clear Development Build "
                  + "Flags and build again, or Nsolo > Build > Allow a Development Build (this "
                  + "session) if the watermark is wanted.");
            }
        }
    }
}
