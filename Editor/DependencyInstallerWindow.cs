using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using SplashEdit.RuntimeCode;
using System.IO;
using System.Linq;
using System.Threading.Tasks;


namespace SplashEdit.EditorCode
{
    public class InstallerWindow : EditorWindow
    {
        // Cached status for MIPS toolchain binaries.
        private Dictionary<string, bool> mipsToolStatus = new Dictionary<string, bool>();

        // Cached status for optional tools.
        private bool makeInstalled;
        private bool gdbInstalled;
        private string pcsxReduxPath;

        // PSXSplash related variables
        private bool psxsplashInstalled = false;
        private bool psxsplashInstalling = false;
        private bool psxsplashFetching = false;
        private string selectedVersion = "main";
        private Dictionary<string, string> availableBranches = new Dictionary<string, string>();
        private List<string> availableReleases = new List<string>();
        private bool showBranches = true;
        private bool showReleases = false;
        private Vector2 scrollPosition;
        private Vector2 versionScrollPosition;

        private bool isInstalling = false;

        [MenuItem("PSX/Toolchain & Build Tools Installer")]
        public static void ShowWindow()
        {
            InstallerWindow window = GetWindow<InstallerWindow>("Toolchain Installer");
            window.RefreshToolStatus();
            window.pcsxReduxPath = DataStorage.LoadData().PCSXReduxPath;
            window.CheckPSXSplashInstallation();
        }

        /// <summary>
        /// Refresh the cached statuses for all tools.
        /// </summary>
        private void RefreshToolStatus()
        {
            mipsToolStatus.Clear();
            foreach (var tool in ToolchainChecker.GetRequiredTools())
            {
                mipsToolStatus[tool] = ToolchainChecker.IsToolAvailable(tool);
            }

            makeInstalled = ToolchainChecker.IsToolAvailable("make");
            gdbInstalled = ToolchainChecker.IsToolAvailable("gdb-multiarch");
        }

        private void CheckPSXSplashInstallation()
        {
            psxsplashInstalled = PSXSplashInstaller.IsInstalled();

            if (psxsplashInstalled)
            {
                FetchPSXSplashVersions();
            }
            else
            {
                availableBranches = new Dictionary<string, string>();
                availableReleases = new List<string>();
            }
        }

        private async void FetchPSXSplashVersions()
        {
            if (psxsplashFetching) return;

            psxsplashFetching = true;
            try
            {
                // Fetch latest from remote
                await PSXSplashInstaller.FetchLatestAsync();

                // Get all available versions
                var branchesTask = PSXSplashInstaller.GetBranchesWithLatestCommitsAsync();
                var releasesTask = PSXSplashInstaller.GetReleasesAsync();

                await Task.WhenAll(branchesTask, releasesTask);

                availableBranches = branchesTask.Result;
                availableReleases = releasesTask.Result;

                // If no branches were found, add main as default
                if (!availableBranches.Any())
                {
                    availableBranches["main"] = "latest";
                }

                // Select the first branch by default
                if (availableBranches.Any() && string.IsNullOrEmpty(selectedVersion))
                {
                    selectedVersion = availableBranches.Keys.First();
                }

                Repaint();
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"Failed to fetch PSXSplash versions: {e.Message}");
            }
            finally
            {
                psxsplashFetching = false;
            }
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            GUILayout.Label("Toolchain & Build Tools Installer", EditorStyles.boldLabel);
            GUILayout.Space(5);

            if (GUILayout.Button("Refresh Status"))
            {
                RefreshToolStatus();
                CheckPSXSplashInstallation();
            }
            GUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            DrawToolchainColumn();
            DrawAdditionalToolsColumn();
            DrawPSXSplashColumn();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
        }

        private void DrawToolchainColumn()
        {
            EditorGUILayout.BeginVertical("box", GUILayout.MaxWidth(position.width / 3 - 10));
            GUILayout.Label("MIPS Toolchain", EditorStyles.boldLabel);
            GUILayout.Space(5);

            // Display cached status for each required MIPS tool.
            foreach (var kvp in mipsToolStatus)
            {
                GUI.color = kvp.Value ? Color.green : Color.red;
                GUILayout.Label($"{kvp.Key}: {(kvp.Value ? "Found" : "Missing")}");
            }
            GUI.color = Color.white;
            GUILayout.Space(5);

            if (GUILayout.Button("Install MIPS Toolchain"))
            {
                if (!isInstalling)
                    InstallMipsToolchainAsync();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawAdditionalToolsColumn()
        {
            EditorGUILayout.BeginVertical("box", GUILayout.MaxWidth(position.width / 3 - 10));
            GUILayout.Label("Optional Tools", EditorStyles.boldLabel);
            GUILayout.Space(5);

            // GNU Make status (required).
            GUI.color = makeInstalled ? Color.green : Color.red;
            GUILayout.Label($"GNU Make: {(makeInstalled ? "Found" : "Missing")} (Required)");
            GUI.color = Color.white;
            GUILayout.Space(5);
            if (GUILayout.Button("Install GNU Make"))
            {
                if (!isInstalling)
                    InstallMakeAsync();
            }

            GUILayout.Space(10);

            // GDB status (optional).
            GUI.color = gdbInstalled ? Color.green : Color.red;
            GUILayout.Label($"GDB: {(gdbInstalled ? "Found" : "Missing")} (Optional)");
            GUI.color = Color.white;
            GUILayout.Space(5);
            if (GUILayout.Button("Install GDB"))
            {
                if (!isInstalling)
                    InstallGDBAsync();
            }

            GUILayout.Space(10);

            // PCSX-Redux (manual install)
            GUI.color = string.IsNullOrEmpty(pcsxReduxPath) ? Color.red : Color.green;
            GUILayout.Label($"PCSX-Redux: {(string.IsNullOrEmpty(pcsxReduxPath) ? "Not Configured" : "Configured")} (Optional)");
            GUI.color = Color.white;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Browse for PCSX-Redux"))
            {
                string selectedPath = EditorUtility.OpenFilePanel("Select PCSX-Redux Executable", "", "");
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    pcsxReduxPath = selectedPath;
                    PSXData data = DataStorage.LoadData();
                    data.PCSXReduxPath = pcsxReduxPath;
                    DataStorage.StoreData(data);
                }
            }
            if (!string.IsNullOrEmpty(pcsxReduxPath))
            {
                if (GUILayout.Button("Clear", GUILayout.Width(60)))
                {
                    pcsxReduxPath = "";
                    PSXData data = DataStorage.LoadData();
                    data.PCSXReduxPath = pcsxReduxPath;
                    DataStorage.StoreData(data);
                }
            }
            GUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawPSXSplashColumn()
        {
            EditorGUILayout.BeginVertical("box", GUILayout.MaxWidth(position.width / 3 - 10));
            GUILayout.Label("PSXSplash", EditorStyles.boldLabel);
            GUILayout.Space(5);

            // PSXSplash status
            GUI.color = psxsplashInstalled ? Color.green : Color.red;
            GUILayout.Label($"PSXSplash: {(psxsplashInstalled ? "Installed" : "Not Installed")}");
            GUI.color = Color.white;

            if (psxsplashFetching)
            {
                GUILayout.Label("Fetching versions...");
            }
            else if (!psxsplashInstalled)
            {
                GUILayout.Space(5);
                EditorGUILayout.HelpBox("Git is required to install PSXSplash. Make sure it's installed and available in your PATH.", MessageType.Info);

                // Show version selection even before installation
                DrawVersionSelection();

                if (GUILayout.Button("Install PSXSplash") && !psxsplashInstalling)
                {
                    InstallPSXSplashAsync();
                }
            }
            else
            {
                GUILayout.Space(10);

                // Current version
                EditorGUILayout.LabelField($"Current Version: {selectedVersion}", EditorStyles.boldLabel);

                // Version selection
                DrawVersionSelection();

                GUILayout.Space(10);

                // Refresh and update buttons
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Refresh Versions"))
                {
                    FetchPSXSplashVersions();
                }

                if (GUILayout.Button("Update PSXSplash"))
                {
                    UpdatePSXSplashAsync();
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawVersionSelection()
        {
            EditorGUILayout.LabelField("Available Versions:", EditorStyles.boldLabel);

            versionScrollPosition = EditorGUILayout.BeginScrollView(versionScrollPosition, GUILayout.Height(200));

            // Branches (with latest commits)
            showBranches = EditorGUILayout.Foldout(showBranches, $"Branches ({availableBranches.Count})");
            if (showBranches && availableBranches.Any())
            {
                foreach (var branch in availableBranches)
                {
                    EditorGUILayout.BeginHorizontal();
                    bool isSelected = selectedVersion == branch.Key;
                    if (GUILayout.Toggle(isSelected, "", GUILayout.Width(20)) && !isSelected)
                    {
                        selectedVersion = branch.Key;
                        if (psxsplashInstalled)
                        {
                            CheckoutPSXSplashVersionAsync(branch.Key);
                        }
                    }
                    GUILayout.Label($"{branch.Key} (Latest: {branch.Value})", EditorStyles.label);
                    EditorGUILayout.EndHorizontal();
                }
            }
            else if (showBranches)
            {
                GUILayout.Label("No branches available");
            }

            // Releases
            showReleases = EditorGUILayout.Foldout(showReleases, $"Releases ({availableReleases.Count})");
            if (showReleases && availableReleases.Any())
            {
                foreach (var release in availableReleases)
                {
                    EditorGUILayout.BeginHorizontal();
                    bool isSelected = selectedVersion == release;
                    if (GUILayout.Toggle(isSelected, "", GUILayout.Width(20)) && !isSelected)
                    {
                        selectedVersion = release;
                        if (psxsplashInstalled)
                        {
                            CheckoutPSXSplashVersionAsync(release);
                        }
                    }
                    GUILayout.Label(release, EditorStyles.label);
                    EditorGUILayout.EndHorizontal();
                }
            }
            else if (showReleases)
            {
                GUILayout.Label("No releases available");
            }

            EditorGUILayout.EndScrollView();
        }

        private async void InstallPSXSplashAsync()
        {
            try
            {
                psxsplashInstalling = true;
                EditorUtility.DisplayProgressBar("Installing PSXSplash", "Cloning repository...", 0.3f);

                bool success = await PSXSplashInstaller.Install();

                EditorUtility.ClearProgressBar();

                if (success)
                {
                    EditorUtility.DisplayDialog("Installation Complete", "PSXSplash installed successfully.", "OK");
                    CheckPSXSplashInstallation();

                    // Checkout the selected version after installation
                    if (!string.IsNullOrEmpty(selectedVersion))
                    {
                        await CheckoutPSXSplashVersionAsync(selectedVersion);
                    }
                }
                else
                {
                    EditorUtility.DisplayDialog("Installation Failed",
                        "Failed to install PSXSplash. Make sure Git is installed and available in your PATH.", "OK");
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Installation Failed", $"Error: {ex.Message}", "OK");
            }
            finally
            {
                psxsplashInstalling = false;
            }
        }

        private async Task<bool> CheckoutPSXSplashVersionAsync(string version)
        {
            try
            {
                psxsplashInstalling = true;
                EditorUtility.DisplayProgressBar("Checking Out Version", $"Switching to {version}...", 0.3f);

                bool success = await PSXSplashInstaller.CheckoutVersionAsync(version);

                EditorUtility.ClearProgressBar();

                if (success)
                {
                    EditorUtility.DisplayDialog("Checkout Complete", $"Switched to {version} successfully.", "OK");
                    return true;
                }
                else
                {
                    EditorUtility.DisplayDialog("Checkout Failed",
                        $"Failed to switch to {version}.", "OK");
                    return false;
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Checkout Failed", $"Error: {ex.Message}", "OK");
                return false;
            }
            finally
            {
                psxsplashInstalling = false;
            }
        }

        private async void UpdatePSXSplashAsync()
        {
            try
            {
                psxsplashInstalling = true;
                EditorUtility.DisplayProgressBar("Updating PSXSplash", "Pulling latest changes...", 0.3f);

                // Pull the latest changes
                bool success = await PSXSplashInstaller.CheckoutVersionAsync(selectedVersion);

                EditorUtility.ClearProgressBar();

                if (success)
                {
                    EditorUtility.DisplayDialog("Update Complete", "PSXSplash updated successfully.", "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Update Failed",
                        "Failed to update PSXSplash.", "OK");
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Update Failed", $"Error: {ex.Message}", "OK");
            }
            finally
            {
                psxsplashInstalling = false;
            }
        }

        private async void InstallMipsToolchainAsync()
        {
            try
            {
                isInstalling = true;
                EditorUtility.DisplayProgressBar("Installing MIPS Toolchain",
                    "Please wait while the MIPS toolchain is being installed...", 0f);
                bool success = await ToolchainInstaller.InstallToolchain();
                EditorUtility.ClearProgressBar();
                if (success)
                {
                    EditorUtility.DisplayDialog("Installation Complete", "MIPS toolchain installed successfully.", "OK");
                }
                RefreshToolStatus(); // Update cached statuses after installation
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Installation Failed", $"Error: {ex.Message}", "OK");
            }
            finally
            {
                isInstalling = false;
            }
        }

        private async void InstallMakeAsync()
        {
            try
            {
                isInstalling = true;
                EditorUtility.DisplayProgressBar("Installing GNU Make",
                    "Please wait while GNU Make is being installed...", 0f);
                await ToolchainInstaller.InstallMake();
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Installation Complete", "GNU Make installed successfully.", "OK");
                RefreshToolStatus();
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Installation Failed", $"Error: {ex.Message}", "OK");
            }
            finally
            {
                isInstalling = false;
            }
        }

        private async void InstallGDBAsync()
        {
            try
            {
                isInstalling = true;
                EditorUtility.DisplayProgressBar("Installing GDB",
                    "Please wait while GDB is being installed...", 0f);
                await ToolchainInstaller.InstallGDB();
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Installation Complete", "GDB installed successfully.", "OK");
                RefreshToolStatus();
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Installation Failed", $"Error: {ex.Message}", "OK");
            }
            finally
            {
                isInstalling = false;
            }
        }
    }
}