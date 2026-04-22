using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// Downloads and installs PCSX-Redux from the official distrib.app CDN.
    /// Mirrors the logic from pcsx-redux.js (the official download script).
    /// 
    /// Flow: fetch platform manifest → find latest build ID → fetch build manifest →
    ///       get download URL → download zip → extract to .tools/pcsx-redux/
    /// </summary>
    public static class PCSXReduxDownloader
    {
        private const string MANIFEST_BASE = "https://distrib.app/storage/manifests/pcsx-redux/";

        private static readonly HttpClient _http;

        static PCSXReduxDownloader()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                       | System.Net.DecompressionMethods.Deflate
            };
            _http = new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(60);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("SplashEdit/1.0");
        }

        /// <summary>
        /// Returns the platform variant string for the current platform. 
        /// </summary> 
        private static string GetPlatformVariant()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    return "dev-win-cli-x64"; 
                case RuntimePlatform.LinuxEditor:
                    return "dev-linux-x64";
                case RuntimePlatform.OSXEditor:
                    return "dev-macos-arm";
                default:
                    return "dev-win-cli-x64";
            }
        }

        /// <summary>
        /// Downloads and installs PCSX-Redux to .tools/pcsx-redux/.
        /// Shows progress bar during download.
        /// </summary>
        public static async Task<bool> DownloadAndInstall(Action<string> log = null)
        {
            string variant = GetPlatformVariant();
            log?.Invoke($"Platform variant: {variant}");

            try
            {
                // Step 1: Fetch the master manifest to get the latest build ID
                string manifestUrl = $"{MANIFEST_BASE}{variant}/manifest.json";
                log?.Invoke($"Fetching manifest: {manifestUrl}");
                string manifestJson = await _http.GetStringAsync(manifestUrl);

                // Parse the latest build ID from the manifest.
                // The manifest is JSON with a "builds" array. We want the highest ID.
                // Simple JSON parsing without dependencies:
                int latestBuildId = ParseLatestBuildId(manifestJson);
                if (latestBuildId < 0)
                {
                    log?.Invoke("Failed to parse build ID from manifest.");
                    return false;
                }
                log?.Invoke($"Latest build ID: {latestBuildId}");

                // Step 2: Fetch the specific build manifest
                string buildManifestUrl = $"{MANIFEST_BASE}{variant}/manifest-{latestBuildId}.json";
                log?.Invoke($"Fetching build manifest...");
                string buildManifestJson = await _http.GetStringAsync(buildManifestUrl);

                // Parse the download path
                string downloadPath = ParseDownloadPath(buildManifestJson);
                if (string.IsNullOrEmpty(downloadPath))
                {
                    log?.Invoke("Failed to parse download path from build manifest.");
                    return false;
                }

                string downloadUrl = $"https://distrib.app{downloadPath}";
                log?.Invoke($"Downloading: {downloadUrl}");

                // Step 3: Download — use the real extension from the URL
                string ext = ".zip";
                if (downloadPath.EndsWith(".tar.gz")) ext = ".tar.gz";
                else if (downloadPath.EndsWith(".dmg")) ext = ".dmg";
                else if (downloadPath.EndsWith(".zip")) ext = ".zip";

                string tempFile = Path.Combine(Path.GetTempPath(), $"pcsx-redux-{latestBuildId}{ext}");
                EditorUtility.DisplayProgressBar("Downloading PCSX-Redux", "Downloading...", 0.1f);

                using (var client = new System.Net.WebClient())
                {
                    client.Headers.Add("User-Agent", "SplashEdit/1.0");

                    client.DownloadProgressChanged += (s, e) =>
                    {
                        float progress = 0.1f + 0.8f * (e.ProgressPercentage / 100f);
                        string sizeMB = $"{e.BytesReceived / (1024 * 1024)}/{e.TotalBytesToReceive / (1024 * 1024)} MB";
                        EditorUtility.DisplayProgressBar("Downloading PCSX-Redux", $"Downloading... {sizeMB}", progress);
                    };

                    await client.DownloadFileTaskAsync(new Uri(downloadUrl), tempFile);
                }

                log?.Invoke($"Downloaded to {tempFile}");
                EditorUtility.DisplayProgressBar("Installing PCSX-Redux", "Extracting...", 0.9f);

                // Step 4: Extract — handle zip, tar.gz, and dmg
                string installDir = SplashBuildPaths.PCSXReduxDir;
                if (Directory.Exists(installDir))
                    Directory.Delete(installDir, true);
                Directory.CreateDirectory(installDir);

                // distrib.app serves different formats per platform:
                // macOS = DMG, Linux = zip (not tar.gz despite the old code),
                // Windows = zip. The temp file extension is derived from the
                // download URL so the right branch fires.
                if (tempFile.EndsWith(".dmg"))
                {
                    ExtractDMG(tempFile, installDir, log);
                }
                else if (tempFile.EndsWith(".tar.gz"))
                {
                    RunSystemCommand("tar", $"xzf \"{tempFile}\" -C \"{installDir}\" --strip-components=1");
                }
                else
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(tempFile, installDir);
                    log?.Invoke($"Extracted to {installDir}");
                }

                // Platform-specific post-install
                if (Application.platform == RuntimePlatform.OSXEditor)
                {
                    PostInstallMacOS(installDir, log);
                }
                else if (Application.platform == RuntimePlatform.LinuxEditor)
                {
                    RunSystemCommand("chmod", $"+x \"{SplashBuildPaths.PCSXReduxBinary}\"");
                }

                // Clean up temp file
                try { File.Delete(tempFile); } catch { }

                // Step 5: Verify
                if (SplashBuildPaths.IsPCSXReduxInstalled())
                {
                    log?.Invoke("PCSX-Redux installed successfully!");
                    EditorUtility.ClearProgressBar();
                    return true;
                }
                else
                {
                    // The zip might have a nested directory — try to find the exe
                    SplashEdit.RuntimeCode.Utils.FixNestedDirectory(installDir);
                    if (SplashBuildPaths.IsPCSXReduxInstalled())
                    {
                        log?.Invoke("PCSX-Redux installed successfully!");
                        EditorUtility.ClearProgressBar();
                        return true;
                    }

                    log?.Invoke("Installation completed but PCSX-Redux binary not found at expected path.");
                    log?.Invoke($"Expected: {SplashBuildPaths.PCSXReduxBinary}");
                    log?.Invoke($"Check: {installDir}");
                    EditorUtility.ClearProgressBar();
                    return false;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Download failed: {ex.Message}");
                EditorUtility.ClearProgressBar();
                return false;
            }
        }

        /// <summary>
        /// Mount a DMG, copy PCSX-Redux.app to the install directory, unmount.
        /// </summary>
        private static void ExtractDMG(string dmgPath, string installDir, Action<string> log)
        {
            string mountPoint = Path.Combine(Path.GetTempPath(),
                $"pcsx-redux-mount-{Path.GetFileNameWithoutExtension(dmgPath)}");

            // Clean up stale mount from a previous failed attempt
            if (Directory.Exists(mountPoint))
                RunSystemCommand("hdiutil", $"detach \"{mountPoint}\" -quiet -force");

            // Mount
            if (RunSystemCommand("hdiutil", $"attach \"{dmgPath}\" -mountpoint \"{mountPoint}\" -nobrowse -quiet") != 0)
            {
                log?.Invoke("Failed to mount DMG.");
                return;
            }
            log?.Invoke("Mounted DMG.");

            try
            {
                // Find the .app inside the mounted volume
                string appSource = null;
                foreach (string dir in Directory.GetDirectories(mountPoint, "*.app"))
                {
                    appSource = dir;
                    break;
                }

                if (appSource != null)
                {
                    string appDest = Path.Combine(installDir, Path.GetFileName(appSource));
                    if (RunSystemCommand("cp", $"-R \"{appSource}\" \"{appDest}\"") != 0)
                        log?.Invoke("Failed to copy .app bundle from DMG.");
                    else
                        log?.Invoke($"Copied {Path.GetFileName(appSource)} to tools directory.");
                }
                else
                {
                    log?.Invoke("No .app bundle found in DMG.");
                }
            }
            finally
            {
                RunSystemCommand("hdiutil", $"detach \"{mountPoint}\" -quiet");
                log?.Invoke("Unmounted DMG.");
            }
        }

        /// <summary>
        /// macOS post-install: the distrib.app download is a .app bundle, but
        /// SplashBuildPaths.PCSXReduxBinary expects a flat "pcsx-redux" at the
        /// install root. Create a wrapper script, symlink the font share directory,
        /// and strip quarantine attributes.
        /// </summary>
        private static void PostInstallMacOS(string installDir, Action<string> log)
        {
            string appBinary = Path.Combine(installDir,
                "PCSX-Redux.app", "Contents", "MacOS", "PCSX-Redux");
            string wrapperPath = SplashBuildPaths.PCSXReduxBinary;

            // SplashBuildPaths.PCSXReduxBinary expects a flat "pcsx-redux" executable,
            // but distrib.app ships a .app bundle. This wrapper bridges the gap.
            // It also handles process cleanup — without pkill, PCSX-Redux sometimes
            // survives Unity's Process.Kill and blocks the port on next launch.
            if (File.Exists(appBinary))
            {
                string wrapper =
                    "#!/bin/bash\n" +
                    "DIR=\"$(cd \"$(dirname \"$0\")\" && pwd)\"\n" +
                    "APP=\"$DIR/PCSX-Redux.app/Contents/MacOS/PCSX-Redux\"\n" +
                    "LOG=\"$DIR/pcsx-redux.log\"\n" +
                    "echo \"=== PCSX-Redux Launch ===\" > \"$LOG\"\n" +
                    "echo \"Args: $@\" >> \"$LOG\"\n" +
                    "echo \"Date: $(date)\" >> \"$LOG\"\n" +
                    "echo \"=========================\" >> \"$LOG\"\n" +
                    "pkill -f \"PCSX-Redux.app/Contents/MacOS/PCSX-Redux\" 2>/dev/null\n" +
                    "sleep 0.2\n" +
                    "\"$APP\" \"$@\" 2>&1 | tee -a \"$LOG\"\n" +
                    "EXIT=${PIPESTATUS[0]}\n" +
                    "echo \"Exit code: $EXIT\" >> \"$LOG\"\n" +
                    "pkill -f \"PCSX-Redux.app/Contents/MacOS/PCSX-Redux\" 2>/dev/null\n" +
                    "exit $EXIT\n";

                File.WriteAllText(wrapperPath, wrapper);
                RunSystemCommand("chmod", $"+x \"{wrapperPath}\"");
                log?.Invoke("Created macOS launcher wrapper.");
            }

            // When launched directly (not via macOS `open`), PCSX-Redux looks for
            // share/pcsx-redux/fonts/ relative to the binary. Inside the .app bundle,
            // fonts are in Contents/Resources/share/ but the binary is in Contents/MacOS/.
            // Without this symlink, ImGui asserts and the emulator crashes on startup.
            string macosDir = Path.Combine(installDir,
                "PCSX-Redux.app", "Contents", "MacOS");
            string shareLink = Path.Combine(macosDir, "share");
            if (!File.Exists(shareLink) && !Directory.Exists(shareLink))
            {
                RunSystemCommand("ln", $"-sf \"../Resources/share\" \"{shareLink}\"");
                log?.Invoke("Created font share symlink.");
            }

            // Belt-and-suspenders: the symlink above should suffice, but on some
            // macOS configs ImGui still fails to load fonts from the bundle path.
            // Copying to ~/Library/Fonts/ makes it reliable. overwrite:false avoids
            // clobbering user fonts that happen to share a name.
            string fontsDir = Path.Combine(installDir,
                "PCSX-Redux.app", "Contents", "Resources", "share", "pcsx-redux", "fonts");
            if (Directory.Exists(fontsDir))
            {
                string userFonts = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Fonts");
                if (Directory.Exists(userFonts))
                {
                    int copied = 0;
                    foreach (string font in Directory.GetFiles(fontsDir))
                    {
                        string ext = Path.GetExtension(font).ToLower();
                        if (ext == ".ttf" || ext == ".otf")
                        {
                            string dest = Path.Combine(userFonts, Path.GetFileName(font));
                            try { File.Copy(font, dest, false); copied++; } catch { }
                        }
                    }
                    if (copied > 0)
                        log?.Invoke($"Copied {copied} emulator font(s) to ~/Library/Fonts/ (prevents ImGui crash).");
                }
            }

            // 4. Strip quarantine attributes
            string appPath = Path.Combine(installDir, "PCSX-Redux.app");
            RunSystemCommand("xattr", $"-cr \"{appPath}\"");
            log?.Invoke("Cleared macOS quarantine attributes.");
        }

        private static int RunSystemCommand(string cmd, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit();
                return proc?.ExitCode ?? -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Parse the latest build ID from the master manifest JSON.
        /// Expected format: {"builds":[{"id":1234,...},...],...}
        /// distrib.app returns builds sorted newest-first, so we take the first.
        /// Falls back to scanning all IDs if the "builds" section isn't found.
        /// </summary>
        private static int ParseLatestBuildId(string json)
        {
            // Fast path: find the first "id" inside "builds" array
            int buildsIdx = json.IndexOf("\"builds\"", StringComparison.Ordinal);
            int startPos = buildsIdx >= 0 ? buildsIdx : 0;

            string searchToken = "\"id\":";
            int idx = json.IndexOf(searchToken, startPos, StringComparison.Ordinal);
            if (idx < 0) return -1;

            int pos = idx + searchToken.Length;
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;

            int numStart = pos;
            while (pos < json.Length && char.IsDigit(json[pos])) pos++;

            if (pos > numStart && int.TryParse(json.Substring(numStart, pos - numStart), out int id))
                return id;

            return -1;
        }

        /// <summary>
        /// Parse the download path from a build-specific manifest.
        /// Expected format: {...,"path":"/storage/builds/..."}
        /// </summary>
        private static string ParseDownloadPath(string json)
        {
            string searchToken = "\"path\":";
            int idx = json.IndexOf(searchToken, StringComparison.Ordinal);
            if (idx < 0) return null;

            int pos = idx + searchToken.Length;
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;

            if (pos >= json.Length || json[pos] != '"') return null;
            pos++; // skip opening quote

            int pathStart = pos;
            while (pos < json.Length && json[pos] != '"') pos++;

            return json.Substring(pathStart, pos - pathStart);
        }
    }
}
