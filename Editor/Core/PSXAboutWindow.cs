using System;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PMPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace SplashEdit.EditorCode
{
    public class PSXAboutWindow : EditorWindow
    {
        private const string WindowTitle = "About SplashEdit";
        private const string DiscordUrl = "https://discord.com/invite/Pp6fenHkxH";
        private const string DocumentationUrl = "https://psxsplash.github.io/docs/latest/";
        private const string WebsiteUrl = "https://psxsplash.github.io/";
        private const string JoinTierUrl = "https://www.youtube.com/channel/UCzp1RaZ3HmejKl723qlKOAw/join";

        private static readonly string[] Supporters =
        {
            "Schizoidpropagandanetwork",
            "Mati Valdez Marzari",
            "MiniStumpy"
        };

        [Serializable]
        private class PackageInfoDto
        {
            public string version;
        }

        private Vector2 _scrollPos;
        private Texture2D _logo;
        private string _version = "Unknown";
        private GUIStyle _supporterNameStyle;

        public static void ShowWindow()
        {
            var window = GetWindow<PSXAboutWindow>();
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(520, 520);
            window.Show();
        }

        private void OnEnable()
        {
            _version = ReadPackageVersion();
            _logo = LoadLogoTexture();
        }

        private void OnGUI()
        {
            DrawHeader();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            DrawOverviewCard();
            DrawLinksCard();
            DrawSupportersCard();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(PSXEditorStyles.ToolbarStyle);
            GUILayout.Label(WindowTitle, PSXEditorStyles.WindowHeader);
            GUILayout.FlexibleSpace();
            PSXEditorStyles.DrawStatusBadge($"v{_version}", PSXEditorStyles.Info, 90);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawOverviewCard()
        {
            EditorGUILayout.BeginVertical(PSXEditorStyles.CardStyle);

            EditorGUILayout.BeginHorizontal();
            if (_logo != null)
            {
                const float logoHeight = 48f;
                float aspect = (float)_logo.width / Mathf.Max(1, _logo.height);
                float logoWidth = logoHeight * aspect;
                GUILayout.Label(_logo, GUILayout.Width(logoWidth), GUILayout.Height(logoHeight));
                GUILayout.Space(8);
            }

            EditorGUILayout.BeginVertical();
            GUILayout.Label("SplashEdit", PSXEditorStyles.SectionHeader);
            GUILayout.Label("By Bandwidth and contributors", PSXEditorStyles.RichLabel);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            GUILayout.Label("Licensed under MIT", PSXEditorStyles.RichLabel);
            GUILayout.Label($"Package Version: {_version}", PSXEditorStyles.RichLabel);

            EditorGUILayout.EndVertical();
        }

        private void DrawLinksCard()
        {
            EditorGUILayout.BeginVertical(PSXEditorStyles.CardStyle);
            GUILayout.Label("Community and Resources", PSXEditorStyles.SectionHeader);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Discord", PSXEditorStyles.SecondaryButton, GUILayout.Height(28)))
            {
                Application.OpenURL(DiscordUrl);
            }
            if (GUILayout.Button("Documentation", PSXEditorStyles.SecondaryButton, GUILayout.Height(28)))
            {
                Application.OpenURL(DocumentationUrl);
            }
            if (GUILayout.Button("Website", PSXEditorStyles.SecondaryButton, GUILayout.Height(28)))
            {
                Application.OpenURL(WebsiteUrl);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawSupportersCard()
        {
            EditorGUILayout.BeginVertical(PSXEditorStyles.CardStyle);
            GUILayout.Label("Supporters", PSXEditorStyles.SectionHeader);

            float contentWidth = Mathf.Max(320f, position.width - 40f);
            float columnSpacing = 8f;
            int columns = Mathf.Clamp(Mathf.FloorToInt(contentWidth / 170f), 2, 4);
            float cellWidth = (contentWidth - ((columns - 1) * columnSpacing)) / columns;
            int rows = Mathf.CeilToInt((float)Supporters.Length / columns);

            for (int row = 0; row < rows; row++)
            {
                EditorGUILayout.BeginHorizontal();
                for (int column = 0; column < columns; column++)
                {
                    int index = row * columns + column;
                    if (index < Supporters.Length)
                    {
                        GUILayout.Label(new GUIContent(Supporters[index], Supporters[index]), GetSupporterNameStyle(), GUILayout.Width(cellWidth));
                    }
                    else
                    {
                        GUILayout.Space(cellWidth);
                    }

                    if (column < columns - 1)
                    {
                        GUILayout.Space(columnSpacing);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(8);
            GUILayout.Label("If you wanna be part of this list become a member of \"The PlayStation\" tier.", PSXEditorStyles.RichLabel);
            if (GUILayout.Button("Join The PlayStation Tier", PSXEditorStyles.SecondaryButton, GUILayout.Height(28)))
            {
                Application.OpenURL(JoinTierUrl);
            }

            EditorGUILayout.EndVertical();
        }

        private GUIStyle GetSupporterNameStyle()
        {
            if (_supporterNameStyle == null)
            {
                _supporterNameStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = false,
                    clipping = TextClipping.Clip,
                    alignment = TextAnchor.MiddleLeft,
                    richText = false
                };
                _supporterNameStyle.normal.textColor = PSXEditorStyles.TextSecondary;
            }

            return _supporterNameStyle;
        }

        private static string ReadPackageVersion()
        {
            try
            {
                var package = PMPackageInfo.FindForAssembly(typeof(PSXAboutWindow).Assembly);
                if (package != null && !string.IsNullOrEmpty(package.resolvedPath))
                {
                    string packageJsonPath = Path.Combine(package.resolvedPath, "package.json");
                    if (File.Exists(packageJsonPath))
                    {
                        string json = File.ReadAllText(packageJsonPath);
                        var dto = JsonUtility.FromJson<PackageInfoDto>(json);
                        if (!string.IsNullOrEmpty(dto?.version))
                        {
                            return dto.version;
                        }
                    }
                }

                string fallbackPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "package.json"));
                if (File.Exists(fallbackPath))
                {
                    string json = File.ReadAllText(fallbackPath);
                    var dto = JsonUtility.FromJson<PackageInfoDto>(json);
                    if (!string.IsNullOrEmpty(dto?.version))
                    {
                        return dto.version;
                    }
                }
            }
            catch
            {
                // Keep unknown version if package metadata cannot be read.
            }

            return "Unknown";
        }

        private static Texture2D LoadLogoTexture()
        {
            var package = PMPackageInfo.FindForAssembly(typeof(PSXAboutWindow).Assembly);
            if (package != null && !string.IsNullOrEmpty(package.assetPath))
            {
                string packageLogoPath = package.assetPath + "/Icons/Logo.png";
                var packageLogo = AssetDatabase.LoadAssetAtPath<Texture2D>(packageLogoPath);
                if (packageLogo != null)
                {
                    return packageLogo;
                }
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Icons/Logo.png");
        }
    }
}