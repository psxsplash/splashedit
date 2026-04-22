using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using SplashEdit.RuntimeCode;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// Downloads psxavenc and converts WAV audio to PS1 SPU ADPCM format.
    /// psxavenc is the standard tool for PS1 audio encoding from the
    /// WonderfulToolchain project.
    /// </summary>
    [InitializeOnLoad]
    public static class PSXAudioConverter
    {
        static PSXAudioConverter()
        {
            // Register the converter delegate so Runtime code can call it
            // without directly referencing this Editor assembly.
            PSXSceneExporter.AudioConvertDelegate = ConvertToADPCM;
        }

        private const string PSXAVENC_VERSION = "v0.3.1";
        private const string PSXAVENC_RELEASE_BASE = 
            "https://github.com/WonderfulToolchain/psxavenc/releases/download/";

        private static readonly HttpClient _http = new HttpClient();

        /// <summary>
        /// Path to the psxavenc binary inside .tools/
        /// </summary>
        public static string PsxavencBinary
        {
            get
            {
                string dir = Path.Combine(SplashBuildPaths.ToolsDir, "psxavenc");
                if (Application.platform == RuntimePlatform.WindowsEditor)
                    return Path.Combine(dir, "psxavenc.exe");
                return Path.Combine(dir, "psxavenc");
            }
        }

        public static bool IsInstalled() => File.Exists(PsxavencBinary);

        public static bool CheckMacOSBuildDeps(out string missing)
        {
            var list = new System.Collections.Generic.List<string>();
            if (!HasCommand("meson")) list.Add("meson");
            if (!HasCommand("pkg-config")) list.Add("pkg-config");

            bool hasFFmpeg =
                File.Exists("/opt/homebrew/lib/pkgconfig/libavformat.pc") ||
                File.Exists("/usr/local/lib/pkgconfig/libavformat.pc") ||
                File.Exists("/opt/homebrew/opt/ffmpeg/lib/pkgconfig/libavformat.pc") ||
                File.Exists("/usr/local/opt/ffmpeg/lib/pkgconfig/libavformat.pc");
            if (!hasFFmpeg) list.Add("ffmpeg");

            missing = string.Join(" ", list);
            return list.Count == 0;
        }

        /// <summary>
        /// Downloads and installs psxavenc from the official GitHub releases.
        /// </summary>
        public static async Task<bool> DownloadAndInstall(Action<string> log = null)
        {
            // macOS: no prebuilt binary available — try to build from source
            if (Application.platform == RuntimePlatform.OSXEditor)
                return await BuildFromSourceMacOS(log);

            string archiveName;
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    archiveName = $"psxavenc-windows.zip";
                    break;
                case RuntimePlatform.LinuxEditor:
                    archiveName = $"psxavenc-linux.zip";
                    break;
                default:
                    log?.Invoke("Unsupported platform.");
                    return false;
            }

            string downloadUrl = $"{PSXAVENC_RELEASE_BASE}{PSXAVENC_VERSION}/{archiveName}";
            log?.Invoke($"Downloading psxavenc: {downloadUrl}");

            try
            {
                string tempFile = Path.Combine(Path.GetTempPath(), archiveName);
                EditorUtility.DisplayProgressBar("Downloading psxavenc", "Downloading...", 0.1f);

                using (var client = new System.Net.WebClient())
                {
                    client.Headers.Add("User-Agent", "SplashEdit/1.0");

                    client.DownloadProgressChanged += (s, e) =>
                    {
                        float progress = 0.1f + 0.8f * (e.ProgressPercentage / 100f);
                        string sizeMB = $"{e.BytesReceived / (1024 * 1024)}/{e.TotalBytesToReceive / (1024 * 1024)} MB";
                        EditorUtility.DisplayProgressBar("Downloading psxavenc", $"Downloading... {sizeMB}", progress);
                    };

                    await client.DownloadFileTaskAsync(new Uri(downloadUrl), tempFile);
                }

                log?.Invoke("Extracting...");
                EditorUtility.DisplayProgressBar("Installing psxavenc", "Extracting...", 0.9f);

                string installDir = Path.Combine(SplashBuildPaths.ToolsDir, "psxavenc");
                if (Directory.Exists(installDir))
                    Directory.Delete(installDir, true);
                Directory.CreateDirectory(installDir);

                if (tempFile.EndsWith(".zip"))
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(tempFile, installDir);
                }
                else
                {
                    // tar.gz extraction — use system tar
                    var psi = new ProcessStartInfo
                    {
                        FileName = "tar",
                        Arguments = $"xzf \"{tempFile}\" -C \"{installDir}\" --strip-components=1",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    var proc = Process.Start(psi);
                    proc.WaitForExit();
                }

                // Fix nested directory (sometimes archives have one extra level)
                SplashEdit.RuntimeCode.Utils.FixNestedDirectory(installDir);

                try { File.Delete(tempFile); } catch { }

                EditorUtility.ClearProgressBar();

                if (IsInstalled())
                {
                    // Make executable on Linux
                    if (Application.platform == RuntimePlatform.LinuxEditor)
                    {
                        var chmod = Process.Start("chmod", $"+x \"{PsxavencBinary}\"");
                        chmod?.WaitForExit();
                    }
                    log?.Invoke("psxavenc installed successfully!");
                    return true;
                }

                log?.Invoke($"psxavenc binary not found at: {PsxavencBinary}");
                return false;
            }
            catch (Exception ex)
            {
                log?.Invoke($"psxavenc download failed: {ex.Message}");
                EditorUtility.ClearProgressBar();
                return false;
            }
        }

        /// <summary>
        /// macOS: no prebuilt binary exists. Clone the repo and build with meson.
        /// Requires: brew install meson pkg-config ffmpeg
        /// </summary>
        private static async Task<bool> BuildFromSourceMacOS(Action<string> log)
        {
            string installDir = Path.Combine(SplashBuildPaths.ToolsDir, "psxavenc");
            string srcDir = Path.Combine(installDir, "src");
            string buildDir = Path.Combine(srcDir, "build");

            // Safety check — the UI should prevent getting here without deps,
            // but guard against direct calls
            string missingDeps;
            if (!CheckMacOSBuildDeps(out missingDeps))
            {
                log?.Invoke($"Missing build dependencies: {missingDeps}. " +
                            $"Run: brew install {missingDeps}");
                return false;
            }

            try
            {
                if (Directory.Exists(installDir))
                    Directory.Delete(installDir, true);
                Directory.CreateDirectory(installDir);

                log?.Invoke("Building psxavenc (clone + configure + compile)...");
                EditorUtility.DisplayProgressBar("Building psxavenc", "This may take a minute...", 0.2f);

                // Run the entire build sequence on a background thread.
                // Individual progress bar updates can't happen mid-build since
                // Unity UI must be called from the main thread, but this keeps
                // the editor responsive during the build.
                string capturedInstallDir = installDir;
                string capturedSrcDir = srcDir;
                string capturedBuildDir = buildDir;
                string resultLog = "";
                bool buildOk = false;

                await Task.Run(() =>
                {
                    string o, e;

                    // Clone
                    int rc = RunToolCaptured("git",
                        $"clone --depth 1 --branch {PSXAVENC_VERSION} " +
                        $"https://github.com/WonderfulToolchain/psxavenc.git \"{capturedSrcDir}\"",
                        out o, out e);
                    if (rc != 0)
                    {
                        resultLog = $"Git clone failed (exit {rc}).\n{e.TrimEnd()}";
                        return;
                    }

                    // Meson setup
                    rc = RunToolCaptured("meson",
                        $"setup \"{capturedBuildDir}\" \"{capturedSrcDir}\" --prefix=\"{capturedInstallDir}\"",
                        out o, out e);
                    if (rc != 0)
                    {
                        resultLog = $"Meson setup failed (exit {rc}).\n{o.TrimEnd()}\n{e.TrimEnd()}";
                        string mlog = Path.Combine(capturedBuildDir, "meson-logs", "meson-log.txt");
                        if (File.Exists(mlog))
                            try { resultLog += $"\nmeson-log.txt:\n{File.ReadAllText(mlog)}"; } catch { }
                        return;
                    }

                    // Meson compile
                    rc = RunToolCaptured("meson", $"compile -C \"{capturedBuildDir}\"",
                        out o, out e);
                    if (rc != 0)
                    {
                        resultLog = $"Compilation failed (exit {rc}).\n{o.TrimEnd()}\n{e.TrimEnd()}";
                        return;
                    }

                    // Copy binary
                    string built = Path.Combine(capturedBuildDir, "psxavenc");
                    if (File.Exists(built))
                    {
                        File.Copy(built, PsxavencBinary, true);
                        RunToolCaptured("chmod", $"+x \"{PsxavencBinary}\"", out _, out _);
                        buildOk = true;
                    }
                    else
                    {
                        resultLog = "Build completed but psxavenc binary not found.";
                    }
                });

                if (!buildOk)
                {
                    log?.Invoke(resultLog);
                    return false;
                }

                log?.Invoke("psxavenc built and installed successfully!");
                try { Directory.Delete(srcDir, true); } catch { }

                EditorUtility.ClearProgressBar();
                return IsInstalled();
            }
            catch (Exception ex)
            {
                log?.Invoke($"Build failed: {ex.Message}");
                EditorUtility.ClearProgressBar();
                return false;
            }
        }

        private static bool HasCommand(string cmd)
        {
            return RunTool("which", cmd) == 0;
        }

        private static int RunTool(string fileName, string arguments)
        {
            return RunToolCaptured(fileName, arguments, out _, out _);
        }

        // Mono/.NET on macOS resolves a bare FileName (e.g. "meson") against the
        // PARENT process's PATH, not psi.Environment["PATH"]. Unity launched from
        // Finder/Hub inherits launchd's PATH (/usr/bin:/bin only), so bare commands
        // fail with "Cannot find the specified file" even though AugmentPath sets
        // the child's PATH correctly. ResolveCommand fixes this by finding the
        // absolute path before Process.Start sees it.
        private static int RunToolCaptured(string fileName, string arguments,
                                           out string stdout, out string stderr)
        {
            stdout = ""; stderr = "";
            try
            {
                string resolved = ResolveCommand(fileName);
                var psi = new ProcessStartInfo
                {
                    FileName = resolved,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                AugmentPath(psi);
                using (var proc = Process.Start(psi))
                {
                    var outTask = proc.StandardOutput.ReadToEndAsync();
                    var errTask = proc.StandardError.ReadToEndAsync();
                    proc.WaitForExit();
                    stdout = outTask.Result ?? "";
                    stderr = errTask.Result ?? "";
                    return proc.ExitCode;
                }
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                return -1;
            }
        }

        private static string ResolveCommand(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (name.StartsWith("/") || name.StartsWith("./") || name.StartsWith("../"))
                return name;
            if (Application.platform != RuntimePlatform.OSXEditor &&
                Application.platform != RuntimePlatform.LinuxEditor)
                return name;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] dirs = new[]
            {
                "/opt/homebrew/bin",
                "/usr/local/bin",
                Path.Combine(home, "bin"),
                Path.Combine(home, ".local", "bin"),
                "/usr/bin",
                "/bin",
                "/usr/sbin",
                "/sbin"
            };
            foreach (var d in dirs)
            {
                string candidate = Path.Combine(d, name);
                if (File.Exists(candidate)) return candidate;
            }

            // Fallback: ask the shell
            try
            {
                var probe = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = $"-c \"command -v '{name}'\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                string extra = $"/opt/homebrew/bin:/usr/local/bin:{home}/bin";
                string current = probe.Environment.ContainsKey("PATH")
                    ? probe.Environment["PATH"] : "";
                probe.Environment["PATH"] = string.IsNullOrEmpty(current)
                    ? extra : $"{extra}:{current}";
                using (var p = Process.Start(probe))
                {
                    string found = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    if (!string.IsNullOrEmpty(found) && File.Exists(found)) return found;
                }
            }
            catch { }

            return name;
        }

        // macOS GUI apps (launched via Finder/Hub) inherit launchd's env, which
        // has no Homebrew paths and no shell profile settings. We must inject
        // PATH, PKG_CONFIG_PATH, and clear PKG_CONFIG_LIBDIR for every child
        // process, or tools like pkg-config and meson silently fail to find
        // libraries that work fine in Terminal.
        private static void AugmentPath(ProcessStartInfo psi)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // /opt/homebrew/bin = ARM Mac, /usr/local/bin = Intel Mac
            string extra = $"/opt/homebrew/bin:/usr/local/bin:{home}/bin";
            string current = psi.Environment.ContainsKey("PATH") ? psi.Environment["PATH"] : "";
            psi.Environment["PATH"] = $"{extra}:{current}";

            // pkg-config needs to find Homebrew's .pc files for ffmpeg
            string pcExtra =
                "/opt/homebrew/lib/pkgconfig:" +
                "/opt/homebrew/share/pkgconfig:" +
                "/opt/homebrew/opt/ffmpeg/lib/pkgconfig:" +
                "/usr/local/lib/pkgconfig:" +
                "/usr/local/share/pkgconfig:" +
                "/usr/local/opt/ffmpeg/lib/pkgconfig";
            string pcCurrent = psi.Environment.ContainsKey("PKG_CONFIG_PATH")
                ? psi.Environment["PKG_CONFIG_PATH"] : "";
            psi.Environment["PKG_CONFIG_PATH"] = string.IsNullOrEmpty(pcCurrent)
                ? pcExtra : $"{pcExtra}:{pcCurrent}";

            // PKG_CONFIG_LIBDIR replaces the compiled-in pc_path entirely.
            // If something in the launchd env set it, pkg-config won't find
            // Homebrew's .pc files even with PKG_CONFIG_PATH set.
            if (psi.Environment.ContainsKey("PKG_CONFIG_LIBDIR"))
                psi.Environment.Remove("PKG_CONFIG_LIBDIR");
        }

        /// <summary>
        /// Converts a Unity AudioClip to PS1 SPU ADPCM format using psxavenc.
        /// Returns the ADPCM byte array, or null on failure.
        /// </summary>
        public static byte[] ConvertToADPCM(AudioClip clip, int targetSampleRate, bool loop)
        {
            if (!IsInstalled())
            {
                Debug.LogError("[SplashEdit] psxavenc not installed. Install it from the Setup tab.");
                return null;
            }

            if (clip == null)
            {
                Debug.LogError("[SplashEdit] AudioClip is null.");
                return null;
            }

            // Export Unity AudioClip to a temporary WAV file
            string tempWav = Path.Combine(Path.GetTempPath(), $"psx_audio_{clip.name}.wav");
            string tempVag = Path.Combine(Path.GetTempPath(), $"psx_audio_{clip.name}.vag");

            try
            {
                ExportWav(clip, tempWav);

                // Run psxavenc: convert WAV to SPU ADPCM
                // -t spu: raw SPU ADPCM output (no header, ready for DMA upload)
                // -f <rate>: target sample rate
                // -L: enable looping flag in the last ADPCM block
                string loopFlag = loop ? "-L" : "";
                string args = $"-t spu -f {targetSampleRate} {loopFlag} \"{tempWav}\" \"{tempVag}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = PsxavencBinary,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var process = Process.Start(psi);
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Debug.LogError($"[SplashEdit] psxavenc failed: {stderr}");
                    return null;
                }

                if (!File.Exists(tempVag))
                {
                    Debug.LogError("[SplashEdit] psxavenc produced no output file.");
                    return null;
                }

                // -t spu outputs raw SPU ADPCM blocks (no header) — use directly.
                byte[] adpcm = File.ReadAllBytes(tempVag);
                if (adpcm.Length == 0)
                {
                    Debug.LogError("[SplashEdit] psxavenc produced empty output.");
                    return null;
                }
                return adpcm;
            }
            finally
            {
                try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
                try { if (File.Exists(tempVag)) File.Delete(tempVag); } catch { }
            }
        }

        /// <summary>
        /// Exports a Unity AudioClip to a 16-bit mono WAV file.
        /// </summary>
        private static void ExportWav(AudioClip clip, string path)
        {
            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);

            // Downmix to mono if stereo
            float[] mono;
            if (clip.channels > 1)
            {
                mono = new float[clip.samples];
                for (int i = 0; i < clip.samples; i++)
                {
                    float sum = 0;
                    for (int ch = 0; ch < clip.channels; ch++)
                        sum += samples[i * clip.channels + ch];
                    mono[i] = sum / clip.channels;
                }
            }
            else
            {
                mono = samples;
            }

            // Write WAV
            using (var fs = new FileStream(path, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
                int sampleCount = mono.Length;
                int dataSize = sampleCount * 2; // 16-bit
                int fileSize = 44 + dataSize;

                // RIFF header
                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(fileSize - 8);
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });

                // fmt chunk
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16); // chunk size
                writer.Write((short)1); // PCM
                writer.Write((short)1); // mono
                writer.Write(clip.frequency);
                writer.Write(clip.frequency * 2); // byte rate
                writer.Write((short)2); // block align
                writer.Write((short)16); // bits per sample

                // data chunk
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(dataSize);

                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = (short)(Mathf.Clamp(mono[i], -1f, 1f) * 32767f);
                    writer.Write(sample);
                }
            }
        }
    }
}
