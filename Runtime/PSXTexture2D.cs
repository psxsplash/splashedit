using System;
using System.Collections.Generic;
using UnityEngine;


namespace SplashEdit.RuntimeCode
{

    /// <summary>
    /// Represents the bit depth of a PSX texture.
    /// </summary>
    public enum PSXBPP
    {
        TEX_4BIT = 4,
        TEX_8BIT = 8,
        TEX_16BIT = 15
    }

    /// <summary>
    /// Represents a pixel in VRAM with RGB components and a semi-transparency flag.
    /// </summary>
    public struct VRAMPixel
    {
        private ushort r; // 0-4 bits
        private ushort g; // 5-9 bits
        private ushort b; // 10-14 bits

        /// <summary>
        /// Red component (0-4 bits).
        /// </summary>
        public ushort R
        {
            get => r;
            set => r = (ushort)(value & 0b11111);
        }

        /// <summary>
        /// Green component (0-4 bits).
        /// </summary>
        public ushort G
        {
            get => g;
            set => g = (ushort)(value & 0b11111);
        }

        /// <summary>
        /// Blue component (0-4 bits).
        /// </summary>
        public ushort B
        {
            get => b;
            set => b = (ushort)(value & 0b11111);
        }

        /// <summary>
        /// Gets or sets a value indicating whether the pixel is semi-transparent (15th bit).
        /// </summary>
        public bool SemiTransparent { get; set; } // 15th bit


        /// <summary>
        /// Packs the RGB components and semi-transparency flag into a single ushort value.
        /// </summary>
        /// <returns>The packed ushort value.</returns>
        public ushort Pack()
        {
            return (ushort)((SemiTransparent ? 1 << 15 : 0) | (b << 10) | (g << 5) | r);
        }

        /// <summary>
        /// Unpacks the RGB components and semi-transparency flag from a packed ushort value.
        /// </summary>
        /// <param name="packedValue">The packed ushort value.</param>
        public void Unpack(ushort packedValue)
        {
            SemiTransparent = (packedValue & (1 << 15)) != 0;
            b = (ushort)((packedValue >> 10) & 0b11111);
            g = (ushort)((packedValue >> 5) & 0b11111);
            r = (ushort)(packedValue & 0b11111);
        }


        public Color GetUnityColor()
        {
            return new Color(R / 31.0f, G / 31.0f, B / 31.0f);
        }
    }

    /// <summary>
    /// Represents a PSX texture with various bit depths and provides methods to create and manipulate the texture.
    /// </summary>
    public class PSXTexture2D
    {
        public int Width { get; set; }
        public int QuantizedWidth { get; set; }
        public int Height { get; set; }
        public int[,] PixelIndices { get; set; }
        public List<VRAMPixel> ColorPalette = new List<VRAMPixel>();
        public PSXBPP BitDepth { get; set; }


        public Texture2D OriginalTexture;

        // Within supertexture
        public byte PackingX;
        public byte PackingY;

        public byte TexpageX;
        public byte TexpageY;

        // Absolute positioning
        public ushort ClutPackingX;
        public ushort ClutPackingY;

        private int _maxColors;

        public VRAMPixel[,] ImageData { get; set; }

        /// <summary>
        /// Creates a PSX texture from a given Texture2D with the specified bit depth.
        /// </summary>
        /// <param name="inputTexture">The input Texture2D.</param>
        /// <param name="bitDepth">The desired bit depth for the PSX texture.</param>
        /// <returns>The created PSXTexture2D.</returns>
        public static PSXTexture2D CreateFromTexture2D(Texture2D inputTexture, PSXBPP bitDepth)
        {
            PSXTexture2D psxTex = new PSXTexture2D();

            psxTex.Width = inputTexture.width;
            psxTex.QuantizedWidth = bitDepth == PSXBPP.TEX_4BIT ? inputTexture.width / 4 :
                        bitDepth == PSXBPP.TEX_8BIT ? inputTexture.width / 2 :
                        inputTexture.width;
            psxTex.Height = inputTexture.height;

            psxTex.BitDepth = bitDepth;


            if (bitDepth == PSXBPP.TEX_16BIT)
            {
                psxTex.ImageData = new VRAMPixel[inputTexture.width, inputTexture.height];

                int width = inputTexture.width;
                int height = inputTexture.height;

                for (int y = 0; y < height; y++) // Start from top row, move downward
                {
                    for (int x = 0; x < width; x++) // Start from right column, move leftward
                    {
                        Color pixel = inputTexture.GetPixel(x, height - y - 1);
                        VRAMPixel vramPixel = new VRAMPixel
                        {
                            R = (ushort)(pixel.r * 31),
                            G = (ushort)(pixel.g * 31),
                            B = (ushort)(pixel.b * 31)
                        };
                        psxTex.ImageData[x, y] = vramPixel;
                    }
                }
                psxTex.ColorPalette = null;
                return psxTex;
            }

            psxTex._maxColors = (int)Mathf.Pow((int)bitDepth, 2);

            TextureQuantizer.QuantizedResult result = TextureQuantizer.Quantize(inputTexture, psxTex._maxColors);

            foreach (Vector3 color in result.Palette)
            {
                Color pixel = new Color(color.x, color.y, color.z);
                VRAMPixel vramPixel = new VRAMPixel { R = (ushort)(pixel.r * 31), G = (ushort)(pixel.g * 31), B = (ushort)(pixel.b * 31) };
                psxTex.ColorPalette.Add(vramPixel);
            }


            psxTex.ImageData = new VRAMPixel[psxTex.QuantizedWidth, psxTex.Height];

            psxTex.PixelIndices = result.Indices;

            int groupSize = (bitDepth == PSXBPP.TEX_8BIT) ? 2 : 4;

            for (int y = 0; y < psxTex.Height; y++)
            {
                if (bitDepth == PSXBPP.TEX_8BIT)
                {
                    for (int group = 0; group < psxTex.QuantizedWidth; group++)
                    {
                        int baseIndex = group * 2;
                        // Combine two 8-bit indices into one ushort.
                        int index1 = psxTex.PixelIndices[baseIndex, y] & 0xFF;
                        int index2 = psxTex.PixelIndices[baseIndex + 1, y] & 0xFF;
                        ushort packed = (ushort)((index2 << 8) | index1);
                        VRAMPixel pixel = new VRAMPixel();
                        pixel.Unpack(packed);
                        psxTex.ImageData[group, psxTex.Height - y - 1] = pixel;
                    }
                }
                else if (bitDepth == PSXBPP.TEX_4BIT)
                {
                    for (int group = 0; group < psxTex.QuantizedWidth; group++)
                    {
                        int baseIndex = group * 4;
                        // Combine four 4-bit indices into one ushort.
                        int idx1 = psxTex.PixelIndices[baseIndex, y] & 0xF;
                        int idx2 = psxTex.PixelIndices[baseIndex + 1, y] & 0xF;
                        int idx3 = psxTex.PixelIndices[baseIndex + 2, y] & 0xF;
                        int idx4 = psxTex.PixelIndices[baseIndex + 3, y] & 0xF;
                        ushort packed = (ushort)((idx4 << 12) | (idx3 << 8) | (idx2 << 4) | idx1);
                        VRAMPixel pixel = new VRAMPixel();
                        pixel.Unpack(packed);
                        psxTex.ImageData[group, psxTex.Height - y - 1] = pixel;
                    }
                }
            }


            return psxTex;
        }


        /// <summary>
        /// Generates a preview Texture2D from the PSX texture.
        /// </summary>
        /// <returns>The generated preview Texture2D.</returns>
        public Texture2D GeneratePreview()
        {
            Texture2D tex = new Texture2D(Width, Height);
            if (BitDepth == PSXBPP.TEX_16BIT)
            {

                for (int y = 0; y < Width; y++)
                {
                    for (int x = 0; x < Height; x++)
                    {
                        tex.SetPixel(x, y, ImageData[x, y].GetUnityColor());
                    }
                }

                tex.Apply();
                return tex;
            }

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    tex.SetPixel(x, y, ColorPalette[PixelIndices[x, y]].GetUnityColor());
                }
            }
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Generates a VRAM preview Texture2D from the PSX texture.
        /// </summary>
        /// <returns>The generated VRAM preview Texture2D.</returns>
        public Texture2D GenerateVramPreview()
        {

            if (BitDepth == PSXBPP.TEX_16BIT)
            {
                return GeneratePreview();
            }

            Texture2D vramTexture = new Texture2D(QuantizedWidth, Height);

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < QuantizedWidth; x++)
                {
                    vramTexture.SetPixel(x, y, ImageData[x, y].GetUnityColor());
                }
            }
            vramTexture.Apply();

            return vramTexture;

        }
        /// <summary>
        /// Check if we need to update stored texture
        /// </summary>
        /// <param name="bitDepth">new settings for color bit depth</param>
        /// <param name="texture">new texture</param>
        /// <returns>return true if sored texture is different from a new one</returns>
        internal bool NeedUpdate(PSXBPP bitDepth, Texture2D texture)
        {
            return BitDepth != bitDepth || texture.GetInstanceID() != texture.GetInstanceID();
        }
    }
}