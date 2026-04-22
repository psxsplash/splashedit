using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// Unified styling system for PSX Splash editor windows.
    /// Provides consistent colors, fonts, icons, and GUIStyles across the entire plugin.
    /// </summary>
    [InitializeOnLoad]
    public static class PSXEditorStyles
    {
        static PSXEditorStyles()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void OnBeforeAssemblyReload()
        {
            foreach (var tex in _textureCache.Values)
            {
                if (tex != null)
                    Object.DestroyImmediate(tex);
            }
            _textureCache.Clear();
            _styleCache.Clear();
        }

        #region Colors - PS1 Inspired Palette
        
        // Primary colors
        public static readonly Color PrimaryBlue = new Color(0.15f, 0.35f, 0.65f);
        public static readonly Color PrimaryDark = new Color(0.12f, 0.12f, 0.14f);
        public static readonly Color PrimaryLight = new Color(0.22f, 0.22f, 0.25f);
        
        // Accent colors
        public static readonly Color AccentGold = new Color(0.95f, 0.75f, 0.2f);
        public static readonly Color AccentCyan = new Color(0.3f, 0.85f, 0.95f);
        public static readonly Color AccentMagenta = new Color(0.85f, 0.3f, 0.65f);
        public static readonly Color AccentGreen = new Color(0.35f, 0.85f, 0.45f);
        
        // Semantic colors
        public static readonly Color Success = new Color(0.35f, 0.8f, 0.4f);
        public static readonly Color Warning = new Color(0.95f, 0.75f, 0.2f);
        public static readonly Color Error = new Color(0.9f, 0.3f, 0.35f);
        public static readonly Color Info = new Color(0.4f, 0.7f, 0.95f);
        
        // Background colors
        public static readonly Color BackgroundDark = new Color(0.15f, 0.15f, 0.17f);
        public static readonly Color BackgroundMedium = new Color(0.2f, 0.2f, 0.22f);
        public static readonly Color BackgroundLight = new Color(0.28f, 0.28f, 0.3f);
        public static readonly Color BackgroundHighlight = new Color(0.25f, 0.35f, 0.5f);
        
        // Text colors
        public static readonly Color TextPrimary = new Color(0.9f, 0.9f, 0.92f);
        public static readonly Color TextSecondary = new Color(0.65f, 0.65f, 0.7f);
        public static readonly Color TextMuted = new Color(0.45f, 0.45f, 0.5f);
        
        // VRAM specific colors
        public static readonly Color VRAMFrameBuffer1 = new Color(1f, 0.3f, 0.3f, 0.4f);
        public static readonly Color VRAMFrameBuffer2 = new Color(0.3f, 1f, 0.3f, 0.4f);
        public static readonly Color VRAMProhibited = new Color(1f, 0f, 0f, 0.25f);
        public static readonly Color VRAMTexture = new Color(0.3f, 0.6f, 1f, 0.5f);
        public static readonly Color VRAMCLUT = new Color(1f, 0.6f, 0.3f, 0.5f);
        
        #endregion
        
        #region Cached Styles
        
        private static Dictionary<string, GUIStyle> _styleCache = new Dictionary<string, GUIStyle>();
        private static Dictionary<string, Texture2D> _textureCache = new Dictionary<string, Texture2D>();
        
        #endregion
        
        #region Textures
        
        public static Texture2D GetSolidTexture(Color color)
        {
            string key = $"solid_{color.r}_{color.g}_{color.b}_{color.a}";
            if (!_textureCache.TryGetValue(key, out var tex) || tex == null)
            {
                tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, color);
                tex.Apply();
                tex.hideFlags = HideFlags.HideAndDontSave;
                _textureCache[key] = tex;
            }
            return tex;
        }
        
        public static Texture2D CreateGradientTexture(int width, int height, Color top, Color bottom)
        {
            Texture2D tex = new Texture2D(width, height);
            for (int y = 0; y < height; y++)
            {
                Color c = Color.Lerp(bottom, top, (float)y / height);
                for (int x = 0; x < width; x++)
                {
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
        
        public static Texture2D CreateRoundedRect(int width, int height, int radius, Color fillColor, Color borderColor, int borderWidth = 1)
        {
            Texture2D tex = new Texture2D(width, height);
            Color transparent = new Color(0, 0, 0, 0);
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Check if pixel is within rounded corners
                    bool inCorner = false;
                    float dist = 0;
                    
                    // Top-left
                    if (x < radius && y > height - radius - 1)
                    {
                        dist = Vector2.Distance(new Vector2(x, y), new Vector2(radius, height - radius - 1));
                        inCorner = true;
                    }
                    // Top-right
                    else if (x > width - radius - 1 && y > height - radius - 1)
                    {
                        dist = Vector2.Distance(new Vector2(x, y), new Vector2(width - radius - 1, height - radius - 1));
                        inCorner = true;
                    }
                    // Bottom-left
                    else if (x < radius && y < radius)
                    {
                        dist = Vector2.Distance(new Vector2(x, y), new Vector2(radius, radius));
                        inCorner = true;
                    }
                    // Bottom-right
                    else if (x > width - radius - 1 && y < radius)
                    {
                        dist = Vector2.Distance(new Vector2(x, y), new Vector2(width - radius - 1, radius));
                        inCorner = true;
                    }
                    
                    if (inCorner)
                    {
                        if (dist > radius)
                            tex.SetPixel(x, y, transparent);
                        else if (dist > radius - borderWidth)
                            tex.SetPixel(x, y, borderColor);
                        else
                            tex.SetPixel(x, y, fillColor);
                    }
                    else
                    {
                        // Check border
                        if (x < borderWidth || x >= width - borderWidth || y < borderWidth || y >= height - borderWidth)
                            tex.SetPixel(x, y, borderColor);
                        else
                            tex.SetPixel(x, y, fillColor);
                    }
                }
            }
            
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
        
        #endregion
        
        #region GUIStyles
        
        private static GUIStyle _windowHeader;
        public static GUIStyle WindowHeader
        {
            get
            {
                if (_windowHeader == null)
                {
                    _windowHeader = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 18,
                        alignment = TextAnchor.MiddleLeft,
                        padding = new RectOffset(10, 10, 8, 8),
                        margin = new RectOffset(0, 0, 0, 5)
                    };
                    _windowHeader.normal.textColor = TextPrimary;
                }
                return _windowHeader;
            }
        }
        
        private static GUIStyle _sectionHeader;
        public static GUIStyle SectionHeader
        {
            get
            {
                if (_sectionHeader == null)
                {
                    _sectionHeader = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 14,
                        alignment = TextAnchor.MiddleLeft,
                        padding = new RectOffset(5, 5, 8, 8),
                        margin = new RectOffset(0, 0, 10, 5)
                    };
                    _sectionHeader.normal.textColor = TextPrimary;
                }
                return _sectionHeader;
            }
        }
        
        private static GUIStyle _cardStyle;
        public static GUIStyle CardStyle
        {
            get
            {
                if (_cardStyle == null)
                {
                    _cardStyle = new GUIStyle()
                    {
                        padding = new RectOffset(12, 12, 10, 10),
                        margin = new RectOffset(5, 5, 5, 5),
                        normal = { background = GetSolidTexture(BackgroundMedium) }
                    };
                }
                return _cardStyle;
            }
        }
        
        private static GUIStyle _cardHeaderStyle;
        public static GUIStyle CardHeaderStyle
        {
            get
            {
                if (_cardHeaderStyle == null)
                {
                    _cardHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 13,
                        padding = new RectOffset(0, 0, 0, 5),
                        margin = new RectOffset(0, 0, 0, 5)
                    };
                    _cardHeaderStyle.normal.textColor = TextPrimary;
                }
                return _cardHeaderStyle;
            }
        }
        
        private static GUIStyle _primaryButton;
        public static GUIStyle PrimaryButton
        {
            get
            {
                if (_primaryButton == null)
                {
                    _primaryButton = new GUIStyle(GUI.skin.button)
                    {
                        fontSize = 12,
                        fontStyle = FontStyle.Bold,
                        padding = new RectOffset(15, 15, 8, 8),
                        margin = new RectOffset(5, 5, 5, 5),
                        alignment = TextAnchor.MiddleCenter
                    };
                    _primaryButton.normal.textColor = Color.white;
                    _primaryButton.normal.background = GetSolidTexture(PrimaryBlue);
                    _primaryButton.hover.background = GetSolidTexture(PrimaryBlue * 1.2f);
                    _primaryButton.active.background = GetSolidTexture(PrimaryBlue * 0.8f);
                }
                return _primaryButton;
            }
        }
        
        private static GUIStyle _secondaryButton;
        public static GUIStyle SecondaryButton
        {
            get
            {
                if (_secondaryButton == null)
                {
                    _secondaryButton = new GUIStyle(GUI.skin.button)
                    {
                        fontSize = 11,
                        padding = new RectOffset(12, 12, 6, 6),
                        margin = new RectOffset(3, 3, 3, 3),
                        alignment = TextAnchor.MiddleCenter
                    };
                    _secondaryButton.normal.textColor = TextPrimary;
                    _secondaryButton.normal.background = GetSolidTexture(BackgroundLight);
                    _secondaryButton.hover.background = GetSolidTexture(BackgroundLight * 1.3f);
                    _secondaryButton.active.background = GetSolidTexture(BackgroundLight * 0.7f);
                }
                return _secondaryButton;
            }
        }
        
        private static GUIStyle _successButton;
        public static GUIStyle SuccessButton
        {
            get
            {
                if (_successButton == null)
                {
                    _successButton = new GUIStyle(PrimaryButton);
                    _successButton.normal.background = GetSolidTexture(Success * 0.8f);
                    _successButton.hover.background = GetSolidTexture(Success);
                    _successButton.active.background = GetSolidTexture(Success * 0.6f);
                }
                return _successButton;
            }
        }
        
        private static GUIStyle _dangerButton;
        public static GUIStyle DangerButton
        {
            get
            {
                if (_dangerButton == null)
                {
                    _dangerButton = new GUIStyle(PrimaryButton);
                    _dangerButton.normal.background = GetSolidTexture(Error * 0.8f);
                    _dangerButton.hover.background = GetSolidTexture(Error);
                    _dangerButton.active.background = GetSolidTexture(Error * 0.6f);
                }
                return _dangerButton;
            }
        }
        
        private static GUIStyle _statusBadge;
        public static GUIStyle StatusBadge
        {
            get
            {
                if (_statusBadge == null)
                {
                    _statusBadge = new GUIStyle(EditorStyles.label)
                    {
                        fontSize = 10,
                        fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleCenter,
                        padding = new RectOffset(8, 8, 3, 3),
                        margin = new RectOffset(3, 3, 3, 3)
                    };
                }
                return _statusBadge;
            }
        }
        
        private static GUIStyle _toolbarStyle;
        public static GUIStyle ToolbarStyle
        {
            get
            {
                if (_toolbarStyle == null)
                {
                    _toolbarStyle = new GUIStyle()
                    {
                        padding = new RectOffset(8, 8, 6, 6),
                        margin = new RectOffset(0, 0, 0, 0),
                        normal = { background = GetSolidTexture(BackgroundDark) }
                    };
                }
                return _toolbarStyle;
            }
        }
        
        private static GUIStyle _infoBox;
        public static GUIStyle InfoBox
        {
            get
            {
                if (_infoBox == null)
                {
                    _infoBox = new GUIStyle(EditorStyles.helpBox)
                    {
                        fontSize = 11,
                        padding = new RectOffset(10, 10, 8, 8),
                        margin = new RectOffset(5, 5, 5, 5),
                        richText = true
                    };
                }
                return _infoBox;
            }
        }
        
        private static GUIStyle _centeredLabel;
        public static GUIStyle CenteredLabel
        {
            get
            {
                if (_centeredLabel == null)
                {
                    _centeredLabel = new GUIStyle(EditorStyles.label)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        wordWrap = true
                    };
                }
                return _centeredLabel;
            }
        }
        
        private static GUIStyle _richLabel;
        public static GUIStyle RichLabel
        {
            get
            {
                if (_richLabel == null)
                {
                    _richLabel = new GUIStyle(EditorStyles.label)
                    {
                        richText = true,
                        wordWrap = true
                    };
                }
                return _richLabel;
            }
        }
        
        private static GUIStyle _foldoutHeader;
        public static GUIStyle FoldoutHeader
        {
            get
            {
                if (_foldoutHeader == null)
                {
                    _foldoutHeader = new GUIStyle(EditorStyles.foldout)
                    {
                        fontSize = 12,
                        fontStyle = FontStyle.Bold,
                        padding = new RectOffset(15, 0, 3, 3)
                    };
                    _foldoutHeader.normal.textColor = TextPrimary;
                }
                return _foldoutHeader;
            }
        }
        
        // Consolas is Windows-only. Using it on macOS/Linux causes Unity to
        // spam "Unable to load font face" warnings every frame.
        public static string MonoFontName
        {
            get
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.OSXEditor: return "Menlo";
                    case RuntimePlatform.LinuxEditor: return "DejaVu Sans Mono";
                    default: return "Consolas";
                }
            }
        }

        #endregion

        #region Drawing Helpers
        
        /// <summary>
        /// Draw a horizontal separator line
        /// </summary>
        public static void DrawSeparator(float topMargin = 5, float bottomMargin = 5)
        {
            GUILayout.Space(topMargin);
            var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, TextMuted * 0.5f);
            GUILayout.Space(bottomMargin);
        }
        
        /// <summary>
        /// Draw a status badge with color
        /// </summary>
        public static void DrawStatusBadge(string text, Color color, float width = 80)
        {
            var style = new GUIStyle(StatusBadge);
            style.normal.background = GetSolidTexture(color);
            style.normal.textColor = GetContrastColor(color);
            GUILayout.Label(text, style, GUILayout.Width(width));
        }
        
        /// <summary>
        /// Draw a progress bar
        /// </summary>
        public static void DrawProgressBar(float progress, string label, Color fillColor, float height = 20)
        {
            var rect = GUILayoutUtility.GetRect(100, height, GUILayout.ExpandWidth(true));
            
            // Background
            EditorGUI.DrawRect(rect, BackgroundDark);
            
            // Fill
            var fillRect = new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(progress), rect.height);
            EditorGUI.DrawRect(fillRect, fillColor);
            
            // Border
            DrawBorder(rect, TextMuted * 0.5f, 1);
            
            // Label
            var labelStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = TextPrimary }
            };
            GUI.Label(rect, $"{label} ({progress * 100:F0}%)", labelStyle);
        }
        
        /// <summary>
        /// Draw a border around a rect
        /// </summary>
        public static void DrawBorder(Rect rect, Color color, int thickness = 1)
        {
            // Top
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            // Bottom
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            // Left
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            // Right
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }
        
        /// <summary>
        /// Get a contrasting text color for a background
        /// </summary>
        public static Color GetContrastColor(Color background)
        {
            float luminance = 0.299f * background.r + 0.587f * background.g + 0.114f * background.b;
            return luminance > 0.5f ? Color.black : Color.white;
        }
        
        /// <summary>
        /// Begin a styled card section
        /// </summary>
        public static void BeginCard()
        {
            EditorGUILayout.BeginVertical(CardStyle);
        }
        
        /// <summary>
        /// End a styled card section
        /// </summary>
        public static void EndCard()
        {
            EditorGUILayout.EndVertical();
        }
        
        /// <summary>
        /// Draw a card with header and content
        /// </summary>
        public static bool DrawFoldoutCard(string title, bool isExpanded, System.Action drawContent)
        {
            EditorGUILayout.BeginVertical(CardStyle);
            
            EditorGUILayout.BeginHorizontal();
            isExpanded = EditorGUILayout.Foldout(isExpanded, title, true, FoldoutHeader);
            EditorGUILayout.EndHorizontal();
            
            if (isExpanded)
            {
                EditorGUILayout.Space(5);
                drawContent?.Invoke();
            }
            
            EditorGUILayout.EndVertical();
            
            return isExpanded;
        }
        
        /// <summary>
        /// Draw a large icon button (for dashboard)
        /// </summary>
        public static bool DrawIconButton(string label, string icon, string description, float width = 150, float height = 100)
        {
            var rect = GUILayoutUtility.GetRect(width, height);
            
            bool isHover = rect.Contains(Event.current.mousePosition);
            var bgColor = isHover ? BackgroundHighlight : BackgroundMedium;
            
            EditorGUI.DrawRect(rect, bgColor);
            DrawBorder(rect, isHover ? AccentCyan : TextMuted * 0.3f, 1);
            
            // Icon (using Unity's built-in icons or a placeholder)
            var iconRect = new Rect(rect.x + rect.width / 2 - 16, rect.y + 15, 32, 32);
            var iconContent = EditorGUIUtility.IconContent(icon);   
            if (iconContent != null && iconContent.image != null)
            {
                GUI.DrawTexture(iconRect, iconContent.image);
            }
            
            // Label
            var labelRect = new Rect(rect.x, rect.y + 52, rect.width, 20);
            var labelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = TextPrimary }
            };
            GUI.Label(labelRect, label, labelStyle);
            
            // Description
            var descRect = new Rect(rect.x + 5, rect.y + 70, rect.width - 10, 25);
            var descStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                wordWrap = true,
                normal = { textColor = TextSecondary }
            };
            GUI.Label(descRect, description, descStyle);
            
            return GUI.Button(rect, GUIContent.none, GUIStyle.none);
        }
        
        /// <summary>
        /// Draw a horizontal button group
        /// </summary>
        public static int DrawButtonGroup(string[] labels, int selected, float height = 25)
        {
            EditorGUILayout.BeginHorizontal();
            
            for (int i = 0; i < labels.Length; i++)
            {
                bool isSelected = i == selected;
                var style = new GUIStyle(GUI.skin.button)
                {
                    fontStyle = isSelected ? FontStyle.Bold : FontStyle.Normal
                };
                
                if (isSelected)
                {
                    style.normal.background = GetSolidTexture(PrimaryBlue);
                    style.normal.textColor = Color.white;
                }
                else
                {
                    style.normal.background = GetSolidTexture(BackgroundLight);
                    style.normal.textColor = TextSecondary;
                }
                
                if (GUILayout.Button(labels[i], style, GUILayout.Height(height)))
                {
                    selected = i;
                }
            }
            
            EditorGUILayout.EndHorizontal();
            
            return selected;
        }
        
        #endregion
        
        #region Layout Helpers
        
        /// <summary>
        /// Begin a toolbar row
        /// </summary>
        public static void BeginToolbar()
        {
            EditorGUILayout.BeginHorizontal(ToolbarStyle);
        }
        
        /// <summary>
        /// End a toolbar row
        /// </summary>
        public static void EndToolbar()
        {
            EditorGUILayout.EndHorizontal();
        }
        
        /// <summary>
        /// Add flexible space
        /// </summary>
        public static void FlexibleSpace()
        {
            GUILayout.FlexibleSpace();
        }
        
        /// <summary>
        /// Begin a centered layout
        /// </summary>
        public static void BeginCentered()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginVertical();
        }
        
        /// <summary>
        /// End a centered layout
        /// </summary>
        public static void EndCentered()
        {
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }
        
        #endregion
        
        #region Cleanup
        
        /// <summary>
        /// Clear cached styles and textures. Call when recompiling.
        /// </summary>
        public static void ClearCache()
        {
            foreach (var tex in _textureCache.Values)
            {
                if (tex != null)
                    Object.DestroyImmediate(tex);
            }
            _textureCache.Clear();
            
            _windowHeader = null;
            _sectionHeader = null;
            _cardStyle = null;
            _cardHeaderStyle = null;
            _primaryButton = null;
            _secondaryButton = null;
            _successButton = null;
            _dangerButton = null;
            _statusBadge = null;
            _toolbarStyle = null;
            _infoBox = null;
            _centeredLabel = null;
            _richLabel = null;
            _foldoutHeader = null;
        }
        
        #endregion
    }
    
    /// <summary>
    /// Icons used throughout the PSX Splash editor
    /// </summary>
    public static class PSXIcons
    {
        // Unity built-in icons that work well for our purposes
        public const string Scene = "d_SceneAsset Icon";
        public const string Build = "d_BuildSettings.SelectedIcon";
        public const string Settings = "d_Settings";
        public const string Play = "d_PlayButton";
        public const string Refresh = "d_Refresh";
        public const string Warning = "d_console.warnicon";
        public const string Error = "d_console.erroricon";
        public const string Info = "d_console.infoicon";
        public const string Success = "d_Progress";
        public const string Texture = "d_Texture Icon";
        public const string Mesh = "d_Mesh Icon";
        public const string Script = "d_cs Script Icon";
        public const string Folder = "d_Folder Icon";
        public const string Download = "d_Download-Available";
        public const string Upload = "d_UpArrow";
        public const string Link = "d_Linked";
        public const string Unlink = "d_Unlinked";
        public const string Eye = "d_scenevis_visible_hover";
        public const string EyeOff = "d_scenevis_hidden_hover";
        public const string Add = "d_Toolbar Plus";
        public const string Remove = "d_Toolbar Minus";
        public const string Edit = "d_editicon.sml";
        public const string Search = "d_Search Icon";
        public const string Console = "d_UnityEditor.ConsoleWindow";
        public const string Help = "d__Help";
        public const string GameObject = "d_GameObject Icon";
        public const string Camera = "d_Camera Icon";
        public const string Light = "d_Light Icon";
        public const string Prefab = "d_Prefab Icon";
        
        /// <summary>
        /// Get a GUIContent with icon and tooltip
        /// </summary>
        public static GUIContent GetContent(string icon, string tooltip = "")
        {
            var content = EditorGUIUtility.IconContent(icon);
            if (content == null) content = new GUIContent();
            content.tooltip = tooltip;
            return content;
        }
        
        /// <summary>
        /// Get a GUIContent with icon, text and tooltip
        /// </summary>
        public static GUIContent GetContent(string icon, string text, string tooltip)
        {
            var content = EditorGUIUtility.IconContent(icon);
            if (content == null) content = new GUIContent();
            content.text = text;
            content.tooltip = tooltip;
            return content;
        }
    }
}
