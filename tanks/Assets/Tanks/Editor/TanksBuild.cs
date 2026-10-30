using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Tanks.Editor
{
    public static class TanksBuild
    {
        /// <summary>
        /// One Windows build with all four netcodes: it starts on TanksSettings.netcode, <c>-netcode</c>
        /// on the command line or the HUD buttons pick another.
        /// </summary>
        [MenuItem("Tanks/Build Windows client")]
        public static void BuildWindows()
        {
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = "Builds/Windows/Tanks.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log($"Tanks build: {report.summary.result}, {report.summary.totalErrors} error(s)");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
