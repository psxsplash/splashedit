using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// Builds the native psxsplash engine with xmake. The engine's xmake.lua sits
    /// beside its Makefile; xmake is used when both the file and the tool are
    /// there, and the Makefile build stays the fallback otherwise.
    /// Every invocation is an argument list, never a shell string, so paths with
    /// spaces reach xmake untouched.
    /// </summary>
    public static class XMakeBuild
    {
        /// <summary>The standalone xmake that ToolchainInstaller downloads.</summary>
        public static string ManagedBinary => Path.Combine(
            SplashBuildPaths.ToolsDir, "xmake",
            Application.platform == RuntimePlatform.WindowsEditor ? "xmake.exe" : "xmake");

        /// <summary>
        /// The xmake to run: the downloaded copy if present, else one on PATH,
        /// else null.
        /// </summary>
        public static string FindXMake()
        {
            if (File.Exists(ManagedBinary)) return ManagedBinary;
            if (ToolchainChecker.IsToolAvailable("xmake")) return "xmake";
            return null;
        }

        public static bool IsAvailable() => FindXMake() != null;

        /// <summary>True when the engine tree has an xmake.lua and xmake can be found.</summary>
        public static bool CanBuild(string nativeDir) =>
            !string.IsNullOrEmpty(nativeDir) &&
            File.Exists(Path.Combine(nativeDir, "xmake.lua")) &&
            IsAvailable();

        public static string ModeName() =>
            SplashSettings.Mode == BuildMode.Debug ? "debug" : "release";

        /// <summary>Where xmake leaves the .ps-exe for the current mode.</summary>
        public static string OutputDir(string nativeDir) =>
            Path.Combine(nativeDir, "build", "psx", "mipsel", ModeName());

        /// <summary>
        /// Arguments for "xmake f". Every option is passed explicitly and the
        /// config cache is reset with -c, so a setting that was switched off
        /// does not survive from the previous configure.
        /// </summary>
        public static List<string> ConfigureArgs(string nativeDir, string features, bool noparser)
        {
            var args = new List<string>
            {
                "f", "-c", "-y", "-P", nativeDir, "-m", ModeName(),
                "--profiler=" + YesNo(SplashSettings.ProfilerOverlay),
                "--noparser=" + YesNo(noparser),
                "--ot_size=" + SplashSettings.OtSize,
                "--bump_size=" + SplashSettings.BumpSize,
            };
            if (!string.IsNullOrEmpty(features))
                args.Add("--features=" + features);
            return args;
        }

        /// <summary>
        /// The engine target for the current settings. psxsplash's xmake.lua has
        /// one target per build, each with its own file: psxsplash (PCdrv),
        /// psxsplash-cdrom (ISO), and the PCdrv builds with overlays, -perf (FPS
        /// counter and heap bar together), -rooms and -perf-rooms. Every name
        /// ends in -nologo when the features leave out bootlogo. The ISO build
        /// has no overlay variant.
        /// </summary>
        public static string EngineTarget(string features)
        {
            string name = "psxsplash";
            if (SplashSettings.Target == BuildTarget.ISO)
                name += "-cdrom";
            else
            {
                bool perf = SplashSettings.MemoryOverlay || SplashSettings.FpsOverlay;
                bool rooms = SplashSettings.RoomDebugOverlay;
                if (perf && rooms) name += "-perf-rooms";
                else if (perf) name += "-perf";
                else if (rooms) name += "-rooms";
            }
            if (!string.IsNullOrEmpty(features) &&
                !features.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Contains("bootlogo"))
                name += "-nologo";
            return name;
        }

        /// <summary>True when the settings ask for an overlay the target cannot have.</summary>
        public static bool OverlaysDropped() =>
            SplashSettings.Target == BuildTarget.ISO &&
            (SplashSettings.MemoryOverlay || SplashSettings.FpsOverlay || SplashSettings.RoomDebugOverlay);

        public static List<string> BuildArgs(string nativeDir, int jobs, string target) =>
            new List<string> { "build", "-y", "-j", jobs.ToString(), "-P", nativeDir, target + ".ps-exe" };

        public static List<string> CleanArgs(string nativeDir) =>
            new List<string> { "clean", "-y", "-P", nativeDir };

        private static string YesNo(bool b) => b ? "y" : "n";
    }
}
