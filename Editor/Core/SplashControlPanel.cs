using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SplashEdit.RuntimeCode;
using Debug = UnityEngine.Debug;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// SplashEdit Control Panel — the single unified window for the entire pipeline.
    /// One window. One button. Everything works.
    /// </summary>
    public class SplashControlPanel : EditorWindow
    {
        // ───── Constants ─────
        private const string WINDOW_TITLE = "SplashEdit Control Panel";
        private const string MENU_PATH = "PlayStation 1/SplashEdit Control Panel %#l";

        // ───── UI State ─────
        private Vector2 _scrollPos;
        private int _selectedTab = 0;
        private static readonly string[] _tabNames = { "Dependencies", "Scenes", "Music (CD-DA)", "Build" };
        private bool _showNativeProject = true;
        private bool _showToolchainSection = true;
        private bool _showScenesSection = true;
        private bool _showMusicSection = true;
        private bool _showVRAMSection = true;
        private bool _showBuildSection = true;

        // ───── Build State ─────
        private static bool _isBuilding;
        private static bool _isRunning;
        private static bool _luaBytecodeCompiled;
        private static Process _emulatorProcess;

        // ───── Scene List ─────
        private List<SceneEntry> _sceneList = new List<SceneEntry>();

        // ───── Music List ─────
        private List<MusicEntry> _musicList = new List<MusicEntry>();

        // ───── Memory Reports ─────
        private List<SceneMemoryReport> _memoryReports = new List<SceneMemoryReport>();
        private bool _showMemoryReport = true;

        // ───── Toolchain Cache ─────
        private bool _hasMIPS;
        private bool _hasMake;
        private bool _hasRedux;
        private bool _hasNativeProject;
        private bool _hasPsxavenc;
        private bool _hasMkpsxiso;
        private string _reduxVersion = "";

        // ───── Native project installer ─────
        private bool _isInstallingNative;
        private string _nativeInstallStatus = "";
        private string _manualNativePath = "";

        // ───── Release selector ─────
        private int _selectedReleaseIndex = 0;
        private string[] _releaseDisplayNames = new string[0];
        private bool _isFetchingReleases;
        private string _currentTag = "";
        private bool _isSwitchingRelease;

        // ───── Native status cache (avoid expensive checks every repaint) ─────
        private bool _isGitAvailable;
        private bool _isNativeRepoInstalled;
        private double _nextNativeStatusRefreshTime;
        private const double NativeStatusRefreshIntervalSec = 2.0;

        // PCdrv serial host instance (for real hardware file serving)
        private static PCdrvSerialHost _pcdrvHost;

        private struct SceneEntry
        {
            public SceneAsset asset;
            public string path;
            public string name;
        }

        private struct MusicEntry
        {
            public AudioClip asset;
            public string path;
            public string name;
        }

        // ═══════════════════════════════════════════════════════════════
        // Menu & Window Lifecycle
        // ═══════════════════════════════════════════════════════════════

        [MenuItem(MENU_PATH, false, 0)]
        public static void ShowWindow()
        {
            var window = GetWindow<SplashControlPanel>();
            window.titleContent = new GUIContent(WINDOW_TITLE, EditorGUIUtility.IconContent("d_BuildSettings.PSP2.Small").image);
            window.minSize = new Vector2(420, 600);
            window.Show();
        }

        private void OnEnable()
        {
            SplashBuildPaths.EnsureDirectories();
            RefreshToolchainStatus();
            RefreshNativeProjectStatus(force: true);
            LoadSceneList();
            LoadMusicList();
            _manualNativePath = SplashSettings.NativeProjectPath;
            FetchGitHubReleases();
            RefreshCurrentTag();
        }

        private void OnDisable()
        {
        }

        private void OnFocus()
        {
            RefreshToolchainStatus();
            RefreshNativeProjectStatus(force: true);
        }

        // ═══════════════════════════════════════════════════════════════
        // Main GUI
        // ═══════════════════════════════════════════════════════════════

        private void OnGUI()
        {
            // Run expensive shell/file checks at most every couple seconds.
            if (_selectedTab == 0 && Event.current.type == EventType.Layout && !_isInstallingNative && !_isSwitchingRelease)
            {
                RefreshNativeProjectStatus(force: false);
            }

            if (_isRunning && _pcdrvHost != null && !_pcdrvHost.IsRunning)
            {
                StopAll();
                Log("PCdrv host connection lost.", LogType.Warning);
            }

            DrawHeader();
            EditorGUILayout.Space(4);

            _selectedTab = PSXEditorStyles.DrawButtonGroup(_tabNames, _selectedTab, 28);
            EditorGUILayout.Space(4);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            switch (_selectedTab)
            {
                case 0: // Dependencies
                    DrawToolchainSection();
                    EditorGUILayout.Space(2);
                    DrawNativeProjectSection();
                    break;
                case 1: // Scenes
                    DrawScenesSection();
                    break;
                case 2: // Music
                    DrawMusicSection();
                    break;
                case 3: // Build
                    DrawBuildSection();
                    break;
            }

            EditorGUILayout.EndScrollView();
        }

        // ═══════════════════════════════════════════════════════════════
        // Header
        // ═══════════════════════════════════════════════════════════════

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(PSXEditorStyles.ToolbarStyle);

            GUILayout.Label("SplashEdit", PSXEditorStyles.WindowHeader);
            GUILayout.FlexibleSpace();

            EditorGUILayout.EndHorizontal();

            // Status bar
            {
                string statusText;
                Color statusColor;

                if (!_hasMIPS)
                {
                    statusText = "Setup required — install the MIPS toolchain to get started";
                    statusColor = PSXEditorStyles.Warning;
                }
                else if (!_hasNativeProject)
                {
                    statusText = "Native project not found — clone or set path below";
                    statusColor = PSXEditorStyles.Warning;
                }
                else if (_isBuilding)
                {
                    statusText = "Building...";
                    statusColor = PSXEditorStyles.Info;
                }
                else if (_isRunning)
                {
                    statusText = "Running on " + (SplashSettings.Target == BuildTarget.Emulator ? "emulator" : "hardware");
                    statusColor = PSXEditorStyles.AccentGreen;
                }
                else
                {
                    statusText = "Ready";
                    statusColor = PSXEditorStyles.Success;
                }

                EditorGUILayout.BeginHorizontal(PSXEditorStyles.InfoBox);
                PSXEditorStyles.DrawStatusBadge(statusColor == PSXEditorStyles.Success ? "OK" :
                    statusColor == PSXEditorStyles.Warning ? "WARN" :
                    statusColor == PSXEditorStyles.Info ? "INFO" : "RUN", statusColor, 50);
                GUILayout.Label(statusText, PSXEditorStyles.RichLabel);
                EditorGUILayout.EndHorizontal();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Native Project Section
        // ═══════════════════════════════════════════════════════════════

        private void DrawNativeProjectSection()
        {
            _showNativeProject = DrawSectionFoldout("Native Project (psxsplash)", _showNativeProject);
            if (!_showNativeProject) return;

            EditorGUILayout.BeginVertical(PSXEditorStyles.CardStyle);

            string currentPath = SplashBuildPaths.NativeSourceDir;
            bool hasProject = _hasNativeProject;

            // Status
            EditorGUILayout.BeginHorizontal();
            DrawStatusIcon(hasProject);
            if (hasProject)
            {
                GUILayout.Label("Found at:", GUILayout.Width(60));
                GUILayout.Label(TruncatePath(currentPath, 50), EditorStyles.miniLabel);
            }
            else
            {
                GUILayout.Label("Not found — download from GitHub or set path manually", EditorStyles.miniLabel);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);

            // ── Option 1: Download a release from GitHub ──
            PSXEditorStyles.DrawSeparator(4, 4);
            GUILayout.Label("Download from GitHub", PSXEditorStyles.SectionHeader);

            // Git availability check
            if (!_isGitAvailable)
            {
                EditorGUILayout.HelpBox(
                    "git is required to download the native project (submodules need recursive clone).\n" +
                    "Install git from: https://git-scm.com/downloads",
                    MessageType.Warning);
            }

            // Release selector
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Release:", GUILayout.Width(55));
            if (_isFetchingReleases)
            {
                GUILayout.Label("Fetching releases...", EditorStyles.miniLabel);
            }
            else if (_releaseDisplayNames.Length == 0)
            {
                GUILayout.Label("No releases found", EditorStyles.miniLabel);
                if (GUILayout.Button("Refresh", EditorStyles.miniButton, GUILayout.Width(60)))
                    FetchGitHubReleases();
            }
            else
            {
                _selectedReleaseIndex = EditorGUILayout.Popup(_selectedReleaseIndex, _releaseDisplayNames);
                if (GUILayout.Button("↻", EditorStyles.miniButton, GUILayout.Width(22)))
                    FetchGitHubReleases();
            }
            EditorGUILayout.EndHorizontal();

            // Current version display (when installed)
            if (_isNativeRepoInstalled && !string.IsNullOrEmpty(_currentTag))
            {
                var prevColor = GUI.contentColor;
                GUI.contentColor = PSXEditorStyles.Success;
                GUILayout.Label($"Current version: {_currentTag}", EditorStyles.miniLabel);
                GUI.contentColor = prevColor;
            }

            // Clone / Switch / Open buttons
            EditorGUILayout.BeginHorizontal();
            if (!_isNativeRepoInstalled)
            {
                // Not installed yet — show Clone button
                EditorGUI.BeginDisabledGroup(
                    _isInstallingNative || _releaseDisplayNames.Length == 0 ||
                    !_isGitAvailable);
                if (GUILayout.Button("Download Release", GUILayout.Width(130)))
                {
                    CloneNativeProject();
                }
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                // Already installed — show Switch and Open buttons
                EditorGUI.BeginDisabledGroup(
                    _isSwitchingRelease || _isInstallingNative ||
                    _releaseDisplayNames.Length == 0 || !_isGitAvailable);
                if (GUILayout.Button("Switch Release", EditorStyles.miniButton, GUILayout.Width(100)))
                {
                    SwitchNativeRelease();
                }
                EditorGUI.EndDisabledGroup();

                if (GUILayout.Button("Open Folder", EditorStyles.miniButton, GUILayout.Width(90)))
                {
                    EditorUtility.RevealInFinder(PSXSplashInstaller.FullInstallPath);
                }
            }
            EditorGUILayout.EndHorizontal();

            // Progress / status message
            if (_isInstallingNative || _isSwitchingRelease)
            {
                GUILayout.Label(_nativeInstallStatus, PSXEditorStyles.InfoBox);
            }

            // Show release notes for selected release
            if (_releaseDisplayNames.Length > 0 && _selectedReleaseIndex < PSXSplashInstaller.CachedReleases.Count)
            {
                var selected = PSXSplashInstaller.CachedReleases[_selectedReleaseIndex];
                if (!string.IsNullOrEmpty(selected.Body))
                {
                    EditorGUILayout.Space(2);
                    string trimmedNotes = selected.Body.Length > 200
                        ? selected.Body.Substring(0, 200) + "..."
                        : selected.Body;
                    GUILayout.Label(trimmedNotes, EditorStyles.wordWrappedMiniLabel);
                }
            }

            EditorGUILayout.Space(6);

            // ── Option 2: Manual path ──
            PSXEditorStyles.DrawSeparator(4, 4);
            GUILayout.Label("Or set path manually", PSXEditorStyles.SectionHeader);
            EditorGUILayout.BeginHorizontal();

            string newPath = EditorGUILayout.TextField(_manualNativePath);
            if (newPath != _manualNativePath)
            {
                _manualNativePath = newPath;
            }

            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string selected = EditorUtility.OpenFolderPanel("Select psxsplash Source Directory", _manualNativePath, "");
                if (!string.IsNullOrEmpty(selected))
                {
                    _manualNativePath = selected;
                }
            }
            EditorGUILayout.EndHorizontal();

            // Validate & apply the path
            bool manualPathValid = !string.IsNullOrEmpty(_manualNativePath) &&
                                   Directory.Exists(_manualNativePath) &&
                                   File.Exists(Path.Combine(_manualNativePath, "Makefile"));

            EditorGUILayout.BeginHorizontal();
            if (!string.IsNullOrEmpty(_manualNativePath) && !manualPathValid)
            {
                GUILayout.Label("Invalid path. The directory must contain a Makefile.", PSXEditorStyles.InfoBox);
            }
            else if (manualPathValid && _manualNativePath != SplashSettings.NativeProjectPath)
            {
                if (GUILayout.Button("Apply", GUILayout.Width(60)))
                {
                    SplashSettings.NativeProjectPath = _manualNativePath;
                    RefreshToolchainStatus();
                    Log($"Native project path set: {_manualNativePath}", LogType.Log);
                }
            }
            EditorGUILayout.EndHorizontal();

            if (manualPathValid && _manualNativePath == SplashSettings.NativeProjectPath)
            {
                var prevColor = GUI.contentColor;
                GUI.contentColor = PSXEditorStyles.Success;
                GUILayout.Label("✓ Path is set and valid", EditorStyles.miniLabel);
                GUI.contentColor = prevColor;
            }

            EditorGUILayout.EndVertical();
        }

        // ═══════════════════════════════════════════════════════════════
        // Toolchain Section
        // ═══════════════════════════════════════════════════════════════

        private void DrawToolchainSection()
        {
            _showToolchainSection = DrawSectionFoldout("Toolchain", _showToolchainSection);
            if (!_showToolchainSection) return;

            EditorGUILayout.BeginVertical(PSXEditorStyles.CardStyle);

            // MIPS Compiler
            EditorGUILayout.BeginHorizontal();
            DrawStatusIcon(_hasMIPS);
            GUILayout.Label("MIPS Cross-Compiler", GUILayout.Width(160));
            GUILayout.FlexibleSpace();
            if (!_hasMIPS)
            {
                if (GUILayout.Button("Install", PSXEditorStyles.SecondaryButton, GUILayout.Width(70)))
                    InstallMIPS();
            }
            else
            {
                PSXEditorStyles.DrawStatusBadge("Ready", PSXEditorStyles.Success);
            }
            EditorGUILayout.EndHorizontal();

            PSXEditorStyles.DrawSeparator(2, 2);

            // GNU Make
            EditorGUILayout.BeginHorizontal();
            DrawStatusIcon(_hasMake);
            GUILayout.Label("GNU Make", GUILayout.Width(160));
            GUILayout.FlexibleSpace();
            if (!_hasMake)
            {
                if (GUILayout.Button("Install", PSXEditorStyles.SecondaryButton, GUILayout.Width(70)))
                    InstallMake();
            }
            else
            {
                PSXEditorStyles.DrawStatusBadge("Ready", PSXEditorStyles.Success);
            }
            EditorGUILayout.EndHorizontal();

            PSXEditorStyles.DrawSeparator(2, 2);

            // PCSX-Redux
            EditorGUILayout.BeginHorizontal();
            DrawStatusIcon(_hasRedux);
            GUILayout.Label("PCSX-Redux", GUILayout.Width(160));
            GUILayout.FlexibleSpace();
            if (!_hasRedux)
            {
                if (GUILayout.Button("Download", PSXEditorStyles.SecondaryButton, GUILayout.Width(80)))
                    DownloadRedux();
            }
            else
            {
                PSXEditorStyles.DrawStatusBadge(_reduxVersion, PSXEditorStyles.Success);
            }
            EditorGUILayout.EndHorizontal();

            PSXEditorStyles.DrawSeparator(2, 2);

            // psxavenc (audio encoder)
            EditorGUILayout.BeginHorizontal();
            DrawStatusIcon(_hasPsxavenc);
            GUILayout.Label("psxavenc (Audio)", GUILayout.Width(160));
            GUILayout.FlexibleSpace();
            if (!_hasPsxavenc)
            {
                if (GUILayout.Button("Download", PSXEditorStyles.SecondaryButton, GUILayout.Width(80)))
                    DownloadPsxavenc();
            }
            else
            {
                PSXEditorStyles.DrawStatusBadge("Installed", PSXEditorStyles.Success);
            }
            EditorGUILayout.EndHorizontal();

            PSXEditorStyles.DrawSeparator(2, 2);

            // mkpsxiso (ISO builder)
            EditorGUILayout.BeginHorizontal();
            DrawStatusIcon(_hasMkpsxiso);
            GUILayout.Label("mkpsxiso (ISO)", GUILayout.Width(160));
            GUILayout.FlexibleSpace();
            if (!_hasMkpsxiso)
            {
                if (GUILayout.Button("Download", PSXEditorStyles.SecondaryButton, GUILayout.Width(80)))
                    DownloadMkpsxiso();
            }
            else
            {
                PSXEditorStyles.DrawStatusBadge("Installed", PSXEditorStyles.Success);
            }
            EditorGUILayout.EndHorizontal();

            // Refresh button
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", EditorStyles.miniButton, GUILayout.Width(60)))
                RefreshToolchainStatus();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        // ═══════════════════════════════════════════════════════════════
        // Scenes Section
        // ═══════════════════════════════════════════════════════════════

        private void DrawScenesSection()
        {
            _showScenesSection = DrawSectionFoldout("Scenes", _showScenesSection);
            if (!_showScenesSection) return;

            EditorGUILayout.BeginVertical(PSXEditorStyles.CardStyle);

            if (_sceneList.Count == 0)
            {
                GUILayout.Label(
                    "No scenes added yet.\n" +
                    "Each scene needs a GameObject with a PSXSceneExporter component.\n" +
                    "Drag scene assets here, or use the buttons below to add them.",
                    PSXEditorStyles.InfoBox);
            }

            // Draw scene list
            int removeIndex = -1;
            int moveUp = -1;
            int moveDown = -1;

            for (int i = 0; i < _sceneList.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();

                // Index badge
                GUILayout.Label($"[{i}]", EditorStyles.miniLabel, GUILayout.Width(24));

                // Scene asset field
                var newAsset = (SceneAsset)EditorGUILayout.ObjectField(
                    _sceneList[i].asset, typeof(SceneAsset), false);
                if (newAsset != _sceneList[i].asset)
                {
                    var entry = _sceneList[i];
                    entry.asset = newAsset;
                    if (newAsset != null)
                    {
                        entry.path = AssetDatabase.GetAssetPath(newAsset);
                        entry.name = newAsset.name;
                    }
                    _sceneList[i] = entry;
                    SaveSceneList();
                }

                // Move buttons
                EditorGUI.BeginDisabledGroup(i == 0);
                if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(22)))
                    moveUp = i;
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(i == _sceneList.Count - 1);
                if (GUILayout.Button("▼", EditorStyles.miniButtonRight, GUILayout.Width(22)))
                    moveDown = i;
                EditorGUI.EndDisabledGroup();

                // Remove
                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20)))
                    removeIndex = i;

                EditorGUILayout.EndHorizontal();
            }

            // Apply deferred operations
            if (removeIndex >= 0)
            {
                _sceneList.RemoveAt(removeIndex);
                SaveSceneList();
            }
            if (moveUp >= 1)
            {
                var temp = _sceneList[moveUp];
                _sceneList[moveUp] = _sceneList[moveUp - 1];
                _sceneList[moveUp - 1] = temp;
                SaveSceneList();
            }
            if (moveDown >= 0 && moveDown < _sceneList.Count - 1)
            {
                var temp = _sceneList[moveDown];
                _sceneList[moveDown] = _sceneList[moveDown + 1];
                _sceneList[moveDown + 1] = temp;
                SaveSceneList();
            }

            // Add scene buttons
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add Current Scene", EditorStyles.miniButton))
            {
                AddCurrentScene();
            }
            if (GUILayout.Button("+ Add Scene...", EditorStyles.miniButton))
            {
                string path = EditorUtility.OpenFilePanel("Select Scene", "Assets", "unity");
                if (!string.IsNullOrEmpty(path))
                {
                    // Convert to project-relative path
                    string projectPath = Application.dataPath;
                    if (path.StartsWith(projectPath))
                        path = "Assets" + path.Substring(projectPath.Length);
                    AddSceneByPath(path);
                }
            }
            EditorGUILayout.EndHorizontal();

            // Handle drag & drop
            HandleSceneDragDrop();

            EditorGUILayout.EndVertical();
        }

        // ═══════════════════════════════════════════════════════════════
        // Music Section
        // ═══════════════════════════════════════════════════════════════

        private void DrawMusicSection()
        {
            _showMusicSection = DrawSectionFoldout("Music", _showMusicSection);
            if (!_showMusicSection) return;

            EditorGUILayout.BeginVertical(PSXEditorStyles.CardStyle);
            GUILayout.Label( "Please note that CD-DA music works only if you perform an ISO build",
                                PSXEditorStyles.InfoBox);

            if (_musicList.Count == 0)
            {
                GUILayout.Label(
                    "No music added yet.\n" +
                    "Drag audio clips here, or use the buttons below to add them.",
                    PSXEditorStyles.InfoBox);
            }

            // Draw scene list
            int removeIndex = -1;
            int moveUp = -1;
            int moveDown = -1;

            for (int i = 0; i < _musicList.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();

                // Index badge
                GUILayout.Label($"[{i + 2}]", EditorStyles.miniLabel, GUILayout.Width(24));

                // Scene asset field
                var newAsset = (AudioClip)EditorGUILayout.ObjectField(
                    _musicList[i].asset, typeof(AudioClip), false);
                if (newAsset != _musicList[i].asset)
                {
                    var entry = _musicList[i];
                    entry.asset = newAsset;
                    if (newAsset != null)
                    {
                        entry.path = AssetDatabase.GetAssetPath(newAsset);
                        entry.name = newAsset.name;
                    }
                    _musicList[i] = entry;
                    SaveMusicList();
                }

                // Move buttons
                EditorGUI.BeginDisabledGroup(i == 0);
                if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(22)))
                    moveUp = i;
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(i == _musicList.Count - 1);
                if (GUILayout.Button("▼", EditorStyles.miniButtonRight, GUILayout.Width(22)))
                    moveDown = i;
                EditorGUI.EndDisabledGroup();

                // Remove
                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20)))
                    removeIndex = i;

                EditorGUILayout.EndHorizontal();
            }

            // Apply deferred operations
            if (removeIndex >= 0)
            {
                _musicList.RemoveAt(removeIndex);
                SaveMusicList();
            }
            if (moveUp >= 1)
            {
                var temp = _musicList[moveUp];
                _musicList[moveUp] = _musicList[moveUp - 1];
                _musicList[moveUp - 1] = temp;
                SaveMusicList();
            }
            if (moveDown >= 0 && moveDown < _musicList.Count - 1)
            {
                var temp = _musicList[moveDown];
                _musicList[moveDown] = _musicList[moveDown + 1];
                _musicList[moveDown + 1] = temp;
                SaveMusicList();
            }

            // Add scene buttons
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add Audio Clip...", EditorStyles.miniButton))
            {
                string path = EditorUtility.OpenFilePanel("Select Audio Clip", "Assets", "mp3,wav,ogg,aiff");
                if (!string.IsNullOrEmpty(path))
                {
                    // Convert to project-relative path
                    string projectPath = Application.dataPath;
                    if (path.StartsWith(projectPath))
                        path = "Assets" + path.Substring(projectPath.Length);
                    AddMusicByPath(path);
                }
            }
            EditorGUILayout.EndHorizontal();

            // Handle drag & drop
            HandleMusicDragDrop();

            EditorGUILayout.EndVertical();
        }

        // ═══════════════════════════════════════════════════════════════
        // VRAM & Textures Section
        // ═══════════════════════════════════════════════════════════════

        private void DrawVRAMSection()
        {
            _showVRAMSection = DrawSectionFoldout("VRAM & Textures", _showVRAMSection);
            if (!_showVRAMSection) return;

            EditorGUILayout.BeginVertical(PSXEditorStyles.CardStyle);

            // Framebuffer: hardcoded 320x240, vertical, dual-buffered
            GUILayout.Label("Framebuffer", PSXEditorStyles.SectionHeader);
            GUILayout.Label("Resolution: 320x240 (dual-buffered, vertical layout)", PSXEditorStyles.InfoBox);

            PSXEditorStyles.DrawSeparator(4, 4);

            GUILayout.Label("Advanced Tools", PSXEditorStyles.SectionHeader);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open VRAM Editor", PSXEditorStyles.PrimaryButton, GUILayout.Height(26)))
            {
                VRAMEditorWindow.ShowWindow();
            }
            if (GUILayout.Button("Quantized Preview", PSXEditorStyles.PrimaryButton, GUILayout.Height(26)))
            {
                QuantizedPreviewWindow.ShowWindow();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        // ═══════════════════════════════════════════════════════════════
        // Build & Run Section
        // ═══════════════════════════════════════════════════════════════

        private void DrawBuildSection()
        {
            _showBuildSection = DrawSectionFoldout("Build && Run", _showBuildSection);
            if (!_showBuildSection) return;

            EditorGUILayout.BeginVertical(PSXEditorStyles.CardStyle);

            // Target & Mode
            GUILayout.Label("Configuration", PSXEditorStyles.SectionHeader);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Target:", GUILayout.Width(50));
            SplashSettings.Target = (BuildTarget)EditorGUILayout.EnumPopup(SplashSettings.Target);
            GUILayout.Label("Mode:", GUILayout.Width(40));
            SplashSettings.Mode = (BuildMode)EditorGUILayout.EnumPopup(SplashSettings.Mode);
            EditorGUILayout.EndHorizontal();

            // Clean Build toggle
            SplashSettings.CleanBuild = EditorGUILayout.Toggle("Clean Build", SplashSettings.CleanBuild);

            // Memory Overlay toggle
            SplashSettings.MemoryOverlay = EditorGUILayout.Toggle(
                new GUIContent("Memory Overlay", "Show heap/RAM usage bar at top-right during gameplay"),
                SplashSettings.MemoryOverlay);

            SplashSettings.FpsOverlay = EditorGUILayout.Toggle(
                new GUIContent("FPS Overlay", "Show an FPS counter at top-left during gameplay"),
                SplashSettings.FpsOverlay);

            SplashSettings.RoomDebugOverlay = EditorGUILayout.Toggle(
                new GUIContent("Room Debug Overlay", "Render ALL room triangles in per-room colors on top of the scene for culling diagnosis"),
                SplashSettings.RoomDebugOverlay);

            SplashSettings.ProfilerOverlay = EditorGUILayout.Toggle(
                new GUIContent("Profiler Overlay", "Show a per-frame pie chart with timing breakdown for major runtime systems"),
                SplashSettings.ProfilerOverlay);

            SplashSettings.OtSize = EditorGUILayout.IntField(
                new GUIContent("OT Size", "Ordering table entries. Lower = less RAM, shallower Z-sorting."),
                SplashSettings.OtSize);
            SplashSettings.BumpSize = EditorGUILayout.IntField(
                new GUIContent("Bump Alloc Size", "Per-frame primitive buffer (bytes). Lower = less RAM, fewer triangles per frame."),
                SplashSettings.BumpSize);

            // Serial port (only for Real Hardware)
            if (SplashSettings.Target == BuildTarget.RealHardware)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Serial Port:", GUILayout.Width(80));
                SplashSettings.SerialPort = EditorGUILayout.TextField(SplashSettings.SerialPort);
                if (GUILayout.Button("Scan", EditorStyles.miniButton, GUILayout.Width(40)))
                    ScanSerialPorts();
                EditorGUILayout.EndHorizontal();
            }

            // ISO settings (only for ISO build target)
            if (SplashSettings.Target == BuildTarget.ISO)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Volume Label:", GUILayout.Width(80));
                SplashSettings.ISOVolumeLabel = EditorGUILayout.TextField(SplashSettings.ISOVolumeLabel);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("License File:", GUILayout.Width(80));
                string licensePath = SplashSettings.LicenseFilePath;
                string displayPath = string.IsNullOrEmpty(licensePath) ? "(none — homebrew)" : Path.GetFileName(licensePath);
                GUILayout.Label(displayPath, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Browse", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    string path = EditorUtility.OpenFilePanel(
                        "Select Sony License File", "", "dat");
                    if (!string.IsNullOrEmpty(path))
                        SplashSettings.LicenseFilePath = path;
                }
                if (!string.IsNullOrEmpty(licensePath) &&
                    GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    SplashSettings.LicenseFilePath = "";
                }
                EditorGUILayout.EndHorizontal();
            }

            PSXEditorStyles.DrawSeparator(6, 6);

            // Big Build & Run button
            EditorGUI.BeginDisabledGroup(_isBuilding);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            var largeBuildButton = new GUIStyle(PSXEditorStyles.SuccessButton)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(20, 20, 10, 10)
            };

            string buttonLabel = _isBuilding ? "Building..." :
                (SplashSettings.Target == BuildTarget.ISO ? "BUILD" : "BUILD & RUN");
            if (GUILayout.Button(buttonLabel, largeBuildButton, GUILayout.Width(200), GUILayout.Height(38)))
            {
                BuildAndRun();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();

            // Stop button (if running - emulator or hardware PCdrv host)
            if (_isRunning)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                string stopLabel = _emulatorProcess != null ? "STOP EMULATOR" : "STOP PCdrv HOST";
                if (GUILayout.Button(stopLabel, PSXEditorStyles.DangerButton, GUILayout.Width(200), GUILayout.Height(26)))
                {
                    StopAll();
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            // Export-only / Compile-only
            PSXEditorStyles.DrawSeparator(4, 4);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Export Only", PSXEditorStyles.SecondaryButton, GUILayout.Width(100)))
            {
                ExportAllScenes();
            }
            if (GUILayout.Button("Compile Only", PSXEditorStyles.SecondaryButton, GUILayout.Width(100)))
            {
                CompileOnly();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            // Memory report (shown after export)
            if (_memoryReports.Count > 0)
            {
                EditorGUILayout.Space(8);
                DrawMemoryReports();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Memory Reports
        // ═══════════════════════════════════════════════════════════════

        private void DrawMemoryReports()
        {
            _showMemoryReport = DrawSectionFoldout("Memory Report", _showMemoryReport);
            if (!_showMemoryReport) return;

            foreach (var report in _memoryReports)
            {
                EditorGUILayout.BeginVertical(PSXEditorStyles.CardStyle);

                GUILayout.Label($"Scene: {report.sceneName}", PSXEditorStyles.SectionHeader);
                EditorGUILayout.Space(4);

                // Main RAM bar — segmented: OT | Bump | Scene | Heap
                DrawSegmentedMemoryBar("Main RAM",
                    SceneMemoryReport.USABLE_RAM,
                    report.RamPercent,
                    new (string label, long size, Color color)[] {
                        ("OT",    SceneMemoryReport.OT_TOTAL,          new Color(0.9f, 0.4f, 0.2f)),
                        ("Bump",  SceneMemoryReport.BUMP_ALLOC_TOTAL,  new Color(0.8f, 0.6f, 0.1f)),
                        ("Scene", report.SceneRamUsage,                new Color(0.3f, 0.6f, 1.0f)),
                        ("Other", SceneMemoryReport.VIS_REFS + SceneMemoryReport.STACK_ESTIMATE + SceneMemoryReport.LUA_OVERHEAD,
                                                                       new Color(0.5f, 0.5f, 0.5f)),
                    },
                    report.EstimatedHeapFree);

                EditorGUILayout.Space(4);

                // VRAM bar
                DrawMemoryBar("VRAM",
                    report.TotalVramUsed, SceneMemoryReport.TOTAL_VRAM,
                    report.VramPercent,
                    new Color(0.9f, 0.5f, 0.2f),
                    $"FB: {FormatBytes(report.framebufferSize)}  |  " +
                    $"Tex: {FormatBytes(report.textureAtlasSize)}  |  " +
                    $"CLUT: {FormatBytes(report.clutSize)}  |  " +
                    $"Free: {FormatBytes(report.VramFree)}");

                EditorGUILayout.Space(4);

                // SPU RAM bar
                DrawMemoryBar("SPU RAM",
                    report.TotalSpuUsed, SceneMemoryReport.USABLE_SPU,
                    report.SpuPercent,
                    new Color(0.6f, 0.3f, 0.9f),
                    report.audioClipCount > 0
                        ? $"{report.audioClipCount} clips  |  {FormatBytes(report.audioDataSize)}  |  Free: {FormatBytes(report.SpuFree)}"
                        : "No audio clips");

                EditorGUILayout.Space(4);

                // CD Storage (no bar, just info)
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("CD Storage:", EditorStyles.miniLabel, GUILayout.Width(70));
                GUILayout.Label(
                    $"Scene: {FormatBytes(report.splashpackFileSize)}" +
                    (report.loaderPackSize > 0 ? $"  |  Loader: {FormatBytes(report.loaderPackSize)}" : "") +
                    $"  |  Total: {FormatBytes(report.TotalDiscSize)}",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();

                // Summary stats
                PSXEditorStyles.DrawSeparator(4, 4);
                EditorGUILayout.LabelField(
                    $"<b>{report.gameObjectCount}</b> objects  |  " +
                    $"<b>{report.triangleCount}</b> tris  |  " +
                    $"<b>{report.atlasCount}</b> atlases  |  " +
                    $"<b>{report.clutCount}</b> CLUTs",
                    PSXEditorStyles.RichLabel);

                if (report.IsHeapCritical)
                    EditorGUILayout.HelpBox(
                        $"Estimated heap: {report.EstimatedHeapFree / 1024}KB free. Lua scripts may OOM!",
                        MessageType.Error);
                else if (report.IsHeapWarning)
                    EditorGUILayout.HelpBox(
                        $"Estimated heap: {report.EstimatedHeapFree / 1024}KB free. Consider reducing OT/Bump sizes.",
                        MessageType.Warning);

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }
        }

        private void DrawMemoryBar(string label, long used, long total, float percent, Color barColor, string details)
        {
            // Label row
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, EditorStyles.boldLabel, GUILayout.Width(70));
            GUILayout.Label($"{FormatBytes(used)} / {FormatBytes(total)}  ({percent:F1}%)", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // Progress bar
            Rect barRect = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));

            // Background
            EditorGUI.DrawRect(barRect, new Color(0.15f, 0.15f, 0.17f));

            // Fill
            float fillFraction = Mathf.Clamp01((float)used / total);
            Rect fillRect = new Rect(barRect.x, barRect.y, barRect.width * fillFraction, barRect.height);

            // Color shifts toward red when over 80%
            Color fillColor = barColor;
            if (percent > 90f)
                fillColor = Color.Lerp(PSXEditorStyles.Warning, PSXEditorStyles.Error, (percent - 90f) / 10f);
            else if (percent > 80f)
                fillColor = Color.Lerp(barColor, PSXEditorStyles.Warning, (percent - 80f) / 10f);
            EditorGUI.DrawRect(fillRect, fillColor);

            // Border
            DrawRectOutline(barRect, new Color(0.3f, 0.3f, 0.35f));

            // Percent text overlay
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            GUI.Label(barRect, $"{percent:F1}%", style);

            // Details row
            GUILayout.Label(details, EditorStyles.miniLabel);
        }

        private void DrawSegmentedMemoryBar(
            string label, long total, float percent,
            (string label, long size, Color color)[] segments,
            long heapFree)
        {
            long used = 0;
            foreach (var seg in segments) used += seg.size;

            // Label row
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, EditorStyles.boldLabel, GUILayout.Width(70));
            GUILayout.Label($"{FormatBytes(used)} / {FormatBytes(total)}  ({percent:F1}%)  |  Heap free: {FormatBytes(heapFree)}",
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // Bar
            Rect barRect = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(barRect, new Color(0.15f, 0.15f, 0.17f));

            // Draw each segment
            float x = barRect.x;
            foreach (var seg in segments)
            {
                float w = barRect.width * Mathf.Clamp01((float)seg.size / total);
                if (w > 0)
                    EditorGUI.DrawRect(new Rect(x, barRect.y, w, barRect.height), seg.color);
                x += w;
            }

            // Border
            DrawRectOutline(barRect, new Color(0.3f, 0.3f, 0.35f));

            // Percent overlay
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            GUI.Label(barRect, $"{percent:F1}%", style);

            // Legend row
            EditorGUILayout.BeginHorizontal();
            foreach (var seg in segments)
            {
                // Color swatch + label
                Rect swatchRect = GUILayoutUtility.GetRect(10, 10, GUILayout.Width(10));
                swatchRect.y += 2;
                EditorGUI.DrawRect(swatchRect, seg.color);
                GUILayout.Label($"{seg.label}: {FormatBytes(seg.size)}", EditorStyles.miniLabel);
                GUILayout.Space(6);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawRectOutline(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1, rect.y, 1, rect.height), color);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "N/A";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
            return $"{bytes / (1024f * 1024f):F2} MB";
        }

        // ═══════════════════════════════════════════════════════════════
        // Pipeline Actions
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// The main pipeline: Validate → Export all scenes → Compile → Launch.
        /// </summary>
        public async void BuildAndRun()
        {
            if (_isBuilding) return;

            if (UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().isDirty)
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Unsaved Changes",
                    "The current scene has unsaved changes. Save before building?",
                    "Save and Build",    // 0
                    "Cancel",            // 1
                    "Build Without Saving" // 2
                );
                if (choice == 1) return; // Cancel
                if (choice == 0) EditorSceneManager.SaveOpenScenes();
            }

            _isBuilding = true;
            _luaBytecodeCompiled = false;

            var console = EditorWindow.GetWindow<PSXConsoleWindow>();
            console.titleContent = new GUIContent("PSX Console", EditorGUIUtility.IconContent("d_UnityEditor.ConsoleWindow").image);
            console.minSize = new Vector2(400, 200);
            console.Show();

            try
            {
                Log("Validating toolchain...", LogType.Log);
                if (!ValidateToolchain())
                {
                    Log("Toolchain validation failed. Fix issues above.", LogType.Error);
                    return;
                }
                Log("Toolchain OK.", LogType.Log);

                // Collect Lua files from all scenes and compile to bytecode
                Log("Collecting Lua scripts...", LogType.Log);
                EditorUtility.DisplayProgressBar("SplashEdit", "Collecting Lua scripts...", 0.2f);
                var luaFiles = CollectLuaSources();
                if (luaFiles != null && luaFiles.Count > 0)
                {
                    Log($"Found {luaFiles.Count} Lua script(s). Compiling...", LogType.Log);
                    EditorUtility.DisplayProgressBar("SplashEdit", "Compiling Lua scripts...", 0.3f);
                    var bytecodeMap = await CompileLuaAsync(luaFiles);
                    if (bytecodeMap == null)
                    {
                        Log("Lua compilation failed.", LogType.Error);
                        return;
                    }
                    PSXSceneWriter.CompiledLuaBytecode = bytecodeMap;
                    _luaBytecodeCompiled = true;
                    Log($"Compiled {bytecodeMap.Count} Lua script(s) to bytecode.", LogType.Log);
                }
                else
                {
                    Log("No Lua scripts found.", LogType.Log);
                    PSXSceneWriter.CompiledLuaBytecode = null;
                }

                Log("Exporting scenes...", LogType.Log);
                EditorUtility.DisplayProgressBar("SplashEdit", "Exporting scenes...", 0.4f);
                if (!ExportAllScenes())
                {
                    Log("Export failed.", LogType.Error);
                    return;
                }
                Log($"Exported {_sceneList.Count} scene(s).", LogType.Log);

                // Clear bytecode cache after export
                PSXSceneWriter.CompiledLuaBytecode = null;

                Log("Compiling native code...", LogType.Log);
                EditorUtility.DisplayProgressBar("SplashEdit", "Compiling native code...", 0.6f);
                if (!await CompileNativeAsync())
                {
                    Log("Compilation failed. Check build log.", LogType.Error);
                    return;
                }
                Log("Compile succeeded.", LogType.Log);

                Log("Launching...", LogType.Log);
                Launch();
            }
            catch (Exception ex)
            {
                Log($"Pipeline error: {ex.Message}", LogType.Error);
                Debug.LogException(ex);
            }
            finally
            {
                _isBuilding = false;
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        // ───── Step 1: Validate ─────

        private bool ValidateToolchain()
        {
            RefreshToolchainStatus();

            if (!_hasMIPS)
            {
                Log("MIPS cross-compiler not found. Click Install in the Toolchain section.", LogType.Error);
                return false;
            }
            if (!_hasMake)
            {
                Log("GNU Make not found. Click Install in the Toolchain section.", LogType.Error);
                return false;
            }
            if (SplashSettings.Target == BuildTarget.Emulator && !_hasRedux)
            {
                Log("PCSX-Redux not found. Click Download in the Toolchain section.", LogType.Error);
                return false;
            }
            if (SplashSettings.Target == BuildTarget.ISO && !_hasMkpsxiso)
            {
                Log("mkpsxiso not found. Click Download in the Toolchain section.", LogType.Error);
                return false;
            }

            string nativeDir = SplashBuildPaths.NativeSourceDir;
            if (string.IsNullOrEmpty(nativeDir) || !Directory.Exists(nativeDir))
            {
                Log("Native project directory not found. Set it in the Toolchain section.", LogType.Error);
                return false;
            }

            if (_sceneList.Count == 0)
            {
                Log("No scenes in the scene list. Add at least one scene.", LogType.Error);
                return false;
            }

            return true;
        }

        // ───── Step 2: Export ─────

        /// <summary>
        /// Exports all scenes in the scene list to splashpack files in PSXBuild/.
        /// </summary>
        public bool ExportAllScenes()
        {
            SplashBuildPaths.EnsureDirectories();
            _loaderPackCache = new Dictionary<string, string>();
            _memoryReports.Clear();

            // Save current scene
            string currentScenePath = SceneManager.GetActiveScene().path;

            bool success = true;
            for (int i = 0; i < _sceneList.Count; i++)
            {
                var scene = _sceneList[i];
                if (scene.asset == null)
                {
                    Log($"Scene [{i}] is null, skipping.", LogType.Warning);
                    continue;
                }

                EditorUtility.DisplayProgressBar("SplashEdit Export",
                    $"Exporting scene {i + 1}/{_sceneList.Count}: {scene.name}",
                    (float)i / _sceneList.Count);

                try
                {
                    // Open the scene
                    EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single);

                    // Find the exporter
                    var exporter = UnityEngine.Object.FindFirstObjectByType<PSXSceneExporter>();
                    if (exporter == null)
                    {
                        Log($"Scene '{scene.name}' has no PSXSceneExporter. Skipping.", LogType.Warning);
                        continue;
                    }

                    // Export to the build directory
                    string outputPath = SplashBuildPaths.GetSceneSplashpackPath(i, scene.name);
                    string loaderPath = null;
                    exporter.ExportToPath(outputPath);
                    Log($"Exported '{scene.name}' → {Path.GetFileName(outputPath)}", LogType.Log);

                    // Export loading screen if assigned
                    if (exporter.LoadingScreenPrefab != null)
                    {
                        loaderPath = SplashBuildPaths.GetSceneLoaderPackPath(i, scene.name);
                        ExportLoaderPack(exporter.LoadingScreenPrefab, loaderPath, i, scene.name);
                    }

                    // Generate memory report for this scene
                    try
                    {
                        var report = SceneMemoryAnalyzer.Analyze(
                            scene.name,
                            outputPath,
                            loaderPath,
                            exporter.LastExportAtlases,
                            exporter.LastExportAudioSizes,
                            exporter.LastExportFonts,
                            exporter.LastExportTriangleCount);
                        _memoryReports.Add(report);
                    }
                    catch (Exception reportEx)
                    {
                        Log($"Memory report for '{scene.name}' failed: {reportEx.Message}", LogType.Warning);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error exporting '{scene.name}': {ex}", LogType.Error);
                    success = false;
                }
            }

            // Write manifest (simple binary: scene count + list of filenames)
            WriteManifest();

            EditorUtility.ClearProgressBar();

            // Reopen orignal scene
            if (!string.IsNullOrEmpty(currentScenePath))
            {
                EditorSceneManager.OpenScene(currentScenePath, OpenSceneMode.Single);
            }

            return success;
        }

        /// <summary>
        /// Cache of already-exported loader packs for deduplication.
        /// Key = prefab asset GUID, Value = path of the written file.
        /// If two scenes reference the same loading screen prefab, we copy the file
        /// instead of regenerating it.
        /// </summary>
        private Dictionary<string, string> _loaderPackCache = new Dictionary<string, string>();

        private void ExportLoaderPack(GameObject prefab, string outputPath, int sceneIndex, string sceneName)
        {
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            string guid = AssetDatabase.AssetPathToGUID(prefabPath);

            // Dedup: if we already exported this exact prefab, just copy the file
            if (!string.IsNullOrEmpty(guid) && _loaderPackCache.TryGetValue(guid, out string cachedPath))
            {
                if (File.Exists(cachedPath))
                {
                    File.Copy(cachedPath, outputPath, true);
                    Log($"Loading screen for '{sceneName}' → {Path.GetFileName(outputPath)} (deduped from {Path.GetFileName(cachedPath)})", LogType.Log);
                    return;
                }
            }

            // Need the PSXData resolution to pass to the writer
            Vector2 resolution;
            bool db, vl;
            List<ProhibitedArea> pa;
            DataStorage.LoadData(out resolution, out db, out vl, out pa);

            // Instantiate the prefab temporarily so the components are live
            // (GetComponentsInChildren needs active hierarchy)
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                // Pack UI image textures into VRAM (same flow as PSXSceneExporter)
                TextureAtlas[] atlases = null;
                PSXUIImage[] uiImages = instance.GetComponentsInChildren<PSXUIImage>(true);
                if (uiImages != null && uiImages.Length > 0)
                {
                    List<PSXTexture2D> uiTextures = new List<PSXTexture2D>();
                    foreach (PSXUIImage img in uiImages)
                    {
                        if (img.SourceTexture != null)
                        {
                            Utils.SetTextureImporterFormat(img.SourceTexture, true);
                            PSXTexture2D tex = PSXTexture2D.CreateFromTexture2D(img.SourceTexture, img.BitDepth);
                            tex.OriginalTexture = img.SourceTexture;
                            img.PackedTexture = tex;
                            uiTextures.Add(tex);
                        }
                    }

                    if (uiTextures.Count > 0)
                    {
                        (Rect buffer1, Rect buffer2) = Utils.BufferForResolution(resolution, vl);
                        List<Rect> framebuffers = new List<Rect> { buffer1 };
                        if (db) framebuffers.Add(buffer2);

                        VRAMPacker packer = new VRAMPacker(framebuffers, pa);
                        var packed = packer.PackTexturesIntoVRAM(new PSXObjectExporter[0], uiTextures);
                        atlases = packed.atlases;
                    }
                }

                // CollectCanvasFromPrefab reads PackedTexture VRAM coords (set by packer above)
                bool ok = PSXLoaderPackWriter.Write(outputPath, instance, resolution, atlases,
                    (msg, type) => Log(msg, type));
                if (ok)
                {
                    Log($"Loading screen for '{sceneName}' → {Path.GetFileName(outputPath)}", LogType.Log);
                    if (!string.IsNullOrEmpty(guid))
                        _loaderPackCache[guid] = outputPath;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private void WriteManifest()
        {
            string manifestPath = SplashBuildPaths.ManifestPath;
            using (var writer = new BinaryWriter(File.Open(manifestPath, FileMode.Create)))
            {
                // Magic "SM" for Scene Manifest
                writer.Write((byte)'S');
                writer.Write((byte)'M');
                // Version
                writer.Write((ushort)1);
                // Scene count
                writer.Write((uint)_sceneList.Count);

                for (int i = 0; i < _sceneList.Count; i++)
                {
                    string filename = Path.GetFileName(
                        SplashBuildPaths.GetSceneSplashpackPath(i, _sceneList[i].name));
                    byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(filename);
                    // Length-prefixed string
                    writer.Write((byte)nameBytes.Length);
                    writer.Write(nameBytes);
                }
            }
            Log("Wrote scene manifest.", LogType.Log);
        }

        // ───── Lua bytecode compilation ─────

        /// <summary>
        /// Scan all scenes in the scene list and collect unique Lua source files.
        /// Writes each source to PSXBuild/lua_src/ for compilation.
        /// </summary>
        private Dictionary<LuaFile, string> CollectLuaSources()
        {
            SplashBuildPaths.EnsureDirectories();
            string luaSrcDir = SplashBuildPaths.LuaSrcDir;
            if (Directory.Exists(luaSrcDir))
                Directory.Delete(luaSrcDir, true);
            Directory.CreateDirectory(luaSrcDir);

            string currentScenePath = SceneManager.GetActiveScene().path;
            var luaFileMap = new Dictionary<LuaFile, string>(); // LuaFile -> filename (no extension)

            for (int i = 0; i < _sceneList.Count; i++)
            {
                var scene = _sceneList[i];
                if (scene.asset == null) continue;

                try
                {
                    EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single);
                    // Collect from object exporters
                    var objExporters = UnityEngine.Object.FindObjectsByType<PSXObjectExporter>(FindObjectsSortMode.None);
                    foreach (var objExporter in objExporters)
                    {
                        if (objExporter.LuaFile != null && !luaFileMap.ContainsKey(objExporter.LuaFile))
                        {
                            string name = $"script_{luaFileMap.Count}";
                            luaFileMap[objExporter.LuaFile] = name;
                        }
                    }

                    // Collect scene-level Lua file
                    var sceneExporter = UnityEngine.Object.FindFirstObjectByType<PSXSceneExporter>();
                    if (sceneExporter != null && sceneExporter.SceneLuaFile != null &&
                        !luaFileMap.ContainsKey(sceneExporter.SceneLuaFile))
                    {
                        string name = $"script_{luaFileMap.Count}";
                        luaFileMap[sceneExporter.SceneLuaFile] = name;
                    }

                    // Collect from trigger boxes
                    var triggerBoxes = UnityEngine.Object.FindObjectsByType<PSXTriggerBox>(FindObjectsSortMode.None);
                    foreach (var tb in triggerBoxes)
                    {
                        if (tb.LuaFile != null && !luaFileMap.ContainsKey(tb.LuaFile))
                        {
                            string name = $"script_{luaFileMap.Count}";
                            luaFileMap[tb.LuaFile] = name;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error scanning '{scene.name}' for Lua files: {ex.Message}", LogType.Warning);
                }
            }

            // Write source files to disk
            foreach (var kvp in luaFileMap)
            {
                string srcPath = Path.Combine(luaSrcDir, kvp.Value + ".lua");
                File.WriteAllText(srcPath, kvp.Key.LuaScript, new System.Text.UTF8Encoding(false));
            }

            // Restore original scene
            if (!string.IsNullOrEmpty(currentScenePath))
                EditorSceneManager.OpenScene(currentScenePath, OpenSceneMode.Single);

            return luaFileMap;
        }

        /// <summary>
        /// Compile Lua source files to PS1 bytecode using luac_psx inside PCSX-Redux.
        /// Returns a dictionary mapping LuaFile assets to their compiled bytecode,
        /// or null on failure.
        /// </summary>
        private async Task<Dictionary<string, byte[]>> CompileLuaAsync(Dictionary<LuaFile, string> luaFileMap)
        {
            string luaSrcDir = SplashBuildPaths.LuaSrcDir;
            string nativeDir = SplashBuildPaths.NativeSourceDir;

            // Build luac_psx if needed
            string luacDir = SplashBuildPaths.LuacPsxDir;
            string luacExe = SplashBuildPaths.LuacPsxExePath;

            if (!File.Exists(luacExe))
            {
                Log("Building Lua compiler (luac_psx)...", LogType.Log);
                if (!await BuildLuacPsxAsync(luacDir))
                {
                    Log("Failed to build luac_psx.", LogType.Error);
                    return null;
                }
            }

            // Generate manifest.txt
            string manifestPath = SplashBuildPaths.LuaManifestPath;
            using (var sw = new StreamWriter(manifestPath, false, new System.Text.UTF8Encoding(false)))
            {
                foreach (var kvp in luaFileMap)
                {
                    sw.WriteLine(kvp.Value + ".lua");
                    sw.WriteLine(kvp.Value + ".luac");
                }
            }

            // Clean up previous sentinel
            string sentinelPath = SplashBuildPaths.LuaDoneSentinel;
            if (File.Exists(sentinelPath))
                File.Delete(sentinelPath);

            // Launch PCSX-Redux headless with luac_psx
            string reduxBinary = SplashBuildPaths.PCSXReduxBinary;
            if (!File.Exists(reduxBinary))
            {
                Log("PCSX-Redux not found. Install it via the Toolchain section.", LogType.Error);
                return null;
            }

            string args = $"--no-ui --run --fastboot --pcdrv --stdout --pcdrvbase \"{luaSrcDir}\" --loadexe \"{luacExe}\"";
            Log($"luac_psx: {luacExe}", LogType.Log);
            Log($"pcdrvbase: {luaSrcDir}", LogType.Log);
            Log($"manifest: {File.ReadAllText(manifestPath).Trim()}", LogType.Log);

            var psi = new ProcessStartInfo
            {
                FileName = reduxBinary,
                Arguments = args,
                WorkingDirectory = luaSrcDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process process = null;
            try
            {
                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var stdoutBuf = new System.Text.StringBuilder();

                var stderrBuf = new System.Text.StringBuilder();
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        stdoutBuf.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        stderrBuf.AppendLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Poll for sentinel file (100ms interval, 30s timeout)
                bool done = false;
                int elapsed = 0;
                const int timeoutMs = 30000;
                const int pollMs = 100;

                while (elapsed < timeoutMs)
                {
                    await Task.Delay(pollMs);
                    elapsed += pollMs;

                    if (File.Exists(sentinelPath))
                    {
                        done = true;
                        break;
                    }

                    // Check if process died unexpectedly
                    if (process.HasExited)
                    {
                        Log("PCSX-Redux exited unexpectedly during Lua compilation.", LogType.Error);
                        break;
                    }
                }

                // Kill emulator
                if (!process.HasExited)
                {
                    try { process.Kill(); } catch { }
                }

                if (!done)
                {
                    Log("Lua compilation timed out (30s).", LogType.Error);
                    return null;
                }

                // Check sentinel content
                // Give a tiny delay for file to flush on Windows
                await Task.Delay(200);
                string sentinelContent = File.ReadAllText(sentinelPath).Trim();
                if (string.IsNullOrEmpty(sentinelContent))
                    sentinelContent = "(empty)";
                if (sentinelContent != "OK")
                {
                    // Sentinel contains error details from luac_psx
                    foreach (string errLine in sentinelContent.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(errLine))
                            Log(errLine.TrimEnd(), LogType.Error);
                    }

                    // Dump full stdout and stderr for diagnosis
                    string fullStdout = stdoutBuf.ToString().Trim();
                    string fullStderr = stderrBuf.ToString().Trim();

                    if (!string.IsNullOrEmpty(fullStdout))
                    {
                        Log("--- PCSX-Redux stdout ---", LogType.Log);
                        foreach (string line in fullStdout.Split('\n'))
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                                LogToPanel(line.TrimEnd(), LogType.Log);
                        }
                    }
                    else
                    {
                        Log("No stdout captured from PCSX-Redux.", LogType.Warning);
                    }

                    if (!string.IsNullOrEmpty(fullStderr))
                    {
                        Log("--- PCSX-Redux stderr ---", LogType.Log);
                        foreach (string line in fullStderr.Split('\n'))
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                                LogToPanel(line.TrimEnd(), LogType.Log);
                        }
                    }
                    return null;
                }

                // Read compiled bytecode files, keyed by source text
                var result = new Dictionary<string, byte[]>();
                foreach (var kvp in luaFileMap)
                {
                    string luacPath = Path.Combine(luaSrcDir, kvp.Value + ".luac");
                    if (!File.Exists(luacPath))
                    {
                        Log($"Missing compiled bytecode: {kvp.Value}.luac", LogType.Error);
                        return null;
                    }
                    result[kvp.Key.LuaScript] = File.ReadAllBytes(luacPath);
                }

                return result;
            }
            catch (Exception ex)
            {
                Log($"Lua compilation error: {ex.Message}", LogType.Error);
                return null;
            }
            finally
            {
                if (process != null)
                {
                    if (!process.HasExited)
                        try { process.Kill(); } catch { }
                    process.Dispose();
                }
            }
        }

        /// <summary>
        /// Build the luac_psx PS1 compiler executable.
        /// </summary>
        private async Task<bool> BuildLuacPsxAsync(string luacDir)
        {
            int jobCount = Math.Max(1, SystemInfo.processorCount - 1);
            string makeCmd = $"make -j{jobCount}";

            var psi = new ProcessStartInfo
            {
                FileName = Application.platform == RuntimePlatform.WindowsEditor ? "cmd.exe" : "/bin/bash",
                Arguments = WrapCommandForMacOS(luacDir, makeCmd),
                WorkingDirectory = luacDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var stderrBuf = new System.Text.StringBuilder();
                process.OutputDataReceived += (s, e) => { };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) stderrBuf.AppendLine(e.Data);
                };

                var tcs = new TaskCompletionSource<int>();
                process.Exited += (s, e) => tcs.TrySetResult(process.ExitCode);

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                int exitCode = await tcs.Task;
                process.Dispose();

                if (exitCode != 0)
                {
                    foreach (string line in stderrBuf.ToString().Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            LogToPanel(line.Trim(), LogType.Error);
                    }
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to build luac_psx: {ex.Message}", LogType.Error);
                return false;
            }
        }

        // ───── Step 3: Compile ─────

        private async void CompileOnly()
        {
            if (_isBuilding) return;
            _isBuilding = true;
            Repaint();
            try
            {
                Log("Compiling native code...", LogType.Log);
                EditorUtility.DisplayProgressBar("SplashEdit", "Compiling native code...", 0.5f);
                if (await CompileNativeAsync())
                    Log("Compile succeeded.", LogType.Log);
                else
                    Log("Compilation failed. Check build log.", LogType.Error);
            }
            finally
            {
                _isBuilding = false;
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        private async Task<bool> CompileNativeAsync()
        {
            string nativeDir = SplashBuildPaths.NativeSourceDir;
            if (string.IsNullOrEmpty(nativeDir))
            {
                Log("Native project directory not set.", LogType.Error);
                return false;
            }

            string buildArg = SplashSettings.Mode == BuildMode.Debug ? "BUILD=Debug" : "";

            if (SplashSettings.Target == BuildTarget.ISO)
                buildArg += " LOADER=cdrom";

            if (SplashSettings.MemoryOverlay)
                buildArg += " MEMOVERLAY=1";

            if (SplashSettings.FpsOverlay)
                buildArg += " FPSOVERLAY=1";

            if (SplashSettings.RoomDebugOverlay)
                buildArg += " ROOMDEBUG=1";

            if (SplashSettings.ProfilerOverlay)
                buildArg += " PROFILER=1";

            buildArg += $" OT_SIZE={SplashSettings.OtSize} BUMP_SIZE={SplashSettings.BumpSize}";

            // Use noparser Lua library when bytecode was pre-compiled
            string noparserPrefix = "";
            if (_luaBytecodeCompiled)
            {
                buildArg += " NOPARSER=1";
                // Build liblua-noparser.a first
                noparserPrefix = "make -C third_party/nugget/third_party/psxlua psx-noparser && ";
            }

            int jobCount = Math.Max(1, SystemInfo.processorCount - 1);
            string cleanPrefix = SplashSettings.CleanBuild ? "make clean && " : "";
            string makeCmd = $"{cleanPrefix}{noparserPrefix}make all -j{jobCount} {buildArg}".Trim();
            Log($"Running: {makeCmd}", LogType.Log);

            var psi = new ProcessStartInfo
            {
                FileName = Application.platform == RuntimePlatform.WindowsEditor ? "cmd.exe" : "/bin/bash",
                Arguments = WrapCommandForMacOS(nativeDir, makeCmd),
                WorkingDirectory = nativeDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var stdoutBuf = new System.Text.StringBuilder();
                var stderrBuf = new System.Text.StringBuilder();

                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) stdoutBuf.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) stderrBuf.AppendLine(e.Data);
                };

                var tcs = new TaskCompletionSource<int>();
                process.Exited += (s, e) => tcs.TrySetResult(process.ExitCode);

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                int exitCode = await tcs.Task;
                process.Dispose();

                string stdout = stdoutBuf.ToString();
                string stderr = stderrBuf.ToString();

                foreach (string line in stdout.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        LogToPanel(line.Trim(), LogType.Log);
                }

                if (exitCode != 0)
                {
                    foreach (string line in stderr.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            LogToPanel(line.Trim(), LogType.Error);
                    }
                    Log($"Make exited with code {exitCode}", LogType.Error);

                    File.WriteAllText(SplashBuildPaths.BuildLogPath,
                        $"=== STDOUT ===\n{stdout}\n=== STDERR ===\n{stderr}");
                    return false;
                }

                string exeSource = FindCompiledExe(nativeDir);
                if (!string.IsNullOrEmpty(exeSource))
                {
                    File.Copy(exeSource, SplashBuildPaths.CompiledExePath, true);
                    Log("Copied .ps-exe to PSXBuild/", LogType.Log);
                }
                else
                {
                    Log("Warning: Could not find compiled .ps-exe", LogType.Warning);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Compile error: {ex.Message}", LogType.Error);
                return false;
            }
        }

        private string FindCompiledExe(string nativeDir)
        {
            // Look for .ps-exe files in the native dir
            var files = Directory.GetFiles(nativeDir, "*.ps-exe", SearchOption.TopDirectoryOnly);
            if (files.Length > 0)
                return files[0];

            // Also check common build output locations
            foreach (string subdir in new[] { "build", "bin", "." })
            {
                string dir = Path.Combine(nativeDir, subdir);
                if (Directory.Exists(dir))
                {
                    files = Directory.GetFiles(dir, "*.ps-exe", SearchOption.TopDirectoryOnly);
                    if (files.Length > 0)
                        return files[0];
                }
            }
            return null;
        }

        // ───── Step 4: Launch ─────

        private void Launch()
        {
            switch (SplashSettings.Target)
            {
                case BuildTarget.Emulator:
                    LaunchEmulator();
                    break;
                case BuildTarget.RealHardware:
                    LaunchToHardware();
                    break;
                case BuildTarget.ISO:
                    BuildAndLaunchISO();
                    break;
            }
        }

        private void LaunchEmulator()
        {
            string reduxPath = SplashSettings.PCSXReduxPath;
            if (string.IsNullOrEmpty(reduxPath) || !File.Exists(reduxPath))
            {
                Log("PCSX-Redux binary not found.", LogType.Error);
                return;
            }

            string exePath = SplashBuildPaths.CompiledExePath;
            if (!File.Exists(exePath))
            {
                Log("Compiled .ps-exe not found in PSXBuild/.", LogType.Error);
                return;
            }

            // Kill previous instance without clearing the console
            StopAllQuiet();

            string pcdrvBase = SplashBuildPaths.BuildOutputDir;
            string args = $"-exe \"{exePath}\" -run -fastboot -pcdrv -pcdrvbase \"{pcdrvBase}\" -pad1type dualshock -stdout -interpreter";

            Log($"Launching: {Path.GetFileName(reduxPath)} {args}", LogType.Log);

            var psi = new ProcessStartInfo
            {
                FileName = reduxPath,
                Arguments = args,
                UseShellExecute = false,
                // CreateNoWindow = true prevents pcsx-redux's -stdout AllocConsole() from
                // stealing stdout away from our pipe. pcsx-redux is a GUI app and doesn't
                // need a console window - it creates its own OpenGL/SDL window.
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                _emulatorProcess = Process.Start(psi);
                _emulatorProcess.EnableRaisingEvents = true;
                _emulatorProcess.Exited += (s, e) =>
                {
                    EditorApplication.delayCall += () =>
                    {
                        _isRunning = false;
                        _emulatorProcess = null;
                        PSXConsoleWindow.Detach();
                        Repaint();
                    };
                };
                _isRunning = true;
                Log("PCSX-Redux launched.", LogType.Log);

                // Open the PSX Console window and attach to the process output
                PSXConsoleWindow.Attach(_emulatorProcess);
            }
            catch (Exception ex)
            {
                Log($"Failed to launch emulator: {ex.Message}", LogType.Error);
            }
        }

        private void LaunchToHardware()
        {
            string exePath = SplashBuildPaths.CompiledExePath;
            if (!File.Exists(exePath))
            {
                Log("Compiled .ps-exe not found in PSXBuild/.", LogType.Error);
                return;
            }

            string port = SplashSettings.SerialPort;
            int baud = SplashSettings.SerialBaudRate;

            // Stop any previous run (emulator or PCdrv) without clearing the console
            StopAllQuiet();

            // Upload the exe with debug hooks (DEBG → SEXE on the same port).
            // DEBG installs kernel-resident break handlers BEFORE the exe auto-starts.
            // The returned port stays open so PCDrv monitoring can begin immediately.
            Log($"Uploading to {port}...", LogType.Log);
            SerialPort serialPort = UniromUploader.UploadExeForPCdrv(port, baud, exePath,
                msg => Log(msg, LogType.Log));
            if (serialPort == null)
            {
                Log("Upload failed.", LogType.Error);
                return;
            }

            // Start PCdrv host on the same open port — no re-open, no DEBG/CONT needed
            try
            {
                _pcdrvHost = new PCdrvSerialHost(port, baud, SplashBuildPaths.BuildOutputDir,
                    msg => LogToPanel(msg, LogType.Log),
                    msg => PSXConsoleWindow.AddLine(msg));
                _pcdrvHost.Start(serialPort);
                _isRunning = true;
                Log("PCdrv serial host started. Serving files to PS1.", LogType.Log);
            }
            catch (Exception ex)
            {
                Log($"PCdrv host error: {ex.Message}", LogType.Error);
                try { serialPort.Close(); } catch { }
                serialPort.Dispose();
            }
        }

        // ───── ISO Build ─────

        private void BuildAndLaunchISO()
        {
            if (!_hasMkpsxiso)
            {
                Log("mkpsxiso not installed. Click Download in the Toolchain section.", LogType.Error);
                return;
            }

            string exePath = SplashBuildPaths.CompiledExePath;
            if (!File.Exists(exePath))
            {
                Log("Compiled .ps-exe not found in PSXBuild/.", LogType.Error);
                return;
            }

            // Ask user for output location
            string defaultDir = SplashBuildPaths.BuildOutputDir;
            string savePath = EditorUtility.SaveFilePanel(
                "Save ISO Image", defaultDir, "psxsplash", "bin");
            if (string.IsNullOrEmpty(savePath))
            {
                Log("ISO build cancelled.", LogType.Log);
                return;
            }

            string outputBin = savePath;
            string outputCue = Path.ChangeExtension(savePath, ".cue");

            // Step 1: Generate SYSTEM.CNF
            Log("Generating SYSTEM.CNF...", LogType.Log);
            if (!GenerateSystemCnf())
            {
                Log("Failed to generate SYSTEM.CNF.", LogType.Error);
                return;
            }

            // Step 2: Generate XML catalog for mkpsxiso
            Log("Generating ISO catalog...", LogType.Log);
            string xmlPath = GenerateISOCatalog(outputBin, outputCue);
            if (string.IsNullOrEmpty(xmlPath))
            {
                Log("Failed to generate ISO catalog.", LogType.Error);
                return;
            }

            // Step 3: Delete existing .bin/.cue — mkpsxiso won't overwrite them
            try
            {
                if (File.Exists(outputBin)) File.Delete(outputBin);
                if (File.Exists(outputCue)) File.Delete(outputCue);
            }
            catch (Exception ex)
            {
                Log($"Could not remove old ISO files: {ex.Message}", LogType.Error);
                return;
            }

            // Step 4: Run mkpsxiso
            Log("Building ISO image...", LogType.Log);
            bool success = MkpsxisoDownloader.BuildISO(xmlPath, outputBin, outputCue,
                msg => Log(msg, LogType.Log));

            if (success)
            {
                long fileSize = new FileInfo(outputBin).Length;
                Log($"ISO image written: {outputBin} ({fileSize:N0} bytes)", LogType.Log);
                Log($"CUE sheet written: {outputCue}", LogType.Log);

                // Offer to reveal in explorer
                EditorUtility.RevealInFinder(outputBin);
            }
            else
            {
                Log("ISO build failed.", LogType.Error);
            }
        }

        /// <summary>
        /// Derive the executable name on disc from the volume label.
        /// Uppercase, no extension, trimmed to 12 characters (ISO9660 limit).
        /// </summary>
        private static string GetISOExeName()
        {
            string label = SplashSettings.ISOVolumeLabel;
            if (string.IsNullOrEmpty(label)) label = "PSXSPLASH";

            // Uppercase, strip anything not A-Z / 0-9 / underscore
            label = label.ToUpperInvariant();
            var sb = new System.Text.StringBuilder(label.Length);
            foreach (char c in label)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')
                    sb.Append(c);
            }
            string name = sb.ToString();
            if (name.Length == 0) name = "PSXSPLASH";
            if (name.Length > 12) name = name.Substring(0, 12);
            return name;
        }

        private bool GenerateSystemCnf()
        {
            try
            {
                string cnfPath = SplashBuildPaths.SystemCnfPath;

                // The executable name on disc — no extension, max 12 chars
                string exeName = GetISOExeName();

                // SYSTEM.CNF content — the BIOS reads this to launch the executable.
                // BOOT: path to the executable on disc (cdrom:\path;1)
                // TCB: number of thread control blocks (4 is standard)
                // EVENT: number of event control blocks (10 is standard)
                // STACK: initial stack pointer address (top of RAM minus a small margin)
                string content =
                    $"BOOT = cdrom:\\{exeName};1\r\n" +
                    "TCB = 4\r\n" +
                    "EVENT = 10\r\n" +
                    "STACK = 801FFF00\r\n";

                File.WriteAllText(cnfPath, content, new System.Text.UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Log($"SYSTEM.CNF generation error: {ex.Message}", LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// Generates the mkpsxiso XML catalog describing the ISO filesystem layout.
        /// Includes SYSTEM.CNF, the executable, all splashpacks, loading packs, and manifest.
        /// </summary>
        private string GenerateISOCatalog(string outputBin, string outputCue)
        {
            try
            {
                string xmlPath = SplashBuildPaths.ISOCatalogPath;
                string buildDir = SplashBuildPaths.BuildOutputDir;
                string volumeLabel = SplashSettings.ISOVolumeLabel;
                if (string.IsNullOrEmpty(volumeLabel)) volumeLabel = "PSXSPLASH";

                // Sanitize volume label (ISO9660: uppercase, max 31 chars)
                volumeLabel = volumeLabel.ToUpperInvariant();
                if (volumeLabel.Length > 31) volumeLabel = volumeLabel.Substring(0, 31);

                var xml = new System.Text.StringBuilder();
                xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                xml.AppendLine("<iso_project image_name=\"psxsplash.bin\" cue_sheet=\"psxsplash.cue\">");
                xml.AppendLine("  <track type=\"data\">");
                xml.AppendLine("    <identifiers");
                xml.AppendLine("      system=\"PLAYSTATION\"");
                xml.AppendLine("      application=\"PLAYSTATION\"");
                xml.AppendLine($"      volume=\"{EscapeXml(volumeLabel)}\"");
                xml.AppendLine($"      volume_set=\"{EscapeXml(volumeLabel)}\"");
                xml.AppendLine("      publisher=\"SPLASHEDIT\"");
                xml.AppendLine("      data_preparer=\"MKPSXISO\"");
                xml.AppendLine("    />");

                // License file (optional)
                string licensePath = SplashSettings.LicenseFilePath;
                if (!string.IsNullOrEmpty(licensePath) && File.Exists(licensePath))
                {
                    xml.AppendLine($"    <license file=\"{EscapeXml(licensePath)}\"/>");
                }

                xml.AppendLine("    <directory_tree>");

                // SYSTEM.CNF — must be first for BIOS to find it
                string cnfPath = SplashBuildPaths.SystemCnfPath;
                xml.AppendLine($"      <file name=\"SYSTEM.CNF\" source=\"{EscapeXml(cnfPath)}\"/>");

                // The executable — renamed to match what SYSTEM.CNF points to
                string exePath = SplashBuildPaths.CompiledExePath;
                string isoExeName = GetISOExeName();
                xml.AppendLine($"      <file name=\"{isoExeName}\" source=\"{EscapeXml(exePath)}\"/>");

                // Manifest
                string manifestPath = SplashBuildPaths.ManifestPath;
                if (File.Exists(manifestPath))
                {
                    xml.AppendLine($"      <file name=\"MANIFEST.BIN\" source=\"{EscapeXml(manifestPath)}\"/>");
                }

                // Scene splashpacks, VRAM data, SPU data, and loading packs
                for (int i = 0; i < _sceneList.Count; i++)
                {
                    string splashpack = SplashBuildPaths.GetSceneSplashpackPath(i, _sceneList[i].name);
                    if (File.Exists(splashpack))
                    {
                        string isoName = $"SCENE_{i}.SPK";
                        xml.AppendLine($"      <file name=\"{isoName}\" source=\"{EscapeXml(splashpack)}\"/>");
                    }

                    string vramFile = SplashBuildPaths.GetSceneVramPath(i, _sceneList[i].name);
                    if (File.Exists(vramFile))
                    {
                        string isoName = $"SCENE_{i}.VRM";
                        xml.AppendLine($"      <file name=\"{isoName}\" source=\"{EscapeXml(vramFile)}\"/>");
                    }

                    string spuFile = SplashBuildPaths.GetSceneSpuPath(i, _sceneList[i].name);
                    if (File.Exists(spuFile))
                    {
                        string isoName = $"SCENE_{i}.SPU";
                        xml.AppendLine($"      <file name=\"{isoName}\" source=\"{EscapeXml(spuFile)}\"/>");
                    }

                    string loadingPack = SplashBuildPaths.GetSceneLoaderPackPath(i, _sceneList[i].name);
                    if (File.Exists(loadingPack))
                    {
                        string isoName = $"SCENE_{i}.LDG";
                        xml.AppendLine($"      <file name=\"{isoName}\" source=\"{EscapeXml(loadingPack)}\"/>");
                    }
                }

                // Trailing dummy sectors to prevent drive runaway
                xml.AppendLine("      <dummy sectors=\"128\"/>");
                xml.AppendLine("    </directory_tree>");
                xml.AppendLine("  </track>");

                if (_musicList.Count > 0)
                {
                    foreach (MusicEntry music in _musicList)
                    {
                        string musicPath = Path.Combine(SplashBuildPaths.ProjectRoot, music.path);
                        xml.AppendLine($"  <track type=\"audio\" source=\"{EscapeXml(musicPath)}\"/>");
                    }
                }

                xml.AppendLine("</iso_project>");

                File.WriteAllText(xmlPath, xml.ToString(), new System.Text.UTF8Encoding(false));
                Log($"ISO catalog written: {xmlPath}", LogType.Log);
                return xmlPath;
            }
            catch (Exception ex)
            {
                Log($"ISO catalog generation error: {ex.Message}", LogType.Error);
                return null;
            }
        }

        private static string EscapeXml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;")
                    .Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private void StopPCdrvHost()
        {
            if (_pcdrvHost != null)
            {
                _pcdrvHost.Dispose();
                _pcdrvHost = null;
            }
        }

        /// <summary>
        /// Stops everything (emulator, PCdrv host, console reader) — used by the STOP button.
        /// </summary>
        private void StopAll()
        {
            PSXConsoleWindow.Detach();
            StopEmulatorProcess();
            StopPCdrvHost();
            _isRunning = false;
            Log("Stopped.", LogType.Log);
        }

        /// <summary>
        /// Stops emulator and PCdrv host without touching the console window.
        /// Used before re-launching so the console keeps its history.
        /// </summary>
        private void StopAllQuiet()
        {
            StopEmulatorProcess();
            StopPCdrvHost();
            _isRunning = false;
        }

        private void StopEmulatorProcess()
        {
            if (_emulatorProcess != null && !_emulatorProcess.HasExited)
            {
                try
                {
                    _emulatorProcess.Kill();
                    _emulatorProcess.Dispose();
                }
                catch { }
            }
            _emulatorProcess = null;
        }

        // ═══════════════════════════════════════════════════════════════
        // Toolchain Detection & Install
        // ═══════════════════════════════════════════════════════════════

        private static string WrapCommandForMacOS(string dir, string makeCmd)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
                return $"/c \"cd /d \"{dir}\" && {makeCmd}\"";

            string pathExport = "";
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                pathExport = $"export PATH=\\\"{home}/mipsel-none-elf/bin:{home}/bin:/opt/homebrew/bin:/usr/local/bin:$PATH\\\" && ";
            }

            return $"-c \"{pathExport}cd \\\"{dir}\\\" && {makeCmd}\"";
        }

        private void RefreshToolchainStatus()
        {
            _hasMIPS = ToolchainChecker.IsToolAvailable(
                Application.platform == RuntimePlatform.WindowsEditor
                    ? "mipsel-none-elf-gcc"
                    : "mipsel-linux-gnu-gcc");

            _hasMake = ToolchainChecker.IsToolAvailable("make");

            string reduxBin = SplashSettings.PCSXReduxPath;
            _hasRedux = !string.IsNullOrEmpty(reduxBin) && File.Exists(reduxBin);
            _reduxVersion = _hasRedux ? "Installed" : "";

            _hasPsxavenc = PSXAudioConverter.IsInstalled();

            _hasMkpsxiso = MkpsxisoDownloader.IsInstalled();

            string nativeDir = SplashBuildPaths.NativeSourceDir;
            _hasNativeProject = !string.IsNullOrEmpty(nativeDir) && Directory.Exists(nativeDir);
        }

        private void RefreshNativeProjectStatus(bool force)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!force && now < _nextNativeStatusRefreshTime)
                return;

            _nextNativeStatusRefreshTime = now + NativeStatusRefreshIntervalSec;
            _isGitAvailable = PSXSplashInstaller.IsGitAvailable();
            _isNativeRepoInstalled = PSXSplashInstaller.IsInstalled();
        }

        private async void InstallMIPS()
        {
            Log("Installing MIPS toolchain...", LogType.Log);
            try
            {
                await ToolchainInstaller.InstallToolchain();
                Log("MIPS toolchain installation started. You may need to restart.", LogType.Log);
            }
            catch (Exception ex)
            {
                Log($"MIPS install error: {ex.Message}", LogType.Error);
            }
            RefreshToolchainStatus();
            Repaint();
        }

        private async void InstallMake()
        {
            Log("Installing GNU Make...", LogType.Log);
            try
            {
                await ToolchainInstaller.InstallMake();
                Log("GNU Make installation complete.", LogType.Log);
            }
            catch (Exception ex)
            {
                Log($"Make install error: {ex.Message}", LogType.Error);
            }
            RefreshToolchainStatus();
            Repaint();
        }

        private async void DownloadRedux()
        {
            Log("Downloading PCSX-Redux...", LogType.Log);
            bool success = await PCSXReduxDownloader.DownloadAndInstall(msg => Log(msg, LogType.Log));
            if (success)
            {
                // Clear any custom path so it uses the auto-downloaded one
                SplashSettings.PCSXReduxPath = "";
                RefreshToolchainStatus();
                Log("PCSX-Redux ready!", LogType.Log);
            }
            else
            {
                // Fall back to manual selection
                Log("Auto-download failed. Select binary manually.", LogType.Warning);
                string path = EditorUtility.OpenFilePanel("Select PCSX-Redux Binary", "",
                    Application.platform == RuntimePlatform.WindowsEditor ? "exe" : "");
                if (!string.IsNullOrEmpty(path))
                {
                    SplashSettings.PCSXReduxPath = path;
                    RefreshToolchainStatus();
                    Log($"PCSX-Redux set: {path}", LogType.Log);
                }
            }
            Repaint();
        }

        private async void DownloadPsxavenc()
        {
            Log("Downloading psxavenc audio encoder...", LogType.Log);
            bool success = await PSXAudioConverter.DownloadAndInstall(msg => Log(msg, LogType.Log));
            if (success)
            {
                RefreshToolchainStatus();
                Log("psxavenc ready!", LogType.Log);
            }
            else
            {
                Log("psxavenc download failed. Audio export will not work.", LogType.Error);
            }
            Repaint();
        }

        private async void DownloadMkpsxiso()
        {
            Log("Downloading mkpsxiso ISO builder...", LogType.Log);
            bool success = await MkpsxisoDownloader.DownloadAndInstall(msg => Log(msg, LogType.Log));
            if (success)
            {
                RefreshToolchainStatus();
                Log("mkpsxiso ready!", LogType.Log);
            }
            else
            {
                Log("mkpsxiso download failed. ISO builds will not work.", LogType.Error);
            }
            Repaint();
        }

        private void ScanSerialPorts()
        {
            try
            {
                string[] ports = System.IO.Ports.SerialPort.GetPortNames();
                if (ports.Length == 0)
                {
                    Log("No serial ports found.", LogType.Warning);
                }
                else
                {
                    Log($"Available ports: {string.Join(", ", ports)}", LogType.Log);
                    // Auto-select first port if current is empty
                    if (string.IsNullOrEmpty(SplashSettings.SerialPort))
                        SplashSettings.SerialPort = ports[0];
                }
            }
            catch (Exception ex)
            {
                Log($"Error scanning ports: {ex.Message}", LogType.Error);
            }
        }

        // ───── Release fetching & management ─────

        private async void FetchGitHubReleases()
        {
            _isFetchingReleases = true;
            Repaint();

            try
            {
                var releases = await PSXSplashInstaller.FetchReleasesAsync();
                if (releases.Count > 0)
                {
                    _releaseDisplayNames = releases
                        .Select(r =>
                        {
                            string label = r.TagName;
                            if (!string.IsNullOrEmpty(r.Name) && r.Name != r.TagName)
                                label += $" — {r.Name}";
                            if (r.IsPrerelease)
                                label += " (pre-release)";
                            return label;
                        })
                        .ToArray();

                    // Try to select the currently checked-out tag
                    if (!string.IsNullOrEmpty(_currentTag))
                    {
                        int idx = releases.FindIndex(r => r.TagName == _currentTag);
                        if (idx >= 0) _selectedReleaseIndex = idx;
                    }

                    _selectedReleaseIndex = Mathf.Clamp(_selectedReleaseIndex, 0, _releaseDisplayNames.Length - 1);
                }
                else
                {
                    _releaseDisplayNames = new string[0];
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to fetch releases: {ex.Message}", LogType.Warning);
            }
            finally
            {
                _isFetchingReleases = false;
                Repaint();
            }
        }

        private void RefreshCurrentTag()
        {
            _currentTag = PSXSplashInstaller.GetCurrentTag() ?? "";
        }

        // ───── Native Project Clone/Switch ─────

        private async void CloneNativeProject()
        {
            if (_selectedReleaseIndex < 0 || _selectedReleaseIndex >= PSXSplashInstaller.CachedReleases.Count)
                return;

            string tag = PSXSplashInstaller.CachedReleases[_selectedReleaseIndex].TagName;

            _isInstallingNative = true;
            _nativeInstallStatus = $"Downloading psxsplash {tag} (this may take a minute)...";
            Repaint();

            Log($"Downloading psxsplash {tag} from GitHub...", LogType.Log);

            try
            {
                bool success = await PSXSplashInstaller.InstallRelease(tag, msg =>
                {
                    _nativeInstallStatus = msg;
                    Repaint();
                });

                if (success)
                {
                    Log($"psxsplash {tag} downloaded successfully!", LogType.Log);
                    _nativeInstallStatus = "";
                    RefreshToolchainStatus();
                    RefreshNativeProjectStatus(force: true);
                    RefreshCurrentTag();
                }
                else
                {
                    Log("Download failed. Check console for errors.", LogType.Error);
                    _nativeInstallStatus = "Download failed — check console for details.";
                }
            }
            catch (Exception ex)
            {
                Log($"Download error: {ex.Message}", LogType.Error);
                _nativeInstallStatus = $"Error: {ex.Message}";
            }
            finally
            {
                _isInstallingNative = false;
                Repaint();
            }
        }

        private async void SwitchNativeRelease()
        {
            if (_selectedReleaseIndex < 0 || _selectedReleaseIndex >= PSXSplashInstaller.CachedReleases.Count)
                return;

            string tag = PSXSplashInstaller.CachedReleases[_selectedReleaseIndex].TagName;

            if (tag == _currentTag)
            {
                Log($"Already on {tag}.", LogType.Log);
                return;
            }

            _isSwitchingRelease = true;
            _nativeInstallStatus = $"Switching to {tag}...";
            Repaint();

            Log($"Switching native project to {tag}...", LogType.Log);

            try
            {
                bool success = await PSXSplashInstaller.SwitchToReleaseAsync(tag, msg =>
                {
                    _nativeInstallStatus = msg;
                    Repaint();
                });

                if (success)
                {
                    Log($"Switched to {tag}.", LogType.Log);
                    _nativeInstallStatus = "";
                    RefreshNativeProjectStatus(force: true);
                    RefreshCurrentTag();
                }
                else
                {
                    Log($"Failed to switch to {tag}.", LogType.Error);
                    _nativeInstallStatus = "Switch failed — check console for details.";
                }
            }
            catch (Exception ex)
            {
                Log($"Switch error: {ex.Message}", LogType.Error);
                _nativeInstallStatus = $"Error: {ex.Message}";
            }
            finally
            {
                _isSwitchingRelease = false;
                Repaint();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Scene List Persistence (EditorPrefs)
        // ═══════════════════════════════════════════════════════════════

        private void LoadSceneList()
        {
            _sceneList.Clear();
            string prefix = "SplashEdit_" + Application.dataPath.GetHashCode().ToString("X8") + "_";
            int count = EditorPrefs.GetInt(prefix + "SceneCount", 0);

            for (int i = 0; i < count; i++)
            {
                string path = EditorPrefs.GetString(prefix + $"Scene_{i}", "");
                if (string.IsNullOrEmpty(path)) continue;

                var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
                _sceneList.Add(new SceneEntry
                {
                    asset = asset,
                    path = path,
                    name = asset != null ? asset.name : Path.GetFileNameWithoutExtension(path)
                });
            }
        }

        private void SaveSceneList()
        {
            string prefix = "SplashEdit_" + Application.dataPath.GetHashCode().ToString("X8") + "_";
            EditorPrefs.SetInt(prefix + "SceneCount", _sceneList.Count);

            for (int i = 0; i < _sceneList.Count; i++)
            {
                EditorPrefs.SetString(prefix + $"Scene_{i}", _sceneList[i].path);
            }
        }

        private void AddCurrentScene()
        {
            string scenePath = SceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath))
            {
                Log("Current scene is not saved. Save it first.", LogType.Warning);
                return;
            }
            AddSceneByPath(scenePath);
        }

        private void AddSceneByPath(string path)
        {
            // Check for duplicates
            if (_sceneList.Any(s => s.path == path))
            {
                Log($"Scene already in list: {path}", LogType.Warning);
                return;
            }

            var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            _sceneList.Add(new SceneEntry
            {
                asset = asset,
                path = path,
                name = asset != null ? asset.name : Path.GetFileNameWithoutExtension(path)
            });
            SaveSceneList();
            Log($"Added scene: {Path.GetFileNameWithoutExtension(path)}", LogType.Log);
        }

        private void HandleSceneDragDrop()
        {
            Event evt = Event.current;
            Rect dropArea = GUILayoutUtility.GetLastRect();

            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                if (!dropArea.Contains(evt.mousePosition)) return;

                bool hasScenes = DragAndDrop.objectReferences.Any(o => o is SceneAsset);
                if (hasScenes)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        foreach (var obj in DragAndDrop.objectReferences)
                        {
                            if (obj is SceneAsset)
                            {
                                string path = AssetDatabase.GetAssetPath(obj);
                                AddSceneByPath(path);
                            }
                        }
                    }

                    evt.Use();
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Music List Persistence (EditorPrefs)
        // ═══════════════════════════════════════════════════════════════

        private void LoadMusicList()
        {
            _musicList.Clear();
            string prefix = "SplashEdit_" + Application.dataPath.GetHashCode().ToString("X8") + "_";
            int count = EditorPrefs.GetInt(prefix + "MusicCount", 0);

            for (int i = 0; i < count; i++)
            {
                string path = EditorPrefs.GetString(prefix + $"Music_{i}", "");
                if (string.IsNullOrEmpty(path)) continue;

                var asset = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                _musicList.Add(new MusicEntry
                {
                    asset = asset,
                    path = path,
                    name = asset != null ? asset.name : Path.GetFileNameWithoutExtension(path)
                });
            }
        }

        private void SaveMusicList()
        {
            string prefix = "SplashEdit_" + Application.dataPath.GetHashCode().ToString("X8") + "_";
            EditorPrefs.SetInt(prefix + "MusicCount", _musicList.Count);

            for (int i = 0; i < _musicList.Count; i++)
            {
                EditorPrefs.SetString(prefix + $"Music_{i}", _musicList[i].path);
            }
        }

        private void AddMusicByPath(string path)
        {
            // Check for duplicates
            if (_musicList.Any(s => s.path == path))
            {
                Log($"Music already in list: {path}", LogType.Warning);
                return;
            }

            var asset = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            _musicList.Add(new MusicEntry
            {
                asset = asset,
                path = path,
                name = asset != null ? asset.name : Path.GetFileNameWithoutExtension(path)
            });
            SaveMusicList();
            Log($"Added music: {Path.GetFileNameWithoutExtension(path)}", LogType.Log);
        }

        private void HandleMusicDragDrop()
        {
            Event evt = Event.current;
            Rect dropArea = GUILayoutUtility.GetLastRect();

            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                if (!dropArea.Contains(evt.mousePosition)) return;

                bool hasClips = DragAndDrop.objectReferences.Any(o => o is AudioClip);
                if (hasClips)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        foreach (var obj in DragAndDrop.objectReferences)
                        {
                            if (obj is AudioClip)
                            {
                                string path = AssetDatabase.GetAssetPath(obj);
                                AddMusicByPath(path);
                            }
                        }
                    }

                    evt.Use();
                }
            }
        }


        // ═══════════════════════════════════════════════════════════════
        // Utilities
        // ═══════════════════════════════════════════════════════════════

        private static void Log(string message, LogType type)
        {
            bool isError = type == LogType.Error;
            PSXConsoleWindow.AddLine(message, isError);

            // Always log to Unity console as a fallback.
            switch (type)
            {
                case LogType.Error:
                    Debug.LogError($"[SplashEdit] {message}");
                    break;
                case LogType.Warning:
                    Debug.LogWarning($"[SplashEdit] {message}");
                    break;
                default:
                    Debug.Log($"[SplashEdit] {message}");
                    break;
            }
        }

        /// <summary>
        /// Writes make stdout/stderr to PSX Console and Unity console.
        /// </summary>
        private static void LogToPanel(string message, LogType type)
        {
            PSXConsoleWindow.AddLine(message, type == LogType.Error);
            Debug.Log($"[SplashEdit Build] {message}");
        }

        private bool DrawSectionFoldout(string title, bool isOpen)
        {
            EditorGUILayout.BeginHorizontal();
            isOpen = EditorGUILayout.Foldout(isOpen, title, true, PSXEditorStyles.SectionHeader);
            EditorGUILayout.EndHorizontal();
            return isOpen;
        }

        private void DrawStatusIcon(bool ok)
        {
            var content = ok
                ? EditorGUIUtility.IconContent("d_greenLight")
                : EditorGUIUtility.IconContent("d_redLight");
            GUILayout.Label(content, GUILayout.Width(20), GUILayout.Height(20));
        }

        private string TruncatePath(string path, int maxLen)
        {
            if (path.Length <= maxLen) return path;
            return "..." + path.Substring(path.Length - maxLen + 3);
        }
    }
}
