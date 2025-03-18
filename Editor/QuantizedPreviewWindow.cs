using System.Collections.Generic;
using System.IO;
using SplashEdit.RuntimeCode;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SplashEdit.EditorCode
{
    public class QuantizedPreviewWindow : EditorWindow
    {
        private Texture2D originalTexture;
        private Texture2D quantizedTexture;
        private Texture2D vramTexture; // VRAM representation of the texture
        private List<VRAMPixel> clut; // Color Lookup Table (CLUT), stored as a 1D list
        private ushort[] indexedPixelData; // Indexed pixel data for VRAM storage
        private PSXBPP bpp = PSXBPP.TEX_4BIT;
        private readonly int previewSize = 256;

        [MenuItem("Window/Quantized Preview")]
        public static void ShowWindow()
        {
            // Creates and displays the window
            QuantizedPreviewWindow win = GetWindow<QuantizedPreviewWindow>("Quantized Preview");
            win.minSize = new Vector2(800, 700);
        }

        private void OnGUI()
        {
            GUILayout.Label("Quantized Preview", EditorStyles.boldLabel);

            // Texture input field
            originalTexture = (Texture2D)EditorGUILayout.ObjectField("Original Texture", originalTexture, typeof(Texture2D), false);

            // Dropdown for bit depth selection
            bpp = (PSXBPP)EditorGUILayout.EnumPopup("Bit Depth", bpp);

            // Button to generate the quantized preview
            if (GUILayout.Button("Generate Quantized Preview") && originalTexture != null)
            {
                GenerateQuantizedPreview();
            }

            GUILayout.BeginHorizontal();

            // Display the original texture
            if (originalTexture != null)
            {
                GUILayout.BeginVertical();
                GUILayout.Label("Original Texture");
                DrawTexturePreview(originalTexture, previewSize, false);
                GUILayout.EndVertical();
            }

            // Display the VRAM view of the texture
            if (vramTexture != null)
            {
                GUILayout.BeginVertical();
                GUILayout.Label("VRAM View (Indexed Data as 16bpp)");
                DrawTexturePreview(vramTexture, previewSize);
                GUILayout.EndVertical();
            }

            // Display the quantized texture
            if (quantizedTexture != null)
            {
                GUILayout.BeginVertical();
                GUILayout.Label("Quantized Texture");
                DrawTexturePreview(quantizedTexture, previewSize);
                GUILayout.EndVertical();
            }

            GUILayout.EndHorizontal();

            // Display the Color Lookup Table (CLUT)
            if (clut != null)
            {
                GUILayout.Label("Color Lookup Table (CLUT)");
                DrawCLUT();
            }

            GUILayout.Space(10);

            // Export indexed pixel data
            if (indexedPixelData != null)
            {
                if (GUILayout.Button("Export texture data"))
                {
                    string path = EditorUtility.SaveFilePanel("Save texture data", "", "pixel_data", "bin");

                    if (!string.IsNullOrEmpty(path))
                    {
                        using (FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
                        using (BinaryWriter writer = new BinaryWriter(fileStream))
                        {
                            foreach (ushort value in indexedPixelData)
                            {
                                writer.Write(value);
                            }
                        }
                    }
                }
            }

            // Export CLUT data
            if (clut != null)
            {
                if (GUILayout.Button("Export CLUT data"))
                {
                    string path = EditorUtility.SaveFilePanel("Save CLUT data", "", "clut_data", "bin");

                    if (!string.IsNullOrEmpty(path))
                    {
                        using (FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
                        using (BinaryWriter writer = new BinaryWriter(fileStream))
                        {
                            foreach (VRAMPixel value in clut)
                            {
                                writer.Write(value.Pack()); // Convert VRAMPixel data into a binary format
                            }
                        }
                    }
                }
            }
        }

        private void GenerateQuantizedPreview()
        {
            // Converts the texture using PSXTexture2D and stores the processed data
            PSXTexture2D psxTex = PSXTexture2D.CreateFromTexture2D(originalTexture, bpp);

            // Generate the quantized texture preview
            quantizedTexture = psxTex.GeneratePreview();

            // Generate the VRAM representation of the texture
            vramTexture = psxTex.GenerateVramPreview();

            // Store the Color Lookup Table (CLUT)
            clut = psxTex.ColorPalette;
        }

        private void DrawTexturePreview(Texture2D texture, int size, bool flipY = true)
        {
            // Renders a texture preview within the editor window
            Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
            EditorGUI.DrawPreviewTexture(rect, texture, null, ScaleMode.ScaleToFit, 0, 0, ColorWriteMask.All);
        }

        private void DrawCLUT()
        {
            if (clut == null) return;

            int swatchSize = 20;
            int maxColorsPerRow = 40; // Number of colors displayed per row

            GUILayout.Space(10);

            int totalColors = clut.Count;
            int totalRows = Mathf.CeilToInt((float)totalColors / maxColorsPerRow);

            for (int row = 0; row < totalRows; row++)
            {
                GUILayout.BeginHorizontal();

                int colorsInRow = Mathf.Min(maxColorsPerRow, totalColors - row * maxColorsPerRow);

                for (int col = 0; col < colorsInRow; col++)
                {
                    int index = row * maxColorsPerRow + col;

                    // Convert the CLUT colors from 5-bit to float values (0-1 range)
                    Vector3 color = new Vector3(
                        clut[index].R / 31.0f,  // Red: bits 0–4
                        clut[index].G / 31.0f,  // Green: bits 5–9
                        clut[index].B / 31.0f   // Blue: bits 10–14
                    );

                    // Create a small color preview box for each color in the CLUT
                    Rect rect = GUILayoutUtility.GetRect(swatchSize, swatchSize, GUILayout.ExpandWidth(false));
                    EditorGUI.DrawRect(rect, new Color(color.x, color.y, color.z));
                }

                GUILayout.EndHorizontal();
            }
        }
    }
}