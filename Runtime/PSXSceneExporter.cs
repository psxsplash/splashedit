using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Splashedit.RuntimeCode;
using UnityEditor;
using UnityEngine;

namespace SplashEdit.RuntimeCode
{

    [ExecuteInEditMode]
    public class PSXSceneExporter : MonoBehaviour
    {

        public float GTEScaling = 100.0f;
        public LuaFile SceneLuaFile;

        private PSXObjectExporter[] _exporters;
        private TextureAtlas[] _atlases;
        private PSXNavMesh[] _navmeshes;

        private PSXData _psxData;

        private Vector2 selectedResolution;
        private bool dualBuffering;
        private bool verticalLayout;
        private List<ProhibitedArea> prohibitedAreas;

        private Vector3 _playerPos;
        private Quaternion _playerRot;
        private float _playerHeight;

        public void Export()
        {
            _psxData = DataStorage.LoadData(out selectedResolution, out dualBuffering, out verticalLayout, out prohibitedAreas);

            _exporters = FindObjectsByType<PSXObjectExporter>(FindObjectsSortMode.None);
            for (int i = 0; i < _exporters.Length; i++)
            {
                PSXObjectExporter exp = _exporters[i];
                EditorUtility.DisplayProgressBar($"{nameof(PSXSceneExporter)}", $"Export {nameof(PSXObjectExporter)}", ((float)i) / _exporters.Length);
                exp.CreatePSXTextures2D();
                exp.CreatePSXMesh(GTEScaling);
            }

            _navmeshes = FindObjectsByType<PSXNavMesh>(FindObjectsSortMode.None);
            for (int i = 0; i < _navmeshes.Length; i++)
            {
                PSXNavMesh navmesh = _navmeshes[i];
                EditorUtility.DisplayProgressBar($"{nameof(PSXSceneExporter)}", $"Export {nameof(PSXNavMesh)}", ((float)i) / _navmeshes.Length);
                navmesh.CreateNavmesh(GTEScaling);
            }

            EditorUtility.ClearProgressBar();

            PackTextures();

            PSXPlayer player = FindObjectsByType<PSXPlayer>(FindObjectsSortMode.None).FirstOrDefault();
            if (player != null)
            {
                player.FindNavmesh();
                _playerPos = player.CamPoint;
                _playerHeight = player.PlayerHeight;
                _playerRot = player.transform.rotation;
            }

            ExportFile();
        }

        void PackTextures()
        {
            (Rect buffer1, Rect buffer2) = Utils.BufferForResolution(selectedResolution, verticalLayout);

            List<Rect> framebuffers = new List<Rect> { buffer1 };
            if (dualBuffering)
            {
                framebuffers.Add(buffer2);
            }

            VRAMPacker tp = new VRAMPacker(framebuffers, prohibitedAreas);
            var packed = tp.PackTexturesIntoVRAM(_exporters);
            _exporters = packed.processedObjects;
            _atlases = packed.atlases;

        }

        void ExportFile()
        {
            string path = EditorUtility.SaveFilePanel("Select Output File", "", "output", "bin");
            int totalFaces = 0;

            // Lists for lua data offsets.
            OffsetData luaOffset = new();

            // Lists for mesh data offsets.
            OffsetData meshOffset = new();

            // Lists for atlas data offsets.
            OffsetData atlasOffset = new();

            // Lists for clut data offsets.
            OffsetData clutOffset = new();

            // Lists for navmesh data offsets.
            OffsetData navmeshOffset = new();

            int clutCount = 0;
            List<LuaFile> luaFiles = new List<LuaFile>();

            // Cluts
            foreach (TextureAtlas atlas in _atlases)
            {
                foreach (var texture in atlas.ContainedTextures)
                {
                    if (texture.ColorPalette != null)
                    {
                        clutCount++;
                    }
                }
            }

            // Lua files 
            foreach (PSXObjectExporter exporter in _exporters)
            {
                if (exporter.LuaFile != null)
                {
                    //if not contains
                    if (!luaFiles.Contains(exporter.LuaFile))
                    {
                        luaFiles.Add(exporter.LuaFile);
                    }
                }
            }
            if (SceneLuaFile != null)
            {
                if (!luaFiles.Contains(SceneLuaFile))
                {
                    luaFiles.Add(SceneLuaFile);
                }
            }

            using (BinaryWriter writer = new BinaryWriter(File.Open(path, FileMode.Create)))
            {
                // Header
                writer.Write('S'); // 1 byte                                                                    // 1
                writer.Write('P'); // 1 byte                                                                    // 2 
                writer.Write((ushort)1); // 2 bytes - version                                                   // 4
                writer.Write((ushort)luaFiles.Count); // 2 bytes - padding                                                      // 6
                writer.Write((ushort)_exporters.Length); // 2 bytes                                             // 6
                writer.Write((ushort)_navmeshes.Length);                                                        // 8
                writer.Write((ushort)_atlases.Length); // 2 bytes                                               // 10
                writer.Write((ushort)clutCount); // 2 bytes                                                     // 12
                writer.Write((ushort)PSXTrig.ConvertCoordinateToPSX(_playerPos.x, GTEScaling));                 // 14
                writer.Write((ushort)PSXTrig.ConvertCoordinateToPSX(-_playerPos.y, GTEScaling));                // 16
                writer.Write((ushort)PSXTrig.ConvertCoordinateToPSX(_playerPos.z, GTEScaling));                 // 18

                writer.Write((ushort)PSXTrig.ConvertToFixed12(_playerRot.eulerAngles.x * Mathf.Deg2Rad));       // 20
                writer.Write((ushort)PSXTrig.ConvertToFixed12(_playerRot.eulerAngles.y * Mathf.Deg2Rad));       // 22
                writer.Write((ushort)PSXTrig.ConvertToFixed12(_playerRot.eulerAngles.z * Mathf.Deg2Rad));       // 24

                writer.Write((ushort)PSXTrig.ConvertCoordinateToPSX(_playerHeight, GTEScaling));                // 26

                if (SceneLuaFile != null)
                {
                    int index = luaFiles.IndexOf(SceneLuaFile);
                    writer.Write((short)index);
                }
                else
                {
                    writer.Write((short)-1);
                }
                writer.Write((ushort)0);

                // Lua file section
                foreach (LuaFile luaFile in luaFiles)
                {
                    // Write placeholder for lua file data offset and record its position.
                    luaOffset.OffsetPlaceholderPositions.Add(writer.BaseStream.Position);
                    writer.Write((int)0); // 4-byte placeholder for mesh data offset.
                    writer.Write((uint)luaFile.LuaScript.Length);
                }

                // GameObject section (exporters)
                foreach (PSXObjectExporter exporter in _exporters)
                {
                    // Write placeholder for mesh data offset and record its position.
                    meshOffset.OffsetPlaceholderPositions.Add(writer.BaseStream.Position);
                    writer.Write((int)0); // 4-byte placeholder for mesh data offset.

                    // Write object's transform
                    writer.Write((int)PSXTrig.ConvertCoordinateToPSX(exporter.transform.localToWorldMatrix.GetPosition().x, GTEScaling));
                    writer.Write((int)PSXTrig.ConvertCoordinateToPSX(-exporter.transform.localToWorldMatrix.GetPosition().y, GTEScaling));
                    writer.Write((int)PSXTrig.ConvertCoordinateToPSX(exporter.transform.localToWorldMatrix.GetPosition().z, GTEScaling));
                    int[,] rotationMatrix = PSXTrig.ConvertRotationToPSXMatrix(exporter.transform.rotation);

                    writer.Write((int)rotationMatrix[0, 0]);
                    writer.Write((int)rotationMatrix[0, 1]);
                    writer.Write((int)rotationMatrix[0, 2]);
                    writer.Write((int)rotationMatrix[1, 0]);
                    writer.Write((int)rotationMatrix[1, 1]);
                    writer.Write((int)rotationMatrix[1, 2]);
                    writer.Write((int)rotationMatrix[2, 0]);
                    writer.Write((int)rotationMatrix[2, 1]);
                    writer.Write((int)rotationMatrix[2, 2]);

                    writer.Write((ushort)exporter.Mesh.Triangles.Count);
                    if (exporter.LuaFile != null)
                    {
                        int index = luaFiles.IndexOf(exporter.LuaFile);
                        writer.Write((short)index);
                    }
                    else
                    {
                        writer.Write((short)-1);
                    }

                    // Write 4-byte bitfield with LSB as exporter.isActive
                    int bitfield = exporter.IsActive ? 0b1 : 0b0;
                    writer.Write(bitfield);
                }

                // Navmesh metadata section
                foreach (PSXNavMesh navmesh in _navmeshes)
                {
                    // Write placeholder for navmesh raw data offset.
                    navmeshOffset.OffsetPlaceholderPositions.Add(writer.BaseStream.Position);
                    writer.Write((int)0); // 4-byte placeholder for navmesh data offset.

                    writer.Write((ushort)navmesh.Navmesh.Count);
                    writer.Write((ushort)0);
                }

                // Atlas metadata section
                foreach (TextureAtlas atlas in _atlases)
                {
                    // Write placeholder for texture atlas raw data offset.
                    atlasOffset.OffsetPlaceholderPositions.Add(writer.BaseStream.Position);
                    writer.Write((int)0); // 4-byte placeholder for atlas data offset.

                    writer.Write((ushort)atlas.Width);
                    writer.Write((ushort)TextureAtlas.Height);
                    writer.Write((ushort)atlas.PositionX);
                    writer.Write((ushort)atlas.PositionY);
                }

                // Cluts
                foreach (TextureAtlas atlas in _atlases)
                {
                    foreach (var texture in atlas.ContainedTextures)
                    {
                        if (texture.ColorPalette != null)
                        {
                            clutOffset.OffsetPlaceholderPositions.Add(writer.BaseStream.Position);
                            writer.Write((int)0); // 4-byte placeholder for clut data offset.
                            writer.Write((ushort)texture.ClutPackingX); // 2 bytes
                            writer.Write((ushort)texture.ClutPackingY); // 2 bytes
                            writer.Write((ushort)texture.ColorPalette.Count); // 2 bytes
                            writer.Write((ushort)0); // 2 bytes
                        }
                    }
                }

                // Start of data section

                // Lua data section: Write lua file data for each exporter.
                foreach (LuaFile luaFile in luaFiles)
                {
                    AlignToFourBytes(writer);
                    // Record the current offset for this lua file's data.
                    long luaDataOffset = writer.BaseStream.Position;
                    luaOffset.DataOffsets.Add(luaDataOffset);

                    writer.Write(Encoding.UTF8.GetBytes(luaFile.LuaScript));
                }

                void writeVertexPosition(PSXVertex v)
                {
                    writer.Write((short)v.vx);
                    writer.Write((short)v.vy);
                    writer.Write((short)v.vz);
                }
                void writeVertexNormals(PSXVertex v)
                {
                    writer.Write((short)v.nx);
                    writer.Write((short)v.ny);
                    writer.Write((short)v.nz);
                }
                void writeVertexColor(PSXVertex v)
                {
                    writer.Write((byte)v.r);
                    writer.Write((byte)v.g);
                    writer.Write((byte)v.b);
                    writer.Write((byte)0); // padding
                }
                void writeVertexUV(PSXVertex v, PSXTexture2D t, int expander)
                {
                    writer.Write((byte)(v.u + t.PackingX * expander));
                    writer.Write((byte)(v.v + t.PackingY));
                }
                void foreachVertexDo(Tri tri, Action<PSXVertex> action)
                {
                    for (int i = 0; i < tri.Vertexes.Length; i++)
                    {
                        action(tri.Vertexes[i]);
                    }
                }

                // Mesh data section: Write mesh data for each exporter.
                foreach (PSXObjectExporter exporter in _exporters)
                {
                    AlignToFourBytes(writer);
                    // Record the current offset for this exporter's mesh data.
                    long meshDataOffset = writer.BaseStream.Position;
                    meshOffset.DataOffsets.Add(meshDataOffset);

                    totalFaces += exporter.Mesh.Triangles.Count;


                    foreach (Tri tri in exporter.Mesh.Triangles)
                    {
                        int expander = 16 / ((int)exporter.GetTexture(tri.TextureIndex).BitDepth);
                        // Write vertices coordinates
                        foreachVertexDo(tri, (v) => writeVertexPosition(v));

                        // Write vertex normals for v0 only
                        writeVertexNormals(tri.v0);

                        // Write vertex colors with padding
                        foreachVertexDo(tri, (v) => writeVertexColor(v));

                        // Write UVs for each vertex, adjusting for texture packing
                        foreachVertexDo(tri, (v) => writeVertexUV(v, exporter.GetTexture(tri.TextureIndex), expander));

                        writer.Write((ushort)0); // padding


                        TPageAttr tpage = new TPageAttr();
                        tpage.SetPageX(exporter.GetTexture(tri.TextureIndex).TexpageX);
                        tpage.SetPageY(exporter.GetTexture(tri.TextureIndex).TexpageY);
                        tpage.Set(exporter.GetTexture(tri.TextureIndex).BitDepth.ToColorMode());
                        tpage.SetDithering(true);
                        writer.Write((ushort)tpage.info);
                        writer.Write((ushort)exporter.GetTexture(tri.TextureIndex).ClutPackingX);
                        writer.Write((ushort)exporter.GetTexture(tri.TextureIndex).ClutPackingY);
                        writer.Write((ushort)0);
                    }
                }

                foreach (PSXNavMesh navmesh in _navmeshes)
                {
                    AlignToFourBytes(writer);
                    long navmeshDataOffset = writer.BaseStream.Position;
                    navmeshOffset.DataOffsets.Add(navmeshDataOffset);

                    foreach (PSXNavMeshTri tri in navmesh.Navmesh)
                    {
                        // Write vertices coordinates
                        writer.Write((int)tri.v0.vx);
                        writer.Write((int)tri.v0.vy);
                        writer.Write((int)tri.v0.vz);

                        writer.Write((int)tri.v1.vx);
                        writer.Write((int)tri.v1.vy);
                        writer.Write((int)tri.v1.vz);

                        writer.Write((int)tri.v2.vx);
                        writer.Write((int)tri.v2.vy);
                        writer.Write((int)tri.v2.vz);
                    }

                }

                // Atlas data section: Write raw texture data for each atlas.
                foreach (TextureAtlas atlas in _atlases)
                {
                    AlignToFourBytes(writer);
                    // Record the current offset for this atlas's data.
                    long atlasDataOffset = writer.BaseStream.Position;
                    atlasOffset.DataOffsets.Add(atlasDataOffset);

                    // Write the atlas's raw texture data.
                    for (int y = 0; y < atlas.vramPixels.GetLength(1); y++)
                    {
                        for (int x = 0; x < atlas.vramPixels.GetLength(0); x++)
                        {
                            writer.Write(atlas.vramPixels[x, y].Pack());
                        }
                    }
                }

                // Clut data section
                foreach (TextureAtlas atlas in _atlases)
                {
                    foreach (var texture in atlas.ContainedTextures)
                    {
                        if (texture.ColorPalette != null)
                        {
                            AlignToFourBytes(writer);
                            long clutDataOffset = writer.BaseStream.Position;
                            clutOffset.DataOffsets.Add(clutDataOffset);

                            foreach (VRAMPixel color in texture.ColorPalette)
                            {
                                writer.Write((ushort)color.Pack());
                            }
                        }
                    }

                }

                writeOffset(writer, luaOffset, "lua");
                writeOffset(writer, meshOffset, "mesh");
                writeOffset(writer, navmeshOffset, "navmesh");
                writeOffset(writer, atlasOffset, "atlas");
                writeOffset(writer, clutOffset, "clut");
            }
            Debug.Log(totalFaces);
        }

        private void writeOffset(BinaryWriter writer, OffsetData data, string type)
        {
            // Backfill the data offsets into the metadata section.
            if (data.OffsetPlaceholderPositions.Count == data.DataOffsets.Count)
            {
                for (int i = 0; i < data.OffsetPlaceholderPositions.Count; i++)
                {
                    writer.Seek((int)data.OffsetPlaceholderPositions[i], SeekOrigin.Begin);
                    writer.Write((int)data.DataOffsets[i]);
                }
            }
            else
            {
                Debug.LogError("Mismatch between clut offset placeholders and clut data blocks!");
            }
        }


        void AlignToFourBytes(BinaryWriter writer)
        {
            long position = writer.BaseStream.Position;
            int padding = (int)(4 - (position % 4)) % 4; // Compute needed padding
            writer.Write(new byte[padding]); // Write zero padding
        }

        void OnDrawGizmos()
        {
            Vector3 sceneOrigin = new Vector3(0, 0, 0);
            Vector3 cubeSize = new Vector3(8.0f * GTEScaling, 8.0f * GTEScaling, 8.0f * GTEScaling);
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(sceneOrigin, cubeSize);
        }

    }

    public class OffsetData
    {
        public List<long> OffsetPlaceholderPositions = new List<long>();
        public List<long> DataOffsets = new List<long>();
    }
}
