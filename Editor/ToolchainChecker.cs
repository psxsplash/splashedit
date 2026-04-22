using UnityEngine;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System;

/// <summary>
/// Utility that detects whether required build tools (MIPS cross-compiler,
/// GNU Make, GDB, etc.) are available on the host system by probing the
/// PATH via <c>where</c> (Windows) or <c>which</c> (Unix).
/// </summary>
namespace SplashEdit.EditorCode
{
public static class ToolchainChecker
{
    private static readonly string[] mipsToolSuffixes = new[]
    {
        "addr2line", "ar", "as", "cpp", "elfedit", "g++", "gcc", "gcc-ar", "gcc-nm",
        "gcc-ranlib", "gcov", "ld", "nm", "objcopy", "objdump", "ranlib", "readelf",
        "size", "strings", "strip"
    };

    /// <summary>
    /// Returns the full tool names to be checked, based on platform.
    /// </summary>
    public static string[] GetRequiredTools()
    {
        string prefix = Application.platform == RuntimePlatform.WindowsEditor
            ? "mipsel-none-elf-"
            : "mipsel-linux-gnu-";

        return mipsToolSuffixes.Select(s => prefix + s).ToArray();
    }

    /// <summary>
    /// Checks for availability of any tool (either full name like "make" or "mipsel-*").
    /// </summary>
    public static bool IsToolAvailable(string toolName)
    {
        string command = Application.platform == RuntimePlatform.WindowsEditor ? "where" : "which";

        try
        {
            // macOS GUI apps have restricted PATH — check common locations directly
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string[] searchPaths = new[] {
                    Path.Combine(home, "mipsel-none-elf", "bin"),
                    "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"
                };
                string[] variants = toolName.Contains("mipsel-linux-gnu")
                    ? new[] { toolName, toolName.Replace("mipsel-linux-gnu", "mipsel-none-elf") }
                    : toolName.Contains("mipsel-none-elf")
                    ? new[] { toolName, toolName.Replace("mipsel-none-elf", "mipsel-linux-gnu") }
                    : new[] { toolName };
                foreach (var dir in searchPaths)
                    foreach (var variant in variants)
                        if (File.Exists(Path.Combine(dir, variant)))
                            return true;
            }
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = toolName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string extra = $"/opt/homebrew/bin:/usr/local/bin:{home}/bin:{home}/mipsel-none-elf/bin";
                string current = psi.Environment.ContainsKey("PATH") ? psi.Environment["PATH"] : "";
                psi.Environment["PATH"] = $"{extra}:{current}";
            }
            Process process = new Process { StartInfo = psi };

            process.Start();
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(output))
                return true;

            // Additional fallback for MIPS tools on Windows in local MIPS path
            if (Application.platform == RuntimePlatform.WindowsEditor &&
                toolName.StartsWith("mipsel-none-elf-"))
            {
                string localMipsBin = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "mips", "mips", "bin");

                string fullPath = Path.Combine(localMipsBin, toolName + ".exe");
                return File.Exists(fullPath);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
}
