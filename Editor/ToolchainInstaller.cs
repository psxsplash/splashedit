using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using System.IO;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// Installs the MIPS cross-compiler toolchain and GNU Make.
    /// Auto-installs on Windows and Linux. Guides macOS users through manual setup.
    /// </summary>
    public static class ToolchainInstaller
    {
        private static bool _installing;

        public static string MipsVersion = "14.2.0";

        public static bool HasXcodeCommandLineTools()
        {
            if (Application.platform != RuntimePlatform.OSXEditor) return true;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "xcode-select",
                    Arguments = "-p",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                var proc = Process.Start(psi);
                proc.WaitForExit();
                return proc.ExitCode == 0;
            }
            catch { return false; }
        }

        public static void PromptXcodeInstall()
        {
            bool install = EditorUtility.DisplayDialog(
                "macOS: Xcode Command Line Tools Required",
                "Xcode Command Line Tools must be installed first.\n" +
                "They provide GNU Make, Git, and other build essentials.\n\n" +
                "Click Install to open the system installer.",
                "Install", "Cancel");
            if (install)
            {
                Process.Start("xcode-select", "--install");
            }
        }

        /// <summary>
        /// Runs an external process and waits for it to exit.
        /// </summary>
        public static async Task RunCommandAsync(string fileName, string arguments, string workingDirectory = "")
        {
            if (fileName.Equals("mips", StringComparison.OrdinalIgnoreCase))
            {
                fileName = "powershell";
                string roamingPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string scriptPath = Path.Combine(roamingPath, "mips", "mips.ps1");
                arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\" {arguments}";
            }

            var tcs = new TaskCompletionSource<int>();

            Process process = new Process();
            process.StartInfo.FileName = fileName;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.CreateNoWindow = false;
            process.StartInfo.UseShellExecute = true;

            if (!string.IsNullOrEmpty(workingDirectory))
                process.StartInfo.WorkingDirectory = workingDirectory;

            process.EnableRaisingEvents = true;
            process.Exited += (sender, args) =>
            {
                tcs.SetResult(process.ExitCode);
                process.Dispose();
            };

            process.Start();

            int exitCode = await tcs.Task;
            if (exitCode != 0)
                throw new Exception($"Process '{fileName}' exited with code {exitCode}");
        }

        /// <summary>
        /// Installs the MIPS GCC cross-compiler for the current platform.
        /// </summary>
        public static async Task<bool> InstallToolchain()
        {
            if (_installing) return false;
            _installing = true;

            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    if (!ToolchainChecker.IsToolAvailable("mips"))
                    {
                        await RunCommandAsync("powershell",
                            "-c \"& { iwr -UseBasicParsing https://raw.githubusercontent.com/grumpycoders/pcsx-redux/main/mips.ps1 | iex }\"");
                        EditorUtility.DisplayDialog("Reboot Required",
                            "Installing the MIPS toolchain requires a reboot. Please reboot and try again.",
                            "OK");
                        return false;
                    }
                    else
                    {
                        await RunCommandAsync("mips", $"install {MipsVersion}");
                    }
                }
                else if (Application.platform == RuntimePlatform.LinuxEditor)
                {
                    if (ToolchainChecker.IsToolAvailable("apt"))
                        await RunCommandAsync("pkexec", "apt install g++-mipsel-linux-gnu -y");
                    else if (ToolchainChecker.IsToolAvailable("trizen"))
                        await RunCommandAsync("trizen", "-S cross-mipsel-linux-gnu-binutils cross-mipsel-linux-gnu-gcc");
                    else
                        throw new Exception("Unsupported Linux distribution. Install mipsel-linux-gnu-gcc manually.");
                }
                else if (Application.platform == RuntimePlatform.OSXEditor)
                {
                    if (!HasXcodeCommandLineTools())
                    {
                        PromptXcodeInstall();
                        return false;
                    }
                    // No prebuilt MIPS compiler exists for macOS. The pcsx-redux
                    // project maintains formula files that build GCC from source.
                    // brew-install-path is needed because Homebrew 4.6.4+ removed
                    // support for installing from local .rb formula files directly.
                    // The resulting "nikitabobko/local-tap" is a LOCAL directory on
                    // the user's machine, not a GitHub repo — don't reference it
                    // in install instructions.
                    bool hasBrew = ToolchainChecker.IsToolAvailable("brew");
                    string installScript =
                        "brew install nikitabobko/tap/brew-install-path && " +
                        "curl -LO https://raw.githubusercontent.com/grumpycoders/pcsx-redux/main/tools/macos-mips/mipsel-none-elf-binutils.rb && " +
                        "curl -LO https://raw.githubusercontent.com/grumpycoders/pcsx-redux/main/tools/macos-mips/mipsel-none-elf-gcc.rb && " +
                        "brew install-path ./mipsel-none-elf-binutils.rb && " +
                        "brew install-path ./mipsel-none-elf-gcc.rb";
                    if (hasBrew)
                    {
                        EditorUtility.DisplayDialog(
                            "macOS: Install MIPS Cross-Compiler",
                            "Paste this into Terminal (copied to clipboard):\n\n" +
                            "  brew install nikitabobko/tap/brew-install-path\n" +
                            "  curl -LO https://raw.githubusercontent.com/grumpycoders/\n" +
                            "    pcsx-redux/main/tools/macos-mips/mipsel-none-elf-binutils.rb\n" +
                            "  curl -LO https://raw.githubusercontent.com/grumpycoders/\n" +
                            "    pcsx-redux/main/tools/macos-mips/mipsel-none-elf-gcc.rb\n" +
                            "  brew install-path ./mipsel-none-elf-binutils.rb\n" +
                            "  brew install-path ./mipsel-none-elf-gcc.rb\n\n" +
                            "Builds GCC from source — expect 15-30 minutes.\n" +
                            "Then click Refresh in the Dependencies tab.",
                            "OK");
                        GUIUtility.systemCopyBuffer = installScript;
                    }
                    else
                    {
                        EditorUtility.DisplayDialog(
                            "macOS: Install Homebrew + MIPS Cross-Compiler",
                            "Homebrew (the macOS package manager) is required.\n\n" +
                            "Step 1 — Install Homebrew:\n" +
                            "  /bin/bash -c \"$(curl -fsSL\n" +
                            "    https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"\n\n" +
                            "Step 2 — Install the MIPS compiler (paste into Terminal):\n" +
                            "  brew install nikitabobko/tap/brew-install-path\n" +
                            "  curl -LO <grumpycoders pcsx-redux formula URLs>\n" +
                            "  brew install-path ./mipsel-none-elf-binutils.rb\n" +
                            "  brew install-path ./mipsel-none-elf-gcc.rb\n\n" +
                            "Step 2 builds GCC from source — expect 15-30 minutes.\n" +
                            "The Homebrew install command has been copied to your clipboard.\n" +
                            "Then click Refresh in the Dependencies tab.",
                            "OK");
                        GUIUtility.systemCopyBuffer =
                            "/bin/bash -c \"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"";
                    }
                    return false;
                }
                else
                {
                    throw new Exception("Unsupported platform.");
                }
                return true;
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Error",
                    $"Toolchain installation failed: {ex.Message}", "OK");
                return false;
            }
            finally
            {
                _installing = false;
            }
        }

        /// <summary>
        /// Installs GNU Make. On Windows it is bundled with the MIPS toolchain.
        /// </summary>
        public static async Task InstallMake()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                bool proceed = EditorUtility.DisplayDialog(
                    "Install GNU Make",
                    "On Windows, GNU Make is included with the MIPS toolchain installer. Install the full toolchain?",
                    "Yes", "No");
                if (proceed) await InstallToolchain();
            }
            else if (Application.platform == RuntimePlatform.LinuxEditor)
            {
                if (ToolchainChecker.IsToolAvailable("apt"))
                    await RunCommandAsync("pkexec", "apt install build-essential -y");
                else
                    throw new Exception("Unsupported Linux distribution. Install 'make' manually.");
            }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            {
                PromptXcodeInstall();
            }
            else
            {
                throw new Exception("Unsupported platform.");
            }
        }
    }
}
