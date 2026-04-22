using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// A live console window that displays stdout/stderr from PCSX-Redux and PSX build output.
    /// Opens automatically when a build starts or the emulator launches.
    /// </summary>
    public class PSXConsoleWindow : EditorWindow
    {
        private const string WINDOW_TITLE = "PSX Console";
        private const string MENU_PATH = "PlayStation 1/PSX Console";
        private const int MAX_LINES = 2000;
        private const int TRIM_AMOUNT = 500;

        // ── Shared state (set by SplashControlPanel) ──
        private static Process _process;
        private static readonly List<LogLine> _lines = new List<LogLine>();
        private static readonly object _lock = new object();
        private static volatile bool _autoScroll = true;
        private static volatile bool _reading;

        // ── Instance state ──
        private Vector2 _scrollPos;
        private string _filterText = "";
        private bool _showStdout = true;
        private bool _showStderr = true;
        private bool _wrapLines = true;
        private GUIStyle _monoStyle;
        private GUIStyle _monoStyleErr;
        private GUIStyle _monoStyleSelected;
        private int _lastLineCount;

        // ── Selection state (for shift-click range and right-click copy) ──
        private int _selectionAnchor = -1;  // first clicked line index (into _lines)
        private int _selectionEnd = -1;     // last shift-clicked line index (into _lines)

        private struct LogLine
        {
            public string text;
            public bool isError;
            public string timestamp;
        }

        // ═══════════════════════════════════════════════════════════════
        // Menu
        // ═══════════════════════════════════════════════════════════════

        [MenuItem(MENU_PATH, false, 10)]
        public static void ShowWindow()
        {
            var window = GetWindow<PSXConsoleWindow>();
            window.titleContent = new GUIContent(WINDOW_TITLE, EditorGUIUtility.IconContent("d_UnityEditor.ConsoleWindow").image);
            window.minSize = new Vector2(400, 200);
            window.Show();
        }

        // ═══════════════════════════════════════════════════════════════
        // Public API — called by SplashControlPanel
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Adds a line to the console from any source (serial host, emulator fallback, etc.).
        /// Thread-safe. Works whether the window is open or not.
        /// </summary>
        public static void AddLine(string text, bool isError = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            lock (_lock)
            {
                _lines.Add(new LogLine
                {
                    text = text,
                    isError = isError,
                    timestamp = DateTime.Now.ToString("HH:mm:ss.fff")
                });

                if (_lines.Count > MAX_LINES)
                {
                    _lines.RemoveRange(0, TRIM_AMOUNT);
                }
            }

            // Repaint is handled by OnEditorUpdate polling _lines.Count changes.
            // Do NOT call EditorApplication.delayCall here - AddLine is called
            // from background threads (serial host, process readers) and
            // delayCall is not thread-safe. It kills the calling thread.
        }

        /// <summary>
        /// Opens the console window and begins capturing output from the given process.
        /// The process must have RedirectStandardOutput and RedirectStandardError enabled.
        /// </summary>
        public static PSXConsoleWindow Attach(Process process)
        {
            // Stop reading from any previous process (but keep existing lines)
            _reading = false;

            _process = process;

            var window = GetWindow<PSXConsoleWindow>();
            window.titleContent = new GUIContent(WINDOW_TITLE, EditorGUIUtility.IconContent("d_UnityEditor.ConsoleWindow").image);
            window.minSize = new Vector2(400, 200);
            window.Show();

            // Start async readers
            _reading = true;
            StartReader(process.StandardOutput, false);
            StartReader(process.StandardError, true);

            return window;
        }

        /// <summary>
        /// Stops reading and detaches from the current process.
        /// </summary>
        public static void Detach()
        {
            _reading = false;
            _process = null;
        }

        // ═══════════════════════════════════════════════════════════════
        // Async readers
        // ═══════════════════════════════════════════════════════════════

        private static void StartReader(System.IO.StreamReader reader, bool isError)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    while (_reading && !reader.EndOfStream)
                    {
                        string line = reader.ReadLine();
                        if (line == null) break;

                        lock (_lock)
                        {
                            _lines.Add(new LogLine
                            {
                                text = line,
                                isError = isError,
                                timestamp = DateTime.Now.ToString("HH:mm:ss.fff")
                            });

                            // Trim if too many lines
                            if (_lines.Count > MAX_LINES)
                            {
                                _lines.RemoveRange(0, TRIM_AMOUNT);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Stream closed — normal when process exits
                }
            })
            {
                IsBackground = true,
                Name = isError ? "PSXConsole-stderr" : "PSXConsole-stdout"
            };
            thread.Start();
        }

        // ═══════════════════════════════════════════════════════════════
        // Window lifecycle
        // ═══════════════════════════════════════════════════════════════

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            // Repaint when new lines arrive
            int count;
            lock (_lock) { count = _lines.Count; }
            if (count != _lastLineCount)
            {
                _lastLineCount = count;
                Repaint();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // GUI
        // ═══════════════════════════════════════════════════════════════

        private void EnsureStyles()
        {
            if (_monoStyle == null)
            {
                _monoStyle = new GUIStyle(EditorStyles.label)
                {
                    font = Font.CreateDynamicFontFromOSFont(PSXEditorStyles.MonoFontName, 12),
                    fontSize = 11,
                    richText = false,
                    wordWrap = _wrapLines,
                    normal = { textColor = new Color(0.85f, 0.85f, 0.85f) },
                    padding = new RectOffset(4, 4, 1, 1),
                    margin = new RectOffset(0, 0, 0, 0)
                };
            }
            if (_monoStyleErr == null)
            {
                _monoStyleErr = new GUIStyle(_monoStyle)
                {
                    normal = { textColor = new Color(1f, 0.45f, 0.4f) }
                };
            }
            if (_monoStyleSelected == null)
            {
                _monoStyleSelected = new GUIStyle(_monoStyle)
                {
                    normal =
                    {
                        textColor = new Color(0.95f, 0.95f, 0.95f),
                        background = MakeSolidTexture(new Color(0.25f, 0.40f, 0.65f, 0.6f))
                    }
                };
            }
            _monoStyle.wordWrap = _wrapLines;
            _monoStyleErr.wordWrap = _wrapLines;
            _monoStyleSelected.wordWrap = _wrapLines;
        }

        private static Texture2D MakeSolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        // Snapshot taken at the start of each OnGUI so Layout and Repaint
        // events always see the same line count (prevents "Getting control
        // position in a group with only N controls" errors).
        private LogLine[] _snapshot = Array.Empty<LogLine>();

        private void OnGUI()
        {
            EnsureStyles();

            // Take a snapshot once per OnGUI so Layout and Repaint see
            // identical control counts even if background threads add lines.
            if (Event.current.type == EventType.Layout)
            {
                lock (_lock)
                {
                    _snapshot = _lines.ToArray();
                }
            }

            DrawToolbar();
            DrawConsoleOutput();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Process status
            bool alive = _process != null && !_process.HasExited;
            var statusColor = GUI.contentColor;
            GUI.contentColor = alive ? Color.green : Color.gray;
            GUILayout.Label(alive ? "● Live" : "● Stopped", EditorStyles.toolbarButton, GUILayout.Width(60));
            GUI.contentColor = statusColor;

            // Filter
            GUILayout.Label("Filter:", GUILayout.Width(40));
            _filterText = EditorGUILayout.TextField(_filterText, EditorStyles.toolbarSearchField, GUILayout.Width(150));

            GUILayout.FlexibleSpace();

            // Toggles
            _showStdout = GUILayout.Toggle(_showStdout, "stdout", EditorStyles.toolbarButton, GUILayout.Width(50));
            _showStderr = GUILayout.Toggle(_showStderr, "stderr", EditorStyles.toolbarButton, GUILayout.Width(50));
            _wrapLines = GUILayout.Toggle(_wrapLines, "Wrap", EditorStyles.toolbarButton, GUILayout.Width(40));

            // Auto-scroll
            _autoScroll = GUILayout.Toggle(_autoScroll, "Auto↓", EditorStyles.toolbarButton, GUILayout.Width(50));

            // Clear
            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(45)))
            {
                lock (_lock) { _lines.Clear(); }
            }

            // Copy all
            if (GUILayout.Button("Copy", EditorStyles.toolbarButton, GUILayout.Width(40)))
            {
                CopyToClipboard();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawConsoleOutput()
        {
            // Simple scroll view - no BeginArea/EndArea mixing that causes layout errors.
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.ExpandHeight(true));

            // Dark background behind the scroll content
            Rect scrollBg = EditorGUILayout.BeginVertical();
            EditorGUI.DrawRect(scrollBg, new Color(0.13f, 0.13f, 0.15f));

            bool hasFilter = !string.IsNullOrEmpty(_filterText);
            string filterLower = hasFilter ? _filterText.ToLowerInvariant() : null;

            int selMin = Mathf.Min(_selectionAnchor, _selectionEnd);
            int selMax = Mathf.Max(_selectionAnchor, _selectionEnd);
            bool hasSelection = _selectionAnchor >= 0 && _selectionEnd >= 0;

            // Iterate the snapshot taken during Layout so the control count
            // is stable across Layout and Repaint events.
            var snapshot = _snapshot;

            if (snapshot.Length == 0)
            {
                GUILayout.Label("Waiting for output...", EditorStyles.centeredGreyMiniLabel);
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                var line = snapshot[i];

                if (line.isError && !_showStderr) continue;
                if (!line.isError && !_showStdout) continue;
                if (hasFilter && line.text.ToLowerInvariant().IndexOf(filterLower, StringComparison.Ordinal) < 0)
                    continue;

                bool selected = hasSelection && i >= selMin && i <= selMax;
                GUIStyle style = selected ? _monoStyleSelected : (line.isError ? _monoStyleErr : _monoStyle);

                string label = $"[{line.timestamp}] {line.text}";
                GUILayout.Label(label, style);

                // Handle click/right-click on last drawn rect
                Rect lineRect = GUILayoutUtility.GetLastRect();
                Event evt = Event.current;
                if (evt.type == EventType.MouseDown && lineRect.Contains(evt.mousePosition))
                {
                    if (evt.button == 0)
                    {
                        if (evt.shift && _selectionAnchor >= 0)
                            _selectionEnd = i;
                        else
                        {
                            _selectionAnchor = i;
                            _selectionEnd = i;
                        }
                        evt.Use();
                        Repaint();
                    }
                    else if (evt.button == 1)
                    {
                        int clickedLine = i;
                        bool lineInSelection = hasSelection && clickedLine >= selMin && clickedLine <= selMax;
                        var menu = new GenericMenu();
                        if (lineInSelection && selMin != selMax)
                        {
                            menu.AddItem(new GUIContent("Copy selected lines"), false, () => CopyRange(selMin, selMax));
                            menu.AddSeparator("");
                        }
                        menu.AddItem(new GUIContent("Copy this line"), false, () =>
                        {
                            string text;
                            lock (_lock)
                            {
                                text = clickedLine < _lines.Count
                                    ? $"[{_lines[clickedLine].timestamp}] {_lines[clickedLine].text}"
                                    : "";
                            }
                            EditorGUIUtility.systemCopyBuffer = text;
                        });
                        menu.ShowAsContext();
                        evt.Use();
                    }
                }
            }

            EditorGUILayout.EndVertical();

            if (_autoScroll)
                _scrollPos.y = float.MaxValue;

            EditorGUILayout.EndScrollView();
        }

        private void CopyRange(int fromIndex, int toIndex)
        {
            var sb = new StringBuilder();
            lock (_lock)
            {
                int lo = Mathf.Min(fromIndex, toIndex);
                int hi = Mathf.Max(fromIndex, toIndex);
                for (int i = lo; i <= hi && i < _lines.Count; i++)
                {
                    string prefix = _lines[i].isError ? "[ERR]" : "[OUT]";
                    sb.AppendLine($"[{_lines[i].timestamp}] {prefix} {_lines[i].text}");
                }
            }
            EditorGUIUtility.systemCopyBuffer = sb.ToString();
        }

        private void CopyToClipboard()
        {
            var sb = new StringBuilder();
            lock (_lock)
            {
                foreach (var line in _lines)
                {
                    string prefix = line.isError ? "[ERR]" : "[OUT]";
                    sb.AppendLine($"[{line.timestamp}] {prefix} {line.text}");
                }
            }
            EditorGUIUtility.systemCopyBuffer = sb.ToString();
        }
    }
}
