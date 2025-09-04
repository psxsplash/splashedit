using UnityEngine;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace SplashEdit.EditorCode
{

    public static class PSXSplashInstaller
    {
        public static readonly string RepoUrl = "https://github.com/psxsplash/psxsplash.git";
        public static readonly string InstallPath = "Assets/psxsplash";
        public static readonly string FullInstallPath;

        static PSXSplashInstaller()
        {
            FullInstallPath = Path.Combine(Application.dataPath, "psxsplash");
        }

        public static bool IsInstalled()
        {
            return Directory.Exists(FullInstallPath) &&
                   Directory.EnumerateFileSystemEntries(FullInstallPath).Any();
        }

        public static async Task<bool> Install()
        {
            if (IsInstalled()) return true;

            try
            {
                // Create the parent directory if it doesn't exist
                Directory.CreateDirectory(Application.dataPath);

                // Clone the repository
                var result = await RunGitCommandAsync($"clone --recursive {RepoUrl} \"{FullInstallPath}\"", Application.dataPath);
                return !result.Contains("error");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"Failed to install PSXSplash: {e.Message}");
                return false;
            }
        }

        public static async Task<Dictionary<string, string>> GetBranchesWithLatestCommitsAsync()
        {
            if (!IsInstalled()) return new Dictionary<string, string>();

            try
            {
                // Fetch all branches and tags
                await RunGitCommandAsync("fetch --all", FullInstallPath);

                // Get all remote branches
                var branchesOutput = await RunGitCommandAsync("branch -r", FullInstallPath);
                var branches = branchesOutput.Split('\n')
                    .Where(b => !string.IsNullOrEmpty(b.Trim()))
                    .Select(b => b.Trim().Replace("origin/", ""))
                    .Where(b => !b.Contains("HEAD"))
                    .ToList();

                var branchesWithCommits = new Dictionary<string, string>();

                // Get the latest commit for each branch
                foreach (var branch in branches)
                {
                    var commitOutput = await RunGitCommandAsync($"log origin/{branch} -1 --pretty=format:%h", FullInstallPath);
                    if (!string.IsNullOrEmpty(commitOutput))
                    {
                        branchesWithCommits[branch] = commitOutput.Trim();
                    }
                }

                return branchesWithCommits;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"Failed to get branches: {e.Message}");
                return new Dictionary<string, string>();
            }
        }

        public static async Task<List<string>> GetReleasesAsync()
        {
            if (!IsInstalled()) return new List<string>();

            try
            {
                await RunGitCommandAsync("fetch --tags", FullInstallPath);
                var output = await RunGitCommandAsync("tag -l", FullInstallPath);

                return output.Split('\n')
                    .Where(t => !string.IsNullOrEmpty(t.Trim()))
                    .Select(t => t.Trim())
                    .ToList();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"Failed to get releases: {e.Message}");
                return new List<string>();
            }
        }

        public static async Task<bool> CheckoutVersionAsync(string version)
        {
            if (!IsInstalled()) return false;

            try
            {
                // If it's a branch name, checkout the branch
                // If it's a commit hash, checkout the commit
                var result = await RunGitCommandAsync($"checkout {version}", FullInstallPath);
                var result2 = await RunGitCommandAsync("submodule update --init --recursive", FullInstallPath);

                return !result.Contains("error") && !result2.Contains("error");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"Failed to checkout version: {e.Message}");
                return false;
            }
        }

        public static async Task<bool> FetchLatestAsync()
        {
            if (!IsInstalled()) return false;

            try
            {
                var result = await RunGitCommandAsync("fetch --all", FullInstallPath);
                return !result.Contains("error");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"Failed to fetch latest: {e.Message}");
                return false;
            }
        }

        private static async Task<string> RunGitCommandAsync(string arguments, string workingDirectory)
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process())
            {
                process.StartInfo = processInfo;
                var outputBuilder = new System.Text.StringBuilder();
                var errorBuilder = new System.Text.StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        outputBuilder.AppendLine(e.Data);
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        errorBuilder.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait for exit with timeout
                var timeout = TimeSpan.FromSeconds(30);
                if (await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds)))
                {
                    process.WaitForExit(); // Ensure all output is processed

                    string output = outputBuilder.ToString();
                    string error = errorBuilder.ToString();

                    if (!string.IsNullOrEmpty(error))
                    {
                        UnityEngine.Debug.LogError($"Git error: {error}");
                    }

                    return output;
                }
                else
                {
                    process.Kill();
                    throw new TimeoutException("Git command timed out");
                }
            }
        }
    }
}