using UnityEditor;
using UnityEngine;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// Enumerates the pipeline target for builds.
    /// </summary>
    public enum BuildTarget
    {
        Emulator,     // PCSX-Redux with PCdrv
        RealHardware, // Send .ps-exe over serial via Unirom
        ISO           // Build a CD image
    }

    /// <summary>
    /// Enumerates the build configuration.
    /// </summary>
    public enum BuildMode
    {
        Debug,
        Release
    }

    /// <summary>
    /// Centralized EditorPrefs-backed settings for the SplashEdit pipeline.
    /// All settings are project-scoped using a prefix derived from the project path.
    /// </summary>
    public static class SplashSettings
    {
        // Prefix all keys with project path hash to support multiple projects
        internal static string Prefix => "SplashEdit_" + Application.dataPath.GetHashCode().ToString("X8") + "_";

        // --- Build settings ---
        public static BuildTarget Target
        {
            get => (BuildTarget)EditorPrefs.GetInt(Prefix + "Target", (int)BuildTarget.Emulator);
            set => EditorPrefs.SetInt(Prefix + "Target", (int)value);
        }

        public static BuildMode Mode
        {
            get => (BuildMode)EditorPrefs.GetInt(Prefix + "Mode", (int)BuildMode.Release);
            set => EditorPrefs.SetInt(Prefix + "Mode", (int)value);
        }

        // --- Toolchain paths ---
        public static string NativeProjectPath
        {
            get => EditorPrefs.GetString(Prefix + "NativeProjectPath", "");
            set => EditorPrefs.SetString(Prefix + "NativeProjectPath", value);
        }

        public static string MIPSToolchainPath
        {
            get => EditorPrefs.GetString(Prefix + "MIPSToolchainPath", "");
            set => EditorPrefs.SetString(Prefix + "MIPSToolchainPath", value);
        }

        // --- PCSX-Redux ---
        public static string PCSXReduxPath
        {
            get
            {
                string custom = EditorPrefs.GetString(Prefix + "PCSXReduxPath", "");
                if (!string.IsNullOrEmpty(custom))
                    return custom;
                // Fall back to auto-downloaded location
                if (SplashBuildPaths.IsPCSXReduxInstalled())
                    return SplashBuildPaths.PCSXReduxBinary;
                return "";
            }
            set => EditorPrefs.SetString(Prefix + "PCSXReduxPath", value);
        }

        public static string PCSXReduxPCdrvBase
        {
            get => EditorPrefs.GetString(Prefix + "PCSXReduxPCdrvBase", SplashBuildPaths.BuildOutputDir);
            set => EditorPrefs.SetString(Prefix + "PCSXReduxPCdrvBase", value);
        }

        // --- Serial / Real Hardware ---
        public static string SerialPort
        {
            get => EditorPrefs.GetString(Prefix + "SerialPort", "COM3");
            set => EditorPrefs.SetString(Prefix + "SerialPort", value);
        }

        public static int SerialBaudRate
        {
            get => EditorPrefs.GetInt(Prefix + "SerialBaudRate", 115200);
            set => EditorPrefs.SetInt(Prefix + "SerialBaudRate", value);
        }

        // --- VRAM Layout (hardcoded 320x240, dual-buffered, vertical) ---
        public static int ResolutionWidth
        {
            get => 320;
            set { } // no-op, hardcoded
        }

        public static int ResolutionHeight
        {
            get => 240;
            set { } // no-op, hardcoded
        }

        public static bool DualBuffering
        {
            get => true;
            set { } // no-op, hardcoded
        }

        public static bool VerticalLayout
        {
            get => true;
            set { } // no-op, hardcoded
        }

        // --- Clean Build ---
        public static bool CleanBuild
        {
            get => EditorPrefs.GetBool(Prefix + "CleanBuild", true);
            set => EditorPrefs.SetBool(Prefix + "CleanBuild", value);
        }

        // --- Memory Overlay ---
        /// <summary>
        /// When enabled, compiles the runtime with a heap/RAM usage progress bar
        /// and text overlay at the top-right corner of the screen.
        /// Passes MEMOVERLAY=1 to the native Makefile.
        /// </summary>
        public static bool MemoryOverlay
        {
            get => EditorPrefs.GetBool(Prefix + "MemoryOverlay", false);
            set => EditorPrefs.SetBool(Prefix + "MemoryOverlay", value);
        }


        // --- FPS Overlay ---
        /// <summary>
        /// When enabled, compiles the runtime with an FPS counter
        /// and text overlay at the top-left corner of the screen.
        /// Passes FPSOVERLAY=1 to the native Makefile.
        /// </summary>
        public static bool FpsOverlay
        {
            get => EditorPrefs.GetBool(Prefix + "FpsOverlay", false);
            set => EditorPrefs.SetBool(Prefix + "FpsOverlay", value);
        }

        // --- Room Debug Overlay ---
        /// <summary>
        /// When enabled, compiles the runtime with a debug overlay that renders
        /// ALL room triangles in per-room colors on top of the scene.
        /// Useful for visualizing portal/room topology and diagnosing culling issues.
        /// Passes ROOMDEBUG=1 to the native Makefile.
        /// </summary>
        public static bool RoomDebugOverlay
        {
            get => EditorPrefs.GetBool(Prefix + "RoomDebugOverlay", false);
            set => EditorPrefs.SetBool(Prefix + "RoomDebugOverlay", value);
        }

        // --- Profiler Overlay ---
        /// <summary>
        /// When enabled, compiles the runtime with a per-frame profiler overlay
        /// that renders a pie chart and timing breakdown on screen.
        /// Passes PROFILER=1 to the native Makefile.
        /// </summary>
        public static bool ProfilerOverlay
        {
            get => EditorPrefs.GetBool(Prefix + "ProfilerOverlay", false);
            set => EditorPrefs.SetBool(Prefix + "ProfilerOverlay", value);
        }

        // --- Renderer sizes ---
        public static int OtSize
        {
            get => EditorPrefs.GetInt(Prefix + "OtSize", 2048 * 4);
            set => EditorPrefs.SetInt(Prefix + "OtSize", value);
        }

        public static int BumpSize
        {
            get => EditorPrefs.GetInt(Prefix + "BumpSize", 8096 * 16);
            set => EditorPrefs.SetInt(Prefix + "BumpSize", value);
        }

        // --- Export settings ---
        public static float DefaultGTEScaling
        {
            get => EditorPrefs.GetFloat(Prefix + "GTEScaling", 100f);
            set => EditorPrefs.SetFloat(Prefix + "GTEScaling", value);
        }

        // --- ISO Build ---
        /// <summary>
        /// Optional path to a Sony license file (.dat) for the ISO image.
        /// If empty, the ISO will be built without license data (homebrew-only).
        /// The file must be in raw 2336-byte sector format (from PsyQ SDK LCNSFILE).
        /// </summary>
        public static string LicenseFilePath
        {
            get => EditorPrefs.GetString(Prefix + "LicenseFilePath", SplashBuildPaths.DefaultLicenseFilePath);
            set => EditorPrefs.SetString(Prefix + "LicenseFilePath", value);
        }

        /// <summary>
        /// Volume label for the ISO image (up to 31 characters, uppercase).
        /// </summary>
        public static string ISOVolumeLabel
        {
            get => EditorPrefs.GetString(Prefix + "ISOVolumeLabel", "PSXSPLASH");
            set => EditorPrefs.SetString(Prefix + "ISOVolumeLabel", value);
        }

        /// <summary>
        /// Resets all settings to defaults by deleting all prefixed keys.
        /// </summary>
        public static void ResetAll()
        {
            string[] keys = new[]
            {
                "Target", "Mode", "NativeProjectPath", "MIPSToolchainPath",
                "PCSXReduxPath", "PCSXReduxPCdrvBase", "SerialPort", "SerialBaudRate",
                "ResWidth", "ResHeight", "DualBuffering", "VerticalLayout",
                "GTEScaling", "AutoValidate",
                "LicenseFilePath", "ISOVolumeLabel",
                "OtSize", "BumpSize",
                "MemoryOverlay", "FpsOverlay", "RoomDebugOverlay", "ProfilerOverlay",
                "CleanBuild"
            };

            foreach (string key in keys)
            {
                EditorPrefs.DeleteKey(Prefix + key);
            }
        }
    }
}
