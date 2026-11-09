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

        /// <summary>
        /// The same game for the browser. It needs a <c>wss://</c> relay address (browsers have no TCP/UDP sockets), and
        /// reads the options from the page URL: <c>index.html?relay=wss://...&amp;host</c> or <c>&amp;join=ABCD</c>.
        /// Uncompressed so any static file server can serve it.
        /// </summary>
        [MenuItem("Tanks/Build WebGL client")]
        public static void BuildWebGL()
        {
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.dataCaching = false;
            PlayerSettings.runInBackground = true;   // keep the game (and a browser host) running in a background tab
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = "Builds/WebGL",
                target = BuildTarget.WebGL,
                options = BuildOptions.None,
            });
            Debug.Log($"Tanks WebGL build: {report.summary.result}, {report.summary.totalErrors} error(s)");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
