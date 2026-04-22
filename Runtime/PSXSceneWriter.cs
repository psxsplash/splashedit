using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace SplashEdit.RuntimeCode
{
    /// <summary>
    /// Pure binary serializer for the splashpack v16 format.
    /// All I/O extracted from PSXSceneExporter so the MonoBehaviour stays thin.
    /// </summary>
    public static class PSXSceneWriter
    {
        /// <summary>
        /// Pre-compiled Lua bytecode, keyed by Lua source text.
        /// When populated, Write() packs bytecode instead of source text.
        /// Set by the build pipeline after running luac_psx, cleared after export.
        /// </summary>
        public static Dictionary<string, byte[]> CompiledLuaBytecode { get; set; }

        /// <summary>
        /// All scene data needed to produce a .bin file.
        /// Populated by PSXSceneExporter before calling <see cref="Write"/>.
        /// </summary>
        public struct SceneData
        {
            public PSXObjectExporter[] exporters;
            public TextureAtlas[] atlases;
            public PSXInteractable[] interactables;
            public AudioClipExport[] audioClips;
            public PSXNavRegionBuilder navRegionBuilder;
            public PSXRoomBuilder roomBuilder;
            public BVH bvh;
            public LuaFile sceneLuaFile;
            public float gteScaling;

            // Cutscene data (v12)
            public PSXCutsceneClip[] cutscenes;
            public PSXAudioClip[] audioSources;

            // Animation data (v17)
            public PSXAnimationClip[] animations;

            // UI canvases (v13)
            public PSXCanvasData[] canvases;

            // Custom fonts (v13, embedded in UI block)
            public PSXFontData[] fonts;

            // Trigger boxes (v16)
            public PSXTriggerBox[] triggerBoxes;

            // Skinned mesh data (v18)
            public PSXSkinnedMeshExporter.BakedSkinData[] bakedSkinData;
            public PSXSkinnedObjectExporter[] skinnedExporters;

            // Player
            public Vector3 playerPos;
            public Quaternion playerRot;
            public float playerHeight;
            public float playerRadius;
            public float moveSpeed;
            public float sprintSpeed;
            public float jumpHeight;
            public float gravity;

            // Scene configuration (v11)
            public PSXSceneType sceneType;
            public bool fogEnabled;
            public Color fogColor;
            public int fogDensity;      // 1-10
        }

        // ─── Offset bookkeeping ───

        private sealed class OffsetData
        {
            public readonly List<long> PlaceholderPositions = new List<long>();
            public readonly List<long> DataOffsets = new List<long>();
        }

        // ═══════════════════════════════════════════════════════════════
        // Public API
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Serialize the scene to splashpack v20 format: three separate files.
        /// - path                  → .splashpack (live data only)
        /// - path.Replace(…).vram  → VRAM bulk data (atlas pixels + CLUTs + font pixels)
        /// - path.Replace(…).spu   → SPU bulk data (audio ADPCM)
        /// </summary>
        /// <param name="path">Absolute file path to write (splashpack).</param>
        /// <param name="scene">Pre-built scene data.</param>
        /// <param name="log">Optional callback for progress messages.</param>
        public static void Write(string path, in SceneData scene, Action<string, LogType> log = null)
        {
            float gte = scene.gteScaling;
            int totalFaces = 0;

            OffsetData luaOffset = new OffsetData();
            OffsetData meshOffset = new OffsetData();
            OffsetData atlasOffset = new OffsetData();
            OffsetData clutOffset = new OffsetData();

            int clutCount = 0;
            List<LuaFile> luaFiles = new List<LuaFile>();

            // Count CLUTs
            foreach (TextureAtlas atlas in scene.atlases)
            {
                foreach (var texture in atlas.ContainedTextures)
                {
                    if (texture.ColorPalette != null)
                        clutCount++;
                }
            }

            // Collect unique Lua files
            foreach (PSXObjectExporter exporter in scene.exporters)
            {
                if (exporter.LuaFile != null && !luaFiles.Contains(exporter.LuaFile))
                    luaFiles.Add(exporter.LuaFile);
            }
            if (scene.sceneLuaFile != null && !luaFiles.Contains(scene.sceneLuaFile))
                luaFiles.Add(scene.sceneLuaFile);
            // Trigger box Lua files
            if (scene.triggerBoxes != null)
            {
                foreach (var tb in scene.triggerBoxes)
                {
                    if (tb.LuaFile != null && !luaFiles.Contains(tb.LuaFile))
                        luaFiles.Add(tb.LuaFile);
                }
            }

            using (BinaryWriter writer = new BinaryWriter(
                new System.IO.FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)))
            {
                int colliderCount = 0;
                foreach (var e in scene.exporters)
                {
                    if (e.CollisionType != PSXCollisionType.Dynamic) continue;
                    MeshFilter mf = e.GetComponent<MeshFilter>();
                    if (mf?.sharedMesh != null)
                        colliderCount++;
                }

                int triggerBoxCount = scene.triggerBoxes?.Length ?? 0;

                // Build exporter index lookup for components
                Dictionary<PSXObjectExporter, int> exporterIndex = new Dictionary<PSXObjectExporter, int>();
                for (int i = 0; i < scene.exporters.Length; i++)
                    exporterIndex[scene.exporters[i]] = i;

                // ──────────────────────────────────────────────────────
                // Header (120 bytes — splashpack v20)
                // ──────────────────────────────────────────────────────
                writer.Write('S');
                writer.Write('P');
                writer.Write((ushort)20);
                writer.Write((ushort)luaFiles.Count);
                writer.Write((ushort)scene.exporters.Length);
                writer.Write((ushort)scene.atlases.Length);
                writer.Write((ushort)clutCount);
                writer.Write((ushort)colliderCount);
                writer.Write((ushort)scene.interactables.Length);
                writer.Write(PSXTrig.ConvertCoordinateToPSX(scene.playerPos.x, gte));
                writer.Write(PSXTrig.ConvertCoordinateToPSX(-scene.playerPos.y, gte));
                writer.Write(PSXTrig.ConvertCoordinateToPSX(scene.playerPos.z, gte));

                writer.Write(PSXTrig.ConvertToFixed12(scene.playerRot.eulerAngles.x * Mathf.Deg2Rad));
                writer.Write(PSXTrig.ConvertToFixed12(scene.playerRot.eulerAngles.y * Mathf.Deg2Rad));
                writer.Write(PSXTrig.ConvertToFixed12(scene.playerRot.eulerAngles.z * Mathf.Deg2Rad));

                writer.Write((ushort)PSXTrig.ConvertCoordinateToPSX(scene.playerHeight, gte));

                if (scene.sceneLuaFile != null)
                    writer.Write((short)luaFiles.IndexOf(scene.sceneLuaFile));
                else
                    writer.Write((short)-1);

                // Overflow guards: these counts are serialized as uint16.
                // If they exceed 65535, the binary will be corrupted silently.
                if (scene.bvh.NodeCount > 65535)
                    Debug.LogError($"BVH node count ({scene.bvh.NodeCount}) exceeds uint16 max! Scene has too many BVH nodes.");
                if (scene.bvh.TriangleRefCount > 65535)
                    Debug.LogError($"BVH triangle ref count ({scene.bvh.TriangleRefCount}) exceeds uint16 max! Scene has too many triangle references.");

                writer.Write((ushort)Mathf.Min(scene.bvh.NodeCount, 65535));
                writer.Write((ushort)Mathf.Min(scene.bvh.TriangleRefCount, 65535));

                writer.Write((ushort)scene.sceneType);
                writer.Write((ushort)triggerBoxCount); // was pad0

                writer.Write((ushort)0); // collisionMeshCount (removed, kept for binary compat)
                writer.Write((ushort)0); // collisionTriCount (removed, kept for binary compat)
                writer.Write((ushort)scene.navRegionBuilder.RegionCount);
                writer.Write((ushort)scene.navRegionBuilder.PortalCount);

                // Movement parameters (12 bytes)
                {
                    const float fps = 30f;
                    float movePerFrame = scene.moveSpeed / fps / gte;
                    float sprintPerFrame = scene.sprintSpeed / fps / gte;
                    writer.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(movePerFrame * 4096f), 0, 65535));
                    writer.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(sprintPerFrame * 4096f), 0, 65535));

                    float jumpVel = Mathf.Sqrt(2f * scene.gravity * scene.jumpHeight) / gte;
                    writer.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(jumpVel * 4096f), 0, 65535));

                    float gravPsx = scene.gravity / gte;
                    writer.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(gravPsx * 4096f), 0, 65535));

                    writer.Write((ushort)PSXTrig.ConvertCoordinateToPSX(scene.playerRadius, gte));
                    writer.Write((ushort)0); // pad1
                }

                long nameTableOffsetPos = writer.BaseStream.Position;
                writer.Write((uint)0);

                int audioClipCount = scene.audioClips?.Length ?? 0;
                writer.Write((ushort)audioClipCount);
                writer.Write((ushort)0); // pad2
                long audioTableOffsetPos = writer.BaseStream.Position;
                writer.Write((uint)0);

                {
                    writer.Write((byte)(scene.fogEnabled ? 1 : 0));
                    writer.Write((byte)Mathf.Clamp(Mathf.RoundToInt(scene.fogColor.r * 255f), 0, 255));
                    writer.Write((byte)Mathf.Clamp(Mathf.RoundToInt(scene.fogColor.g * 255f), 0, 255));
                    writer.Write((byte)Mathf.Clamp(Mathf.RoundToInt(scene.fogColor.b * 255f), 0, 255));
                    writer.Write((byte)Mathf.Clamp(scene.fogDensity, 1, 10));
                    writer.Write((byte)0); // pad3
                    int roomCount = scene.roomBuilder?.RoomCount ?? 0;
                    int portalCount = scene.roomBuilder?.PortalCount ?? 0;
                    int roomTriRefCount = scene.roomBuilder?.TotalTriRefCount ?? 0;

                    // Overflow guards for room system counts (all serialized as uint16)
                    if (roomCount + 1 > 65535)
                        Debug.LogError($"Room count ({roomCount}+1) exceeds uint16 max!");
                    if (portalCount > 65535)
                        Debug.LogError($"Portal count ({portalCount}) exceeds uint16 max!");
                    if (roomTriRefCount > 65535)
                        Debug.LogError($"Room triangle ref count ({roomTriRefCount}) exceeds uint16 max! Reduce scene complexity.");

                    writer.Write((ushort)Mathf.Min(roomCount > 0 ? roomCount + 1 : 0, 65535));
                    writer.Write((ushort)Mathf.Min(portalCount, 65535));
                    writer.Write((ushort)Mathf.Min(roomTriRefCount, 65535));
                }

                int cutsceneCount = scene.cutscenes?.Length ?? 0;
                writer.Write((ushort)cutsceneCount);
                int roomCellCount = scene.roomBuilder?.CellCount ?? 0;
                if (roomCellCount > 65535)
                    Debug.LogError($"Room cell count ({roomCellCount}) exceeds uint16 max!");
                writer.Write((ushort)Mathf.Min(roomCellCount, 65535));
                long cutsceneTableOffsetPos = writer.BaseStream.Position;
                writer.Write((uint)0); // cutsceneTableOffset placeholder

                int uiCanvasCount = scene.canvases?.Length ?? 0;
                int uiFontCount = scene.fonts?.Length ?? 0;
                writer.Write((ushort)uiCanvasCount);
                writer.Write((byte)uiFontCount);
                writer.Write((byte)0); // uiPad5
                long uiTableOffsetPos = writer.BaseStream.Position;
                writer.Write((uint)0);

                long pixelDataOffsetPos = writer.BaseStream.Position;
                writer.Write((uint)0); // pixelDataOffset placeholder

                // Animation header fields (v17)
                int animationCount = scene.animations?.Length ?? 0;
                writer.Write((ushort)animationCount);
                int roomPortalRefCount = scene.roomBuilder?.PortalRefCount ?? 0;
                if (roomPortalRefCount > 65535)
                    Debug.LogError($"Room portal ref count ({roomPortalRefCount}) exceeds uint16 max!");
                writer.Write((ushort)Mathf.Min(roomPortalRefCount, 65535));
                long animationTableOffsetPos = writer.BaseStream.Position;
                writer.Write((uint)0); // animationTableOffset placeholder

                // Skinned mesh header fields (v18)
                int skinnedMeshCount = scene.bakedSkinData?.Length ?? 0;
                writer.Write((ushort)skinnedMeshCount);
                writer.Write((ushort)0); // pad_skin
                long skinTableOffsetPos = writer.BaseStream.Position;
                writer.Write((uint)0); // skinTableOffset placeholder

                // ──────────────────────────────────────────────────────
                // Lua file metadata
                // ──────────────────────────────────────────────────────
                foreach (LuaFile luaFile in luaFiles)
                {
                    luaOffset.PlaceholderPositions.Add(writer.BaseStream.Position);
                    writer.Write((int)0); // placeholder

                    // Use compiled bytecode length if available, otherwise source length
                    if (CompiledLuaBytecode != null && CompiledLuaBytecode.TryGetValue(luaFile.LuaScript, out byte[] bytecode))
                        writer.Write((uint)bytecode.Length);
                    else
                        writer.Write((uint)Encoding.UTF8.GetByteCount(luaFile.LuaScript));
                }

                // ──────────────────────────────────────────────────────
                // GameObject section
                // ──────────────────────────────────────────────────────
                // Build a set of proxy PSXObjectExporters that represent skinned meshes
                HashSet<PSXObjectExporter> skinnedProxySet = new HashSet<PSXObjectExporter>();
                if (scene.skinnedExporters != null)
                {
                    foreach (var skinExp in scene.skinnedExporters)
                    {
                        if (skinExp != null && skinExp.ProxyExporter != null)
                            skinnedProxySet.Add(skinExp.ProxyExporter);
                    }
                }

                Dictionary<PSXObjectExporter, int> interactableIndices = new Dictionary<PSXObjectExporter, int>();
                for (int i = 0; i < scene.interactables.Length; i++)
                {
                    var exp = scene.interactables[i].GetComponent<PSXObjectExporter>();
                    if (exp != null) interactableIndices[exp] = i;
                }

                foreach (PSXObjectExporter exporter in scene.exporters)
                {
                    meshOffset.PlaceholderPositions.Add(writer.BaseStream.Position);
                    writer.Write((int)0); // placeholder

                    // Transform — position as 20.12 fixed-point
                    Vector3 pos = exporter.transform.localToWorldMatrix.GetPosition();
                    writer.Write(PSXTrig.ConvertWorldToFixed12(pos.x / gte));
                    writer.Write(PSXTrig.ConvertWorldToFixed12(-pos.y / gte));
                    writer.Write(PSXTrig.ConvertWorldToFixed12(pos.z / gte));

                    int[,] rot = PSXTrig.ConvertRotationToPSXMatrix(exporter.transform.rotation);
                    for (int r = 0; r < 3; r++)
                        for (int c = 0; c < 3; c++)
                            writer.Write((int)rot[r, c]);

                    writer.Write((ushort)exporter.Mesh.Triangles.Count);

                    if (exporter.LuaFile != null)
                        writer.Write((short)luaFiles.IndexOf(exporter.LuaFile));
                    else
                        writer.Write((short)-1);

                    // Bitfield (LSB = isActive, bit 4 = isSkinned)
                    int flagsAsInt = exporter.IsActive ? 1 : 0;
                    if (skinnedProxySet.Contains(exporter))
                        flagsAsInt |= 0x10; // bit 4 = isSkinned
                    writer.Write(flagsAsInt);

                    // Component indices (8 bytes)
                    writer.Write(interactableIndices.TryGetValue(exporter, out int interactIdx) ? (ushort)interactIdx : (ushort)0xFFFF);
                    writer.Write((ushort)0);      // uv offset (legacy healthIndex)
                    writer.Write((uint)0);        // eventMask (runtime-only, must be zero)

                    // World-space AABB (24 bytes)
                    WriteObjectAABB(writer, exporter, gte);
                }

                // ──────────────────────────────────────────────────────
                // Collider metadata (32 bytes each) — Dynamic objects only
                // ──────────────────────────────────────────────────────
                for (int exporterIdx = 0; exporterIdx < scene.exporters.Length; exporterIdx++)
                {
                    PSXObjectExporter exporter = scene.exporters[exporterIdx];
                    if (exporter.CollisionType != PSXCollisionType.Dynamic) continue;

                    MeshFilter meshFilter = exporter.GetComponent<MeshFilter>();
                    Mesh renderMesh = meshFilter?.sharedMesh;
                    if (renderMesh == null) continue;

                    WriteWorldAABB(writer, exporter, renderMesh.bounds, gte);

                    writer.Write((byte)1);  // CollisionType::Solid on C++ side
                    writer.Write((byte)0xFF); // layerMask (all layers)
                    writer.Write((ushort)exporterIdx);
                    writer.Write((uint)0);
                }

                // ──────────────────────────────────────────────────────
                // Trigger box metadata (32 bytes each)
                // ──────────────────────────────────────────────────────
                if (scene.triggerBoxes != null)
                {
                    foreach (var tb in scene.triggerBoxes)
                    {
                        Bounds wb = tb.GetWorldBounds();
                        Vector3 wMin = wb.min;
                        Vector3 wMax = wb.max;

                        writer.Write(PSXTrig.ConvertWorldToFixed12(wMin.x / gte));
                        writer.Write(PSXTrig.ConvertWorldToFixed12(-wMax.y / gte));
                        writer.Write(PSXTrig.ConvertWorldToFixed12(wMin.z / gte));
                        writer.Write(PSXTrig.ConvertWorldToFixed12(wMax.x / gte));
                        writer.Write(PSXTrig.ConvertWorldToFixed12(-wMin.y / gte));
                        writer.Write(PSXTrig.ConvertWorldToFixed12(wMax.z / gte));

                        if (tb.LuaFile != null)
                            writer.Write((short)luaFiles.IndexOf(tb.LuaFile));
                        else
                            writer.Write((short)-1);
                        writer.Write((ushort)0); // padding
                        writer.Write((uint)0);   // padding
                    }
                }

                // ──────────────────────────────────────────────────────
                // BVH data (inline)
                // ──────────────────────────────────────────────────────
                AlignToFourBytes(writer);
                scene.bvh.WriteToBinary(writer, gte);

                // ──────────────────────────────────────────────────────
                // Interactable components (28 bytes each)
                // ──────────────────────────────────────────────────────
                AlignToFourBytes(writer);
                foreach (PSXInteractable interactable in scene.interactables)
                {
                    var exp = interactable.GetComponent<PSXObjectExporter>();
                    int goIndex = exporterIndex.TryGetValue(exp, out int idx) ? idx : 0xFFFF;

                    float radiusSq = interactable.InteractionRadius * interactable.InteractionRadius;
                    writer.Write(PSXTrig.ConvertWorldToFixed12(radiusSq / (gte * gte)));

                    writer.Write((byte)interactable.InteractButton);
                    byte flags = 0;
                    if (interactable.IsRepeatable) flags |= 0x01;
                    if (interactable.ShowPrompt) flags |= 0x02;
                    if (interactable.RequireLineOfSight) flags |= 0x04;
                    writer.Write(flags);
                    writer.Write(interactable.CooldownFrames);

                    writer.Write((ushort)0); // currentCooldown (runtime)
                    writer.Write((ushort)goIndex);

                    // Prompt canvas name (16 bytes, null-terminated, zero-padded)
                    string canvasName = interactable.PromptCanvasName ?? "";
                    byte[] nameBytes = new byte[16];
                    int len = System.Math.Min(canvasName.Length, 15);
                    for (int ci = 0; ci < len; ci++)
                        nameBytes[ci] = (byte)canvasName[ci];
                    writer.Write(nameBytes);
                }

                // ──────────────────────────────────────────────────────
                // Nav region data (version 7+)
                // ──────────────────────────────────────────────────────
                if (scene.navRegionBuilder.RegionCount > 0)
                {
                    AlignToFourBytes(writer);
                    scene.navRegionBuilder.WriteToBinary(writer, gte);
                }

                // ──────────────────────────────────────────────────────
                // Room/portal data (version 11, interior scenes)
                // Must be in the sequential cursor section (after nav regions,
                // before atlas metadata) so the C++ reader can find it.
                // ──────────────────────────────────────────────────────
                if (scene.roomBuilder != null && scene.roomBuilder.RoomCount > 0)
                {
                    AlignToFourBytes(writer);
                    scene.roomBuilder.WriteToBinary(writer, scene.gteScaling);
                    log?.Invoke($"Room/portal data: {scene.roomBuilder.RoomCount} rooms, {scene.roomBuilder.PortalCount} portals, {scene.roomBuilder.TotalTriRefCount} tri-refs.", LogType.Log);
                }

                // ──────────────────────────────────────────────────────
                // Atlas metadata
                // ──────────────────────────────────────────────────────
                foreach (TextureAtlas atlas in scene.atlases)
                {
                    atlasOffset.PlaceholderPositions.Add(writer.BaseStream.Position);
                    writer.Write((int)0); // placeholder
                    writer.Write((ushort)atlas.Width);
                    writer.Write((ushort)TextureAtlas.Height);
                    writer.Write((ushort)atlas.PositionX);
                    writer.Write((ushort)atlas.PositionY);
                }

                // ──────────────────────────────────────────────────────
                // CLUT metadata
                // ──────────────────────────────────────────────────────
                foreach (TextureAtlas atlas in scene.atlases)
                {
                    foreach (var texture in atlas.ContainedTextures)
                    {
                        if (texture.ColorPalette != null)
                        {
                            clutOffset.PlaceholderPositions.Add(writer.BaseStream.Position);
                            writer.Write((int)0); // placeholder
                            writer.Write((ushort)texture.ClutPackingX);
                            writer.Write((ushort)texture.ClutPackingY);
                            writer.Write((ushort)texture.ColorPalette.Count);
                            writer.Write((ushort)0);
                        }
                    }
                }

                // ══════════════════════════════════════════════════════
                // Data sections
                // ══════════════════════════════════════════════════════

                // Lua data (bytecode if compiled, source text otherwise)
                int luaIdx = 0;
                log?.Invoke($"CompiledLuaBytecode: {(CompiledLuaBytecode != null ? CompiledLuaBytecode.Count + " entries" : "null")}", LogType.Log);
                foreach (LuaFile luaFile in luaFiles)
                {
                    AlignToFourBytes(writer);
                    luaOffset.DataOffsets.Add(writer.BaseStream.Position);

                    if (CompiledLuaBytecode != null && CompiledLuaBytecode.TryGetValue(luaFile.LuaScript, out byte[] bytecode))
                    {
                        log?.Invoke($"  Lua [{luaIdx}]: using bytecode ({bytecode.Length} bytes)", LogType.Log);
                        writer.Write(bytecode);
                    }
                    else
                    {
                        byte[] srcBytes = Encoding.UTF8.GetBytes(luaFile.LuaScript);
                        log?.Invoke($"  Lua [{luaIdx}]: FALLBACK to source ({srcBytes.Length} bytes, script hash={luaFile.LuaScript.GetHashCode()})", LogType.Warning);
                        writer.Write(srcBytes);
                    }
                    luaIdx++;
                }

                // Mesh data
                foreach (PSXObjectExporter exporter in scene.exporters)
                {
                    AlignToFourBytes(writer);
                    meshOffset.DataOffsets.Add(writer.BaseStream.Position);
                    totalFaces += exporter.Mesh.Triangles.Count;

                    foreach (Tri tri in exporter.Mesh.Triangles)
                    {
                        // Vertex positions (3 × 6 bytes)
                        WriteVertexPosition(writer, tri.v0);
                        WriteVertexPosition(writer, tri.v1);
                        WriteVertexPosition(writer, tri.v2);

                        // Normal for v0 only
                        WriteVertexNormals(writer, tri.v0);

                        // Vertex colors (3 × 4 bytes)
                        WriteVertexColor(writer, tri.v0);
                        WriteVertexColor(writer, tri.v1);
                        WriteVertexColor(writer, tri.v2);

                        ushort flags = 0;
                        if (exporter.UVOffsetMaterial == tri.TextureIndex)
                        {
                            flags |= 0x1;
                        }

                        if (tri.IsUntextured)
                        {
                            // Zero UVs
                            writer.Write((byte)0); writer.Write((byte)0);
                            writer.Write((byte)0); writer.Write((byte)0);
                            writer.Write((byte)0); writer.Write((byte)0);
                            writer.Write((ushort)0); // padding

                            // Sentinel tpage = 0xFFFF marks untextured
                            // haha funny word. Sentinel, sentinel, sentinel. I could keep saying it forever.
                            writer.Write((ushort)0xFFFF);
                            writer.Write((ushort)0);
                            writer.Write((ushort)0);
                            writer.Write(flags);
                        }
                        else
                        {
                            PSXTexture2D tex = exporter.GetTexture(tri.TextureIndex);
                            int expander = 16 / (int)tex.BitDepth;

                            WriteVertexUV(writer, tri.v0, tex, expander);
                            WriteVertexUV(writer, tri.v1, tex, expander);
                            WriteVertexUV(writer, tri.v2, tex, expander);
                            writer.Write((ushort)0); // padding

                            TPageAttr tpage = new TPageAttr();
                            tpage.SetPageX(tex.TexpageX);
                            tpage.SetPageY(tex.TexpageY);
                            tpage.Set(tex.BitDepth.ToColorMode());
                            tpage.SetDithering(true);
                            writer.Write((ushort)tpage.info);
                            writer.Write((ushort)tex.ClutPackingX);
                            writer.Write((ushort)tex.ClutPackingY);
                            writer.Write(flags); // padding
                        }
                    }
                }

                // ──────────────────────────────────────────────────────
                // Object name table (version 9)
                // ──────────────────────────────────────────────────────
                AlignToFourBytes(writer);
                long nameTableStart = writer.BaseStream.Position;
                foreach (PSXObjectExporter exporter in scene.exporters)
                {
                    string objName = exporter.gameObject.name;
                    if (objName.Length > 24) objName = objName.Substring(0, 24);
                    byte[] nameBytes = Encoding.UTF8.GetBytes(objName);
                    writer.Write((byte)nameBytes.Length);
                    writer.Write(nameBytes);
                    writer.Write((byte)0); // null terminator
                }

                // Backfill name table offset
                {
                    long endPos = writer.BaseStream.Position;
                    writer.Seek((int)nameTableOffsetPos, SeekOrigin.Begin);
                    writer.Write((uint)nameTableStart);
                    writer.Seek((int)endPos, SeekOrigin.Begin);
                }

                // ──────────────────────────────────────────────────────
                // Audio clip data (version 10)
                // Metadata entries are 16 bytes each, written contiguously.
                // Name strings follow the metadata block with backfilled offsets.
                // ADPCM blobs deferred to dead zone.
                // ──────────────────────────────────────────────────────
                List<long> audioDataOffsetPositions = new List<long>();
                if (audioClipCount > 0 && scene.audioClips != null)
                {
                    AlignToFourBytes(writer);
                    long audioTableStart = writer.BaseStream.Position;

                    List<long> audioNameOffsetPositions = new List<long>();
                    List<string> audioClipNames = new List<string>();

                    // Phase 1: Write all 16-byte metadata entries contiguously
                    for (int i = 0; i < audioClipCount; i++)
                    {
                        var clip = scene.audioClips[i];
                        string name = clip.clipName ?? "";
                        if (name.Length > 255) name = name.Substring(0, 255);

                        audioDataOffsetPositions.Add(writer.BaseStream.Position);
                        writer.Write((uint)0);  // dataOffset placeholder (backfilled in dead zone)
                        writer.Write((uint)(clip.adpcmData?.Length ?? 0));
                        writer.Write((ushort)clip.sampleRate);
                        writer.Write((byte)(clip.loop ? 1 : 0));
                        writer.Write((byte)name.Length);
                        audioNameOffsetPositions.Add(writer.BaseStream.Position);
                        writer.Write((uint)0);  // nameOffset placeholder
                        audioClipNames.Add(name); 
                    }

                    // Phase 2: Write name strings (after all metadata entries)
                    for (int i = 0; i < audioClipCount; i++)
                    {
                        string name = audioClipNames[i];
                        long namePos = writer.BaseStream.Position;
                        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
                        writer.Write(nameBytes);
                        writer.Write((byte)0);

                        long curPos = writer.BaseStream.Position;
                        writer.Seek((int)audioNameOffsetPositions[i], SeekOrigin.Begin);
                        writer.Write((uint)namePos);
                        writer.Seek((int)curPos, SeekOrigin.Begin);
                    }

                    // Backfill audio table offset in header
                    {
                        long curPos = writer.BaseStream.Position;
                        writer.Seek((int)audioTableOffsetPos, SeekOrigin.Begin);
                        writer.Write((uint)audioTableStart);
                        writer.Seek((int)curPos, SeekOrigin.Begin);
                    }
                }

                // ──────────────────────────────────────────────────────
                // Cutscene data (version 12)
                // ──────────────────────────────────────────────────────
                if (cutsceneCount > 0)
                {
                    PSXCutsceneExporter.ExportCutscenes(
                        writer,
                        scene.cutscenes,
                        scene.exporters,
                        scene.audioSources,
                        scene.skinnedExporters,
                        scene.gteScaling,
                        out long cutsceneTableActual,
                        log);

                    // Backfill cutscene table offset in header
                    if (cutsceneTableActual != 0)
                    {
                        long curPos = writer.BaseStream.Position;
                        writer.Seek((int)cutsceneTableOffsetPos, SeekOrigin.Begin);
                        writer.Write((uint)cutsceneTableActual);
                        writer.Seek((int)curPos, SeekOrigin.Begin);
                    }
                }

                // ──────────────────────────────────────────────────────
                // Animation data (version 17)
                // ──────────────────────────────────────────────────────
                if (animationCount > 0)
                {
                    PSXAnimationExporter.ExportAnimations(
                        writer,
                        scene.animations,
                        scene.exporters,
                        scene.skinnedExporters,
                        scene.gteScaling,
                        out long animationTableActual,
                        log);

                    if (animationTableActual != 0)
                    {
                        long curPos = writer.BaseStream.Position;
                        writer.Seek((int)animationTableOffsetPos, SeekOrigin.Begin);
                        writer.Write((uint)animationTableActual);
                        writer.Seek((int)curPos, SeekOrigin.Begin);
                    }
                }

                // ──────────────────────────────────────────────────────
                // Skinned mesh data (version 18)
                // Table: 12 bytes per entry (dataOffset, nameLen+pad, nameOffset)
                // Per-mesh: gameObjectIndex(u16), boneCount(u8), clipCount(u8),
                //           boneIndices[polyCount*3], align4,
                //           per-clip: nameLen(u8), name, 0x00, flags(u8),
                //                     frameCount(u8), fps(u8), align2,
                //                     BakedBoneMatrix[frameCount*boneCount]
                // ──────────────────────────────────────────────────────
                if (skinnedMeshCount > 0 && scene.bakedSkinData != null)
                {
                    PSXSkinnedMeshExporter.ExportSkinData(
                        writer,
                        scene.bakedSkinData,
                        scene.exporters,
                        out long skinTableActual,
                        log);

                    if (skinTableActual != 0)
                    {
                        long curPos = writer.BaseStream.Position;
                        writer.Seek((int)skinTableOffsetPos, SeekOrigin.Begin);
                        writer.Write((uint)skinTableActual);
                        writer.Seek((int)curPos, SeekOrigin.Begin);
                    }
                }

                // ──────────────────────────────────────────────────────
                // UI canvas + font data (version 13)
                // Font descriptors: 112 bytes each (before canvas data)
                // Canvas descriptor table: 12 bytes per canvas
                // Element records: 48 bytes each
                // Name and text strings follow with offset backfill
                // Font pixel data is deferred to the dead zone.
                // ──────────────────────────────────────────────────────
                List<long> fontDataOffsetPositions = new List<long>();
                if ((uiCanvasCount > 0 && scene.canvases != null) || uiFontCount > 0)
                {
                    AlignToFourBytes(writer);
                    long uiTableStart = writer.BaseStream.Position;

                    // ── Font descriptors (112 bytes each) ──
                    // Layout: glyphW(1) glyphH(1) vramX(2) vramY(2) textureH(2)
                    //         dataOffset(4) dataSize(4)
                    if (scene.fonts != null)
                    {
                        foreach (var font in scene.fonts)
                        {
                            writer.Write(font.GlyphWidth);          // [0]
                            writer.Write(font.GlyphHeight);         // [1]
                            writer.Write(font.VramX);               // [2-3]
                            writer.Write(font.VramY);               // [4-5]
                            writer.Write(font.TextureHeight);       // [6-7]
                            fontDataOffsetPositions.Add(writer.BaseStream.Position);
                            writer.Write((uint)0);                  // [8-11] dataOffset placeholder
                            writer.Write((uint)(font.PixelData?.Length ?? 0)); // [12-15] dataSize
                            // [16-111] per-character advance widths for proportional rendering
                            if (font.AdvanceWidths != null && font.AdvanceWidths.Length >= 96)
                                writer.Write(font.AdvanceWidths, 0, 96);
                            else
                                writer.Write(new byte[96]); // zero-fill if missing
                        }
                    }

                    // Font pixel data is deferred to the dead zone (after pixelDataOffset).
                    // The C++ loader reads font pixel data via the dataOffset, uploads to VRAM,
                    // then never accesses it again.

                    // ── Canvas descriptor table (12 bytes each) ──
                    // Layout per descriptor:
                    //   uint32  dataOffset     — offset to this canvas's element array
                    //   uint8   nameLen
                    //   uint8   sortOrder
                    //   uint8   elementCount
                    //   uint8   flags          — bit 0 = startVisible
                    //   uint32  nameOffset     — offset to null-terminated name string
                    List<long> canvasDataOffsetPos = new List<long>();
                    List<long> canvasNameOffsetPos = new List<long>();
                    for (int ci = 0; ci < uiCanvasCount; ci++)
                    {
                        var cv = scene.canvases[ci];
                        string cvName = cv.Name ?? "";
                        if (cvName.Length > 24) cvName = cvName.Substring(0, 24);

                        canvasDataOffsetPos.Add(writer.BaseStream.Position);
                        writer.Write((uint)0);                               // dataOffset placeholder
                        writer.Write((byte)cvName.Length);                    // nameLen
                        writer.Write((byte)cv.SortOrder);                    // sortOrder
                        writer.Write((byte)(cv.Elements?.Length ?? 0));       // elementCount
                        writer.Write((byte)(cv.StartVisible ? 0x01 : 0x00)); // flags
                        canvasNameOffsetPos.Add(writer.BaseStream.Position);
                        writer.Write((uint)0);                               // nameOffset placeholder
                    }

                    // Phase 2: Write element records (56 bytes each) per canvas
                    for (int ci = 0; ci < uiCanvasCount; ci++)
                    {
                        var cv = scene.canvases[ci];
                        if (cv.Elements == null || cv.Elements.Length == 0) continue;

                        AlignToFourBytes(writer);
                        long elemStart = writer.BaseStream.Position;

                        // Track text offset positions for backfill
                        List<long> textOffsetPositions = new List<long>();
                        List<string> textContents = new List<string>();

                        foreach (var el in cv.Elements)
                        {
                            // Identity (8 bytes)
                            writer.Write((byte)el.Type);                                    // type
                            byte eFlags = (byte)(el.StartVisible ? 0x01 : 0x00);
                            writer.Write(eFlags);                                           // flags
                            string eName = el.Name ?? "";
                            if (eName.Length > 24) eName = eName.Substring(0, 24);
                            writer.Write((byte)eName.Length);                                // nameLen
                            writer.Write((byte)0);                                          // pad0
                            // nameOffset placeholder (backfilled later)
                            long elemNameOffPos = writer.BaseStream.Position;
                            writer.Write((uint)0);                                          // nameOffset

                            // Layout (8 bytes)
                            writer.Write(el.X);
                            writer.Write(el.Y);
                            writer.Write(el.W);
                            writer.Write(el.H);

                            // Anchors (4 bytes)
                            writer.Write(el.AnchorMinX);
                            writer.Write(el.AnchorMinY);
                            writer.Write(el.AnchorMaxX);
                            writer.Write(el.AnchorMaxY);

                            // Primary color (4 bytes)
                            writer.Write(el.ColorR);
                            writer.Write(el.ColorG);
                            writer.Write(el.ColorB);
                            writer.Write((byte)0); // pad1

                            // Type-specific data (16 bytes)
                            switch (el.Type)
                            {
                                case PSXUIElementType.Image:
                                    writer.Write(el.TexpageX);       // [0]
                                    writer.Write(el.TexpageY);       // [1]
                                    writer.Write(el.ClutX);          // [2-3]
                                    writer.Write(el.ClutY);          // [4-5]
                                    writer.Write(el.U0);             // [6]
                                    writer.Write(el.V0);             // [7]
                                    writer.Write(el.U1);             // [8]
                                    writer.Write(el.V1);             // [9]
                                    writer.Write(el.BitDepthIndex);  // [10]
                                    writer.Write(new byte[5]);       // [11-15] padding
                                    break;
                                case PSXUIElementType.Progress:
                                    writer.Write(el.BgR);            // [0]
                                    writer.Write(el.BgG);            // [1]
                                    writer.Write(el.BgB);            // [2]
                                    writer.Write(el.ProgressValue);  // [3]
                                    writer.Write(new byte[12]);      // [4-15] padding
                                    break;
                                case PSXUIElementType.Text:
                                    writer.Write(el.FontIndex);      // [0] font index (0=system, 1+=custom)
                                    writer.Write(new byte[15]);      // [1-15] padding
                                    break;
                                default:
                                    writer.Write(new byte[16]);      // zeroed
                                    break;
                            }

                            // Text content offset (8 bytes)
                            long textOff = writer.BaseStream.Position;
                            writer.Write((uint)0); // textOffset placeholder
                            writer.Write((uint)0); // pad2

                            // Remember for backfill
                            textOffsetPositions.Add(textOff);
                            textContents.Add(el.Type == PSXUIElementType.Text ? (el.DefaultText ?? "") : null);

                            // Also remember element name for backfill
                            // We need to write it after all elements
                            textOffsetPositions.Add(elemNameOffPos);
                            textContents.Add("__NAME__" + eName);
                        }

                        // Backfill canvas data offset
                        {
                            long curPos = writer.BaseStream.Position;
                            writer.Seek((int)canvasDataOffsetPos[ci], SeekOrigin.Begin);
                            writer.Write((uint)elemStart);
                            writer.Seek((int)curPos, SeekOrigin.Begin);
                        }

                        // Write strings and backfill offsets
                        for (int si = 0; si < textOffsetPositions.Count; si++)
                        {
                            string s = textContents[si];
                            if (s == null) continue;

                            bool isName = s.StartsWith("__NAME__");
                            string content = isName ? s.Substring(8) : s;
                            if (string.IsNullOrEmpty(content) && !isName) continue;

                            long strPos = writer.BaseStream.Position;
                            byte[] strBytes = Encoding.UTF8.GetBytes(content);
                            writer.Write(strBytes);
                            writer.Write((byte)0); // null terminator

                            long curPos = writer.BaseStream.Position;
                            writer.Seek((int)textOffsetPositions[si], SeekOrigin.Begin);
                            writer.Write((uint)strPos);
                            writer.Seek((int)curPos, SeekOrigin.Begin);
                        }
                    }

                    // Write canvas name strings and backfill name offsets
                    for (int ci = 0; ci < uiCanvasCount; ci++)
                    {
                        string cvName = scene.canvases[ci].Name ?? "";
                        if (cvName.Length > 24) cvName = cvName.Substring(0, 24);

                        long namePos = writer.BaseStream.Position;
                        byte[] nameBytes = Encoding.UTF8.GetBytes(cvName);
                        writer.Write(nameBytes);
                        writer.Write((byte)0); // null terminator

                        long curPos = writer.BaseStream.Position;
                        writer.Seek((int)canvasNameOffsetPos[ci], SeekOrigin.Begin);
                        writer.Write((uint)namePos);
                        writer.Seek((int)curPos, SeekOrigin.Begin);
                    }

                    // Backfill UI table offset in header
                    {
                        long curPos = writer.BaseStream.Position;
                        writer.Seek((int)uiTableOffsetPos, SeekOrigin.Begin);
                        writer.Write((uint)uiTableStart);
                        writer.Seek((int)curPos, SeekOrigin.Begin);
                    }

                    int totalElements = 0;
                    foreach (var cv in scene.canvases) totalElements += cv.Elements?.Length ?? 0;
                    log?.Invoke($"{uiCanvasCount} UI canvases ({totalElements} elements) written.", LogType.Log);
                }

                // ══════════════════════════════════════════════════════
                // NO MORE DEAD ZONE — pixel/audio data goes into separate files.
                // pixelDataOffset is written as 0 to signal v20 format.
                // ══════════════════════════════════════════════════════

                // Backfill pixelDataOffset as 0 (signals: no dead zone in this file)
                {
                    long curPos = writer.BaseStream.Position;
                    writer.Seek((int)pixelDataOffsetPos, SeekOrigin.Begin);
                    writer.Write((uint)0);
                    writer.Seek((int)curPos, SeekOrigin.Begin);
                }

                // Atlas metadata still needs valid VRAM coordinates for rendering,
                // but polygonsOffset/clutOffset are no longer used (data is in .vram file).
                // Write 0 for all atlas and CLUT data offsets.
                foreach (var pos in atlasOffset.PlaceholderPositions)
                {
                    long curPos = writer.BaseStream.Position;
                    writer.Seek((int)pos, SeekOrigin.Begin);
                    writer.Write((uint)0);
                    writer.Seek((int)curPos, SeekOrigin.Begin);
                }
                foreach (var pos in clutOffset.PlaceholderPositions)
                {
                    long curPos = writer.BaseStream.Position;
                    writer.Seek((int)pos, SeekOrigin.Begin);
                    writer.Write((uint)0);
                    writer.Seek((int)curPos, SeekOrigin.Begin);
                }

                // Audio ADPCM data offset placeholders → 0 (data is in .spu file)
                foreach (var pos in audioDataOffsetPositions)
                {
                    long curPos = writer.BaseStream.Position;
                    writer.Seek((int)pos, SeekOrigin.Begin);
                    writer.Write((uint)0);
                    writer.Seek((int)curPos, SeekOrigin.Begin);
                }

                // Font pixel data offset placeholders → 0 (data is in .vram file)
                foreach (var pos in fontDataOffsetPositions)
                {
                    long curPos = writer.BaseStream.Position;
                    writer.Seek((int)pos, SeekOrigin.Begin);
                    writer.Write((uint)0);
                    writer.Seek((int)curPos, SeekOrigin.Begin);
                }

                // Backfill live data offsets (lua, mesh — these still point within the splashpack)
                BackfillOffsets(writer, luaOffset, "lua", log);
                BackfillOffsets(writer, meshOffset, "mesh", log);
            }

            // ══════════════════════════════════════════════════════════════
            // Write VRAM file (.vram) — atlas pixels + CLUT data + font pixels
            // Format: VRM header + per-atlas entries + per-CLUT entries + per-font entries
            // Each entry: metadata + inline pixel data (self-contained, no offsets)
            // ══════════════════════════════════════════════════════════════
            {
                string vramPath = System.IO.Path.ChangeExtension(path, ".vram");
                using (BinaryWriter vw = new BinaryWriter(
                    new FileStream(vramPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)))
                {
                    // VRM header (8 bytes)
                    vw.Write((byte)'V');
                    vw.Write((byte)'R');
                    vw.Write((ushort)scene.atlases.Length);
                    vw.Write((ushort)clutCount);
                    vw.Write((byte)(scene.fonts?.Length ?? 0));
                    vw.Write((byte)0); // pad

                    // Per-atlas: vramX(u16) vramY(u16) width(u16) height(u16) + pixel data
                    foreach (TextureAtlas atlas in scene.atlases)
                    {
                        vw.Write((ushort)atlas.PositionX);
                        vw.Write((ushort)atlas.PositionY);
                        vw.Write((ushort)atlas.Width);
                        vw.Write((ushort)TextureAtlas.Height);
                        // Inline pixel data: width × height × 2 bytes
                        for (int y = 0; y < atlas.vramPixels.GetLength(1); y++)
                            for (int x = 0; x < atlas.vramPixels.GetLength(0); x++)
                                vw.Write(atlas.vramPixels[x, y].Pack());
                        AlignToFourBytes(vw);
                    }

                    // Per-CLUT: clutPackingX(u16) clutPackingY(u16) length(u16) pad(u16) + data
                    foreach (TextureAtlas atlas in scene.atlases)
                    {
                        foreach (var texture in atlas.ContainedTextures)
                        {
                            if (texture.ColorPalette != null)
                            {
                                vw.Write((ushort)texture.ClutPackingX);
                                vw.Write((ushort)texture.ClutPackingY);
                                vw.Write((ushort)texture.ColorPalette.Count);
                                vw.Write((ushort)0); // pad
                                foreach (VRAMPixel color in texture.ColorPalette)
                                    vw.Write((ushort)color.Pack());
                                AlignToFourBytes(vw);
                            }
                        }
                    }

                    // Per-font: glyphW(u8) glyphH(u8) vramX(u16) vramY(u16) textureH(u16) dataSize(u32) + pixel data
                    if (scene.fonts != null)
                    {
                        foreach (var font in scene.fonts)
                        {
                            vw.Write(font.GlyphWidth);
                            vw.Write(font.GlyphHeight);
                            vw.Write(font.VramX);
                            vw.Write(font.VramY);
                            vw.Write(font.TextureHeight);
                            vw.Write((uint)(font.PixelData?.Length ?? 0));
                            if (font.PixelData != null && font.PixelData.Length > 0)
                                vw.Write(font.PixelData);
                            AlignToFourBytes(vw);
                        }
                    }

                    long vramSize = vw.BaseStream.Position;
                    log?.Invoke($"VRAM data: {vramSize / 1024}KB written to {System.IO.Path.GetFileName(vramPath)}", LogType.Log);
                }
            }

            // ══════════════════════════════════════════════════════════════
            // Write SPU file (.spu) — audio ADPCM data
            // Format: SPU header + per-clip entries
            // Each entry: sizeBytes(u32) sampleRate(u16) loop(u8) pad(u8) + ADPCM data
            // ══════════════════════════════════════════════════════════════
            {
                string spuPath = System.IO.Path.ChangeExtension(path, ".spu");
                int audioClipCountForSpu = scene.audioClips?.Length ?? 0;
                using (BinaryWriter sw = new BinaryWriter(
                    new FileStream(spuPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)))
                {
                    // SPU header (4 bytes)
                    sw.Write((byte)'S');
                    sw.Write((byte)'A');
                    sw.Write((ushort)audioClipCountForSpu);

                    // Per-clip: sizeBytes(u32) sampleRate(u16) loop(u8) pad(u8) + ADPCM data
                    if (scene.audioClips != null)
                    {
                        foreach (var clip in scene.audioClips)
                        {
                            uint dataLen = (uint)(clip.adpcmData?.Length ?? 0);
                            sw.Write(dataLen);
                            sw.Write((ushort)clip.sampleRate);
                            sw.Write((byte)(clip.loop ? 1 : 0));
                            sw.Write((byte)0); // pad
                            if (clip.adpcmData != null && clip.adpcmData.Length > 0)
                                sw.Write(clip.adpcmData);
                            AlignToFourBytes(sw);
                        }
                    }

                    long spuSize = sw.BaseStream.Position;

                    int totalAudioBytes = 0;
                    if (scene.audioClips != null)
                        foreach (var clip in scene.audioClips)
                            if (clip.adpcmData != null) totalAudioBytes += clip.adpcmData.Length;
                    log?.Invoke($"SPU data: {spuSize / 1024}KB ({audioClipCountForSpu} clips, {totalAudioBytes / 1024}KB ADPCM) written to {System.IO.Path.GetFileName(spuPath)}", LogType.Log);
                }
            }

            log?.Invoke($"{totalFaces} faces written to {Path.GetFileName(path)}", LogType.Log);
        }

        // ═══════════════════════════════════════════════════════════════
        // Static helpers
        // ═══════════════════════════════════════════════════════════════

        private static void WriteVertexPosition(BinaryWriter w, PSXVertex v)
        {
            w.Write((short)v.vx);
            w.Write((short)v.vy);
            w.Write((short)v.vz);
        }

        private static void WriteVertexNormals(BinaryWriter w, PSXVertex v)
        {
            w.Write((short)v.nx);
            w.Write((short)v.ny);
            w.Write((short)v.nz);
        }

        private static void WriteVertexColor(BinaryWriter w, PSXVertex v)
        {
            w.Write((byte)v.r);
            w.Write((byte)v.g);
            w.Write((byte)v.b);
            w.Write((byte)0); // padding
        }

        private static void WriteVertexUV(BinaryWriter w, PSXVertex v, PSXTexture2D t, int expander)
        {
            w.Write((byte)(v.u + t.PackingX * expander));
            w.Write((byte)(v.v + t.PackingY));
        }

        private static void WriteObjectAABB(BinaryWriter writer, PSXObjectExporter exporter, float gte)
        {
            MeshFilter mf = exporter.GetComponent<MeshFilter>();
            Mesh mesh = mf?.sharedMesh;
            if (mesh != null)
            {
                WriteWorldAABB(writer, exporter, mesh.bounds, gte);
            }
            else
            {
                for (int z = 0; z < 6; z++) writer.Write((int)0);
            }
        }

        private static void WriteWorldAABB(BinaryWriter writer, PSXObjectExporter exporter, Bounds localBounds, float gte)
        {
            Vector3 ext = localBounds.extents;
            Vector3 center = localBounds.center;
            Vector3 aabbMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 aabbMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            // Compute world-space AABB from 8 transformed corners
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = center + new Vector3(
                    (i & 1) != 0 ? ext.x : -ext.x,
                    (i & 2) != 0 ? ext.y : -ext.y,
                    (i & 4) != 0 ? ext.z : -ext.z
                );
                Vector3 world = exporter.transform.TransformPoint(corner);
                aabbMin = Vector3.Min(aabbMin, world);
                aabbMax = Vector3.Max(aabbMax, world);
            }

            // PS1 coordinate space (negate Y, swap min/max)
            writer.Write(PSXTrig.ConvertWorldToFixed12(aabbMin.x / gte));
            writer.Write(PSXTrig.ConvertWorldToFixed12(-aabbMax.y / gte));
            writer.Write(PSXTrig.ConvertWorldToFixed12(aabbMin.z / gte));
            writer.Write(PSXTrig.ConvertWorldToFixed12(aabbMax.x / gte));
            writer.Write(PSXTrig.ConvertWorldToFixed12(-aabbMin.y / gte));
            writer.Write(PSXTrig.ConvertWorldToFixed12(aabbMax.z / gte));
        }

        private static void AlignToFourBytes(BinaryWriter writer)
        {
            long pos = writer.BaseStream.Position;
            int padding = (int)(4 - (pos % 4)) % 4;
            if (padding > 0)
                writer.Write(new byte[padding]);
        }

        private static void BackfillOffsets(BinaryWriter writer, OffsetData data, string sectionName, Action<string, LogType> log)
        {
            if (data.PlaceholderPositions.Count != data.DataOffsets.Count)
            {
                log?.Invoke($"Offset mismatch in {sectionName}: {data.PlaceholderPositions.Count} placeholders vs {data.DataOffsets.Count} data blocks", LogType.Error);
                return;
            }

            for (int i = 0; i < data.PlaceholderPositions.Count; i++)
            {
                writer.Seek((int)data.PlaceholderPositions[i], SeekOrigin.Begin);
                writer.Write((int)data.DataOffsets[i]);
            }
        }
    }
}
