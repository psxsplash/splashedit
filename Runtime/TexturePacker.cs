using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SplashEdit.RuntimeCode
{
    /// <summary>
    /// Represents a texture atlas that groups PSX textures by bit depth.
    /// Each atlas has a fixed height and a configurable width based on texture bit depth.
    /// </summary>
    public class TextureAtlas
    {
        public PSXBPP BitDepth;             // Bit depth of textures in this atlas.
        public int PositionX;               // X position of the atlas in VRAM.
        public int PositionY;               // Y position of the atlas in VRAM.
        public int Width;                   // Width of the atlas.
        public const int Height = 256;      // Fixed height for all atlases.
        public List<PSXTexture2D> ContainedTextures = new List<PSXTexture2D>(); // Textures packed in this atlas.
    }

    /// <summary>
    /// Packs PSX textures into a simulated VRAM.
    /// It manages texture atlases, placement of textures, and allocation of color lookup tables (CLUTs).
    /// </summary>
    public class VRAMPacker
    {
        private List<TextureAtlas> _textureAtlases = new List<TextureAtlas>();
        private List<Rect> _reservedAreas;             // Areas in VRAM where no textures can be placed.
        private List<TextureAtlas> _finalizedAtlases = new List<TextureAtlas>(); // Atlases that have been successfully placed.
        private List<Rect> _allocatedCLUTs = new List<Rect>();                     // Allocated regions for CLUTs.

        private const int VRAM_WIDTH = 1024;
        private const int VRAM_HEIGHT = 512;

        private VRAMPixel[,] _vramPixels;              // Simulated VRAM pixel data.

        /// <summary>
        /// Initializes the VRAMPacker with reserved areas from prohibited regions and framebuffers.
        /// </summary>
        /// <param name="framebuffers">Framebuffers to reserve in VRAM.</param>
        /// <param name="reservedAreas">Additional prohibited areas as ProhibitedArea instances.</param>
        public VRAMPacker(List<Rect> framebuffers, List<ProhibitedArea> reservedAreas)
        {
            // Convert ProhibitedArea instances to Unity Rects.
            List<Rect> areasConvertedToRect = new List<Rect>();
            foreach (ProhibitedArea area in reservedAreas)
            {
                areasConvertedToRect.Add(new Rect(area.X, area.Y, area.Width, area.Height));
            }
            _reservedAreas = areasConvertedToRect;

            // Reserve the two framebuffers.
            _reservedAreas.Add(framebuffers[0]);
            _reservedAreas.Add(framebuffers[1]);

            _vramPixels = new VRAMPixel[VRAM_WIDTH, VRAM_HEIGHT];
        }

        /// <summary>
        /// Packs the textures from the provided PSXObjectExporter array into VRAM.
        /// Returns the processed objects and the final VRAM pixel array.
        /// </summary>
        /// <param name="objects">Array of PSXObjectExporter objects to process.</param>
        /// <returns>Tuple containing processed objects and the VRAM pixel array.</returns>
        public (PSXObjectExporter[] processedObjects, VRAMPixel[,] _vramPixels) PackTexturesIntoVRAM(PSXObjectExporter[] objects)
        {
            List<PSXTexture2D> uniqueTextures = new List<PSXTexture2D>();
            // Group objects by texture bit depth (high to low).
            var groupedObjects = objects.GroupBy(obj => obj.Texture.BitDepth).OrderByDescending(g => g.Key);

            foreach (var group in groupedObjects)
            {
                // Determine atlas width based on texture bit depth.
                int atlasWidth = group.Key switch
                {
                    PSXBPP.TEX_16BIT => 256,
                    PSXBPP.TEX_8BIT => 128,
                    PSXBPP.TEX_4BIT => 64,
                    _ => 256
                };

                // Create a new atlas for this group.
                TextureAtlas atlas = new TextureAtlas { BitDepth = group.Key, Width = atlasWidth, PositionX = 0, PositionY = 0 };
                _textureAtlases.Add(atlas);

                // Process each texture in descending order of area (width * height).
                foreach (var obj in group.OrderByDescending(obj => obj.Texture.QuantizedWidth * obj.Texture.Height))
                {
                    // Remove duplicate textures
                    if (uniqueTextures.Where(tex => tex.OriginalTexture != null).Any(tex => tex.OriginalTexture.GetInstanceID() == obj.Texture.OriginalTexture.GetInstanceID() && tex.BitDepth == obj.Texture.BitDepth))
                    {
                        obj.Texture = uniqueTextures.First(tex => tex.OriginalTexture.GetInstanceID() == obj.Texture.OriginalTexture.GetInstanceID());
                        continue;
                    }

                    // Try to place the texture in the current atlas.
                    if (!TryPlaceTextureInAtlas(atlas, obj.Texture))
                    {
                        // If failed, create a new atlas and try again.
                        atlas = new TextureAtlas { BitDepth = group.Key, Width = atlasWidth, PositionX = 0, PositionY = 0 };
                        _textureAtlases.Add(atlas);
                        if (!TryPlaceTextureInAtlas(atlas, obj.Texture))
                        {
                            Debug.LogError($"Failed to pack texture {obj.Texture}. It might not fit.");
                            break;
                        }
                    }
                    uniqueTextures.Add(obj.Texture);
                }
            }

            // Arrange atlases in the VRAM space.
            ArrangeAtlasesInVRAM();
            // Allocate color lookup tables (CLUTs) for textures that use palettes.
            AllocateCLUTs();

            // Build the final VRAM pixel array from placed textures and CLUTs.
            BuildVram();
            return (objects, _vramPixels);
        }

        /// <summary>
        /// Attempts to place a texture within the given atlas.
        /// Iterates over possible positions and checks for overlapping textures.
        /// </summary>
        /// <param name="atlas">The atlas where the texture should be placed.</param>
        /// <param name="texture">The texture to place.</param>
        /// <returns>True if the texture was placed successfully; otherwise, false.</returns>
        private bool TryPlaceTextureInAtlas(TextureAtlas atlas, PSXTexture2D texture)
        {
            // Iterate over potential Y positions.
            for (byte y = 0; y <= TextureAtlas.Height - texture.Height; y++)
            {
                // Iterate over potential X positions within the atlas.
                for (byte x = 0; x <= atlas.Width - texture.QuantizedWidth; x++)
                {
                    var candidateRect = new Rect(x, y, texture.QuantizedWidth, texture.Height);
                    // Check if candidateRect overlaps with any already placed texture.
                    if (!atlas.ContainedTextures.Any(tex => new Rect(tex.PackingX, tex.PackingY, tex.QuantizedWidth, tex.Height).Overlaps(candidateRect)))
                    {
                        texture.PackingX = x;
                        texture.PackingY = y;
                        atlas.ContainedTextures.Add(texture);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Arranges all texture atlases into the VRAM, ensuring they do not overlap reserved areas.
        /// Also assigns texpage indices for textures based on atlas position.
        /// </summary>
        private void ArrangeAtlasesInVRAM()
        {
            // Process each bit depth category in order.
            foreach (var bitDepth in new[] { PSXBPP.TEX_16BIT, PSXBPP.TEX_8BIT, PSXBPP.TEX_4BIT })
            {
                foreach (var atlas in _textureAtlases.Where(a => a.BitDepth == bitDepth))
                {
                    bool placed = false;
                    // Try every possible row (stepping by atlas height).
                    for (int y = 0; y <= VRAM_HEIGHT - TextureAtlas.Height; y += 256)
                    {
                        // Try every possible column (stepping by 64 pixels).
                        for (int x = 0; x <= VRAM_WIDTH - atlas.Width; x += 64)
                        {
                            // Only consider atlases that haven't been placed yet.
                            if (atlas.PositionX == 0 && atlas.PositionY == 0)
                            {
                                var candidateRect = new Rect(x, y, atlas.Width, TextureAtlas.Height);
                                if (IsPlacementValid(candidateRect))
                                {
                                    atlas.PositionX = x;
                                    atlas.PositionY = y;
                                    _finalizedAtlases.Add(atlas);
                                    placed = true;
                                    Debug.Log($"Placed an atlas at: {x},{y}");
                                    break;
                                }
                            }
                        }
                        if (placed)
                        {
                            // Assign texpage coordinates for each texture within the atlas.
                            foreach (PSXTexture2D texture in atlas.ContainedTextures)
                            {
                                int colIndex = atlas.PositionX / 64;
                                int rowIndex = atlas.PositionY / 256;
                                texture.TexpageX = (byte)colIndex;
                                texture.TexpageY = (byte)rowIndex;
                            }
                            break;
                        }
                    }
                    if (!placed)
                    {
                        Debug.LogError($"Atlas with BitDepth {atlas.BitDepth} and Width {atlas.Width} could not be placed in VRAM.");
                    }
                }
            }
        }

        /// <summary>
        /// Allocates color lookup table (CLUT) regions in VRAM for textures with palettes.
        /// </summary>
        private void AllocateCLUTs()
        {
            foreach (var texture in _finalizedAtlases.SelectMany(atlas => atlas.ContainedTextures))
            {
                // Skip textures without a color palette.
                if (texture.ColorPalette == null || texture.ColorPalette.Count == 0)
                    continue;

                int clutWidth = texture.ColorPalette.Count;
                int clutHeight = 1;
                bool placed = false;

                // Iterate over possible CLUT positions in VRAM.
                for (ushort x = 0; x < VRAM_WIDTH; x += 16)
                {
                    for (ushort y = 0; y <= VRAM_HEIGHT; y++)
                    {
                        var candidate = new Rect(x, y, clutWidth, clutHeight);
                        if (IsPlacementValid(candidate))
                        {
                            _allocatedCLUTs.Add(candidate);
                            texture.ClutPackingX = x;
                            texture.ClutPackingY = y;
                            placed = true;
                            break;
                        }
                    }
                    if (placed) break;
                }

                if (!placed)
                {
                    Debug.LogError($"Failed to allocate CLUT for texture at {texture.PackingX}, {texture.PackingY}");
                }
            }
        }

        /// <summary>
        /// Builds the final VRAM by copying texture image data and color palettes into the VRAM pixel array.
        /// </summary>
        private void BuildVram()
        {
            foreach (TextureAtlas atlas in _finalizedAtlases)
            {
                foreach (PSXTexture2D texture in atlas.ContainedTextures)
                {
                    // Copy texture image data into VRAM using atlas and texture packing offsets.
                    for (int y = 0; y < texture.Height; y++)
                    {
                        for (int x = 0; x < texture.QuantizedWidth; x++)
                        {
                            _vramPixels[x + atlas.PositionX + texture.PackingX, y + atlas.PositionY + texture.PackingY] = texture.ImageData[x, y];
                        }
                    }

                    // For non-16-bit textures, copy the color palette into VRAM.
                    if (texture.BitDepth != PSXBPP.TEX_16BIT)
                    {
                        for (int x = 0; x < texture.ColorPalette.Count; x++)
                        {
                            _vramPixels[x + texture.ClutPackingX, texture.ClutPackingY] = texture.ColorPalette[x];
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Checks if a given rectangle can be placed in VRAM without overlapping existing atlases,
        /// reserved areas, or allocated CLUT regions.
        /// </summary>
        /// <param name="rect">The rectangle representing a candidate placement.</param>
        /// <returns>True if the placement is valid; otherwise, false.</returns>
        private bool IsPlacementValid(Rect rect)
        {
            // Ensure the rectangle fits within VRAM boundaries.
            if (rect.x + rect.width > VRAM_WIDTH) return false;
            if (rect.y + rect.height > VRAM_HEIGHT) return false;

            // Check for overlaps with existing atlases.
            bool overlapsAtlas = _finalizedAtlases.Any(a => new Rect(a.PositionX, a.PositionY, a.Width, TextureAtlas.Height).Overlaps(rect));
            // Check for overlaps with reserved VRAM areas.
            bool overlapsReserved = _reservedAreas.Any(r => r.Overlaps(rect));
            // Check for overlaps with already allocated CLUT regions.
            bool overlapsCLUT = _allocatedCLUTs.Any(c => c.Overlaps(rect));

            return !(overlapsAtlas || overlapsReserved || overlapsCLUT);
        }

        /// <summary>
        /// Calculates the texpage index from given VRAM coordinates.
        /// This helper method divides VRAM into columns and rows.
        /// </summary>
        /// <param name="x">The X coordinate in VRAM.</param>
        /// <param name="y">The Y coordinate in VRAM.</param>
        /// <returns>The calculated texpage index.</returns>
        private int CalculateTexpage(int x, int y)
        {
            int columns = 16;
            int colIndex = x / 64;
            int rowIndex = y / 256;
            return (rowIndex * columns) + colIndex;
        }
    }
}
