using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Mirror.LightNetRelay.Editor
{
    public static class LightNetRelayBuild
    {
        const string Scene = "Assets/Mirror/Examples/Tanks/Scenes/Scene.unity";

        [MenuItem("LightNet Relay/Build Windows client")]
        public static void BuildWindows()
        {
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = "Builds/Windows/Tanks.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log($"LightNet relay build: {report.summary.result}, {report.summary.totalErrors} error(s)");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
