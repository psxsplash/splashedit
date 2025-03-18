using System.Collections.Generic;
using System.IO;
using System.Linq;
using SplashEdit.RuntimeCode;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;


namespace SplashEdit.EditorCode
{
    public class VRAMEditorWindow : EditorWindow
    {
        private const int VramWidth = 1024;
        private const int VramHeight = 512;
        private static readonly Vector2 MinSize = new Vector2(800, 600);
        private List<ProhibitedArea> prohibitedAreas = new List<ProhibitedArea>();
        private Vector2 scrollPosition;
        private Texture2D vramImage;
        private Vector2 selectedResolution = new Vector2(320, 240);
        private bool dualBuffering = true;
        private bool verticalLayout = true;
        private Color bufferColor1 = new Color(1, 0, 0, 0.5f);
        private Color bufferColor2 = new Color(0, 1, 0, 0.5f);
        private Color prohibitedColor = new Color(1, 0, 0, 0.3f);
        private PSXData _psxData;

        private static readonly Vector2[] resolutions =
        {
        new Vector2(256, 240), new Vector2(256, 480),
        new Vector2(320, 240), new Vector2(320, 480),
        new Vector2(368, 240), new Vector2(368, 480),
        new Vector2(512, 240), new Vector2(512, 480),
        new Vector2(640, 240), new Vector2(640, 480)
        };
        private static string[] resolutionsStrings => resolutions.Select(c => $"{c.x}x{c.y}").ToArray();

        [MenuItem("Window/VRAM Editor")]
        public static void ShowWindow()
        {
            VRAMEditorWindow window = GetWindow<VRAMEditorWindow>("VRAM Editor");
            // Set minimum window dimensions.
            window.minSize = MinSize;
        }

        private void OnEnable()
        {
            // Initialize VRAM texture with black pixels.
            vramImage = new Texture2D(VramWidth, VramHeight);
            NativeArray<Color32> blackPixels = new NativeArray<Color32>(VramWidth * VramHeight, Allocator.Temp);
            vramImage.SetPixelData(blackPixels, 0);
            vramImage.Apply();
            blackPixels.Dispose();

            // Ensure minimum window size is applied.
            this.minSize = MinSize;

            _psxData = Utils.LoadData(out selectedResolution, out dualBuffering, out verticalLayout, out prohibitedAreas);
        }

        /// <summary>
        /// Pastes an overlay texture onto a base texture at the specified position.
        /// </summary>
        public static void PasteTexture(Texture2D baseTexture, Texture2D overlayTexture, int posX, int posY)
        {
            if (baseTexture == null || overlayTexture == null)
            {
                Debug.LogError("Textures cannot be null!");
                return;
            }

            Color[] overlayPixels = overlayTexture.GetPixels();
            Color[] basePixels = baseTexture.GetPixels();

            int baseWidth = baseTexture.width;
            int baseHeight = baseTexture.height;
            int overlayWidth = overlayTexture.width;
            int overlayHeight = overlayTexture.height;

            // Copy each overlay pixel into the base texture if within bounds.
            for (int y = 0; y < overlayHeight; y++)
            {
                for (int x = 0; x < overlayWidth; x++)
                {
                    int baseX = posX + x;
                    int baseY = posY + y;
                    if (baseX >= 0 && baseX < baseWidth && baseY >= 0 && baseY < baseHeight)
                    {
                        int baseIndex = baseY * baseWidth + baseX;
                        int overlayIndex = y * overlayWidth + x;
                        basePixels[baseIndex] = overlayPixels[overlayIndex];
                    }
                }
            }

            baseTexture.SetPixels(basePixels);
            baseTexture.Apply();
        }

        /// <summary>
        /// Packs PSX textures into VRAM, rebuilds the VRAM texture and writes binary data to an output file.
        /// </summary>
        private void PackTextures()
        {
            // Reinitialize VRAM texture with black pixels.
            vramImage = new Texture2D(VramWidth, VramHeight);
            NativeArray<Color32> blackPixels = new NativeArray<Color32>(VramWidth * VramHeight, Allocator.Temp);
            vramImage.SetPixelData(blackPixels, 0);
            vramImage.Apply();
            blackPixels.Dispose();

            // Retrieve all PSXObjectExporter objects and create their PSX textures.
            PSXObjectExporter[] objects = FindObjectsByType<PSXObjectExporter>(FindObjectsSortMode.None);
            foreach (PSXObjectExporter exp in objects)
            {
                exp.CreatePSXTexture2D();
            }

            // Define framebuffer regions based on selected resolution and layout.
            (Rect buffer1, Rect buffer2) = Utils.BufferForResolution(selectedResolution, verticalLayout);

            List<Rect> framebuffers = new List<Rect> { buffer1 };
            if (dualBuffering)
            {
                framebuffers.Add(buffer2);
            }

            // Pack textures into VRAM using the VRAMPacker.
            VRAMPacker tp = new VRAMPacker(framebuffers, prohibitedAreas);
            var packed = tp.PackTexturesIntoVRAM(objects);

            // Copy packed VRAM pixel data into the texture.
            for (int y = 0; y < VramHeight; y++)
            {
                for (int x = 0; x < VramWidth; x++)
                {
                    vramImage.SetPixel(x, VramHeight - y - 1, packed._vramPixels[x, y].GetUnityColor());
                }
            }
            vramImage.Apply();

            // Prompt the user to select a file location and save the VRAM data.
            string path = EditorUtility.SaveFilePanel("Select Output File", "", "output", "bin");
            using (BinaryWriter writer = new BinaryWriter(File.Open(path, FileMode.Create)))
            {
                for (int y = 0; y < VramHeight; y++)
                {
                    for (int x = 0; x < VramWidth; x++)
                    {
                        writer.Write(packed._vramPixels[x, y].Pack());
                    }
                }
            }
        }

        private void OnGUI()
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label("VRAM Editor", EditorStyles.boldLabel);

            // Dropdown for resolution selection.
            selectedResolution = resolutions[EditorGUILayout.Popup("Resolution", System.Array.IndexOf(resolutions, selectedResolution), resolutionsStrings)];

            // Check resolution constraints for dual buffering.
            bool canDBHorizontal = selectedResolution.x * 2 <= VramWidth;
            bool canDBVertical = selectedResolution.y * 2 <= VramHeight;

            if (canDBHorizontal || canDBVertical)
            {
                dualBuffering = EditorGUILayout.Toggle("Dual Buffering", dualBuffering);
            }
            else
            {
                dualBuffering = false;
            }

            if (canDBVertical && canDBHorizontal)
            {
                verticalLayout = EditorGUILayout.Toggle("Vertical", verticalLayout);
            }
            else if (canDBVertical)
            {
                verticalLayout = true;
            }
            else
            {
                verticalLayout = false;
            }

            GUILayout.Space(10);
            GUILayout.Label("Prohibited areas", EditorStyles.boldLabel);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.MaxHeight(150f));

            // List and edit each prohibited area.
            for (int i = 0; i < prohibitedAreas.Count; i++)
            {
                var area = prohibitedAreas[i];

                area.X = EditorGUILayout.IntField("X", area.X);
                area.Y = EditorGUILayout.IntField("Y", area.Y);
                area.Width = EditorGUILayout.IntField("Width", area.Width);
                area.Height = EditorGUILayout.IntField("Height", area.Height);

                if (GUILayout.Button("Remove"))
                {
                    prohibitedAreas.RemoveAt(i);
                    break;
                }

                prohibitedAreas[i] = area;
                GUILayout.Space(10);
            }
            GUILayout.EndScrollView();
            GUILayout.Space(10);
            if (GUILayout.Button("Add Prohibited Area"))
            {
                prohibitedAreas.Add(new ProhibitedArea());
            }

            // Button to initiate texture packing.
            if (GUILayout.Button("Pack Textures"))
            {
                PackTextures();
            }

            // Button to save settings; saving now occurs only on button press.
            if (GUILayout.Button("Save Settings"))
            {
                StoreData();
            }

            GUILayout.EndVertical();

            // Display VRAM image preview.
            Rect vramRect = GUILayoutUtility.GetRect(VramWidth, VramHeight, GUILayout.ExpandWidth(false));
            EditorGUI.DrawPreviewTexture(vramRect, vramImage, null, ScaleMode.ScaleToFit, 0, 0, ColorWriteMask.All);

            // Draw framebuffer overlays.
            (Rect buffer1, Rect buffer2) = Utils.BufferForResolution(selectedResolution, verticalLayout, vramRect.min);

            EditorGUI.DrawRect(buffer1, bufferColor1);
            GUI.Label(new Rect(buffer1.center.x - 40, buffer1.center.y - 10, 120, 20), "Framebuffer A", EditorStyles.boldLabel);
            GUILayout.Space(10);
            if (dualBuffering)
            {
                EditorGUI.DrawRect(buffer2, bufferColor2);
                GUI.Label(new Rect(buffer2.center.x - 40, buffer2.center.y - 10, 120, 20), "Framebuffer B", EditorStyles.boldLabel);
            }

            // Draw overlays for each prohibited area.
            foreach (ProhibitedArea area in prohibitedAreas)
            {
                Rect areaRect = new Rect(vramRect.x + area.X, vramRect.y + area.Y, area.Width, area.Height);
                EditorGUI.DrawRect(areaRect, prohibitedColor);
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Stores current configuration to the PSX data asset.
        /// This is now triggered manually via the "Save Settings" button.
        /// </summary>
        private void StoreData()
        {
            if (_psxData != null)
            {
                _psxData.OutputResolution = selectedResolution;
                _psxData.DualBuffering = dualBuffering;
                _psxData.VerticalBuffering = verticalLayout;
                _psxData.ProhibitedAreas = prohibitedAreas;

                EditorUtility.SetDirty(_psxData);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }
    }
}