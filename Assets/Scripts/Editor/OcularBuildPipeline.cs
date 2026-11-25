// -----------------------------------------------------------------------------
// Ocular -- build entry points.
//
// The single most important behaviour here is EXITING NON-ZERO ON FAILURE. Unity
// in batch mode returns 0 from a failed -executeMethod build unless you quit with
// an explicit code, so a script that trusts the exit status will happily report
// success for a build that produced no binary at all.
// -----------------------------------------------------------------------------

using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Ocular.Core.Diagnostics;

namespace Ocular.Editor
{
    public static class OcularBuildPipeline
    {
        private const string DefaultOutputRoot = "Build";

        private static string[] Scenes
        {
            get
            {
                return EditorBuildSettings.scenes
                    .Where(s => s.enabled)
                    .Select(s => s.path)
                    .ToArray();
            }
        }

        // =====================================================================

        [MenuItem("Ocular/Build/Windows Player", priority = 100)]
        public static void BuildWindowsMenu() { BuildWindows(); }

        public static void BuildWindows()
        {
            Run(new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = Path.Combine(OutputRoot(), "Windows", "Ocular.exe"),
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None
            }, "Windows Player");
        }

        /// <summary>
        /// Windows player WITH logging, built to Build/WindowsDev.
        ///
        /// <para>OLog.Info and OLog.Warn are [Conditional] on UNITY_EDITOR and
        /// DEVELOPMENT_BUILD, so a release player is deliberately silent. That is
        /// right for shipping and useless for diagnosis: a release Player.log
        /// showing nothing cannot distinguish "the study loaded fine" from "the
        /// loader never ran", because both print exactly nothing.</para>
        ///
        /// <para>Built to a separate folder so it can never be mistaken for, or
        /// overwrite, the release build.</para>
        /// </summary>
        [MenuItem("Ocular/Build/Windows Player (development)", priority = 102)]
        public static void BuildWindowsDevMenu() { BuildWindowsDev(); }

        public static void BuildWindowsDev()
        {
            Run(new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = Path.Combine(OutputRoot(), "WindowsDev", "Ocular.exe"),
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.Development
            }, "Windows Player (development)");
        }

        [MenuItem("Ocular/Build/WebGL", priority = 101)]
        public static void BuildWebGLMenu() { BuildWebGL(); }

        /// <summary>
        /// Browser build. This is the one that carries VR and AR once WebXR is
        /// installed, because a single WebGL page serves the desktop view, the
        /// Quest browser and Android Chrome.
        /// </summary>
        public static void BuildWebGL()
        {
            // WebXR needs two things that are easy to forget and silent when missing:
            // the WebGL template that carries the JavaScript glue, and the WebXR
            // loader actually ASSIGNED to the WebGL target in XR Plug-in Management.
            // Without either, the page loads and renders fine and the Enter VR
            // button simply never becomes available.
            OcularXrSetup.ConfigureWebXr();

            // Gzip with the decompression fallback ON. Brotli is smaller but needs
            // the host to send matching Content-Encoding headers; get that wrong and
            // the page is blank with only a console error. The fallback works on any
            // static host, including GitHub Pages, at a small size cost.
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.linkerTarget = WebGLLinkerTarget.Wasm;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.WebGL.dataCaching = true;

            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Low);

            Run(new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = Path.Combine(OutputRoot(), "WebGL"),
                target = BuildTarget.WebGL,
                options = BuildOptions.None
            }, "WebGL");
        }

        // =====================================================================

        private static string OutputRoot()
        {
            string fromArgs = ReadArg("-buildOutput");
            if (!string.IsNullOrEmpty(fromArgs)) return fromArgs;

            string fromEnv = Environment.GetEnvironmentVariable("OCULAR_BUILD_OUTPUT");
            return string.IsNullOrEmpty(fromEnv) ? DefaultOutputRoot : fromEnv;
        }

        private static void Run(BuildPlayerOptions options, string label)
        {
            if (options.scenes == null || options.scenes.Length == 0)
            {
                Fail(label + ": no scenes in Build Settings. Run 'Ocular > Generate Simulation Scene' first.");
                return;
            }

            string directory = Path.GetDirectoryName(options.locationPathName);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            OLog.Info(LogCat.Build, "Building " + label + " -> " + options.locationPathName);

            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            catch (Exception ex)
            {
                Fail(label + ": build threw - " + ex.Message);
                return;
            }

            BuildSummary summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                // Surface the real errors. Without this the log shows only
                // "Build failed", which sends someone hunting through 40k lines.
                foreach (BuildStep step in report.steps)
                {
                    foreach (BuildStepMessage message in step.messages)
                    {
                        if (message.type == LogType.Error || message.type == LogType.Exception)
                        {
                            OLog.Error(LogCat.Build, "[" + step.name + "] " + message.content);
                        }
                    }
                }

                Fail(label + ": " + summary.result + " (" + summary.totalErrors + " error(s)).");
                return;
            }

            OLog.Info(LogCat.Build, string.Format(
                "{0} SUCCEEDED: {1:F1} MB in {2:F0}s -> {3}",
                label,
                summary.totalSize / (1024f * 1024f),
                summary.totalTime.TotalSeconds,
                summary.outputPath));

            Succeed();
        }

        private static string ReadArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }
            return null;
        }

        private static void Succeed()
        {
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        private static void Fail(string reason)
        {
            OLog.Error(LogCat.Build, "BUILD FAILED: " + reason);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
