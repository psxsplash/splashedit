using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SplashEdit.RuntimeCode
{

    [ExecuteInEditMode]
    public class PSXSceneExporter : MonoBehaviour
    {
        private PSXObjectExporter[] _exporters;

        private PSXData _psxData;
        private readonly string _psxDataPath = "Assets/PSXData.asset";

        private Vector2 selectedResolution;
        private bool dualBuffering;
        private bool verticalLayout;
        private List<ProhibitedArea> prohibitedAreas;
        private VRAMPixel[,] vramPixels;



        public void Export()
        {
            _psxData = Utils.LoadData(out selectedResolution, out dualBuffering, out verticalLayout, out prohibitedAreas);
            _exporters = FindObjectsByType<PSXObjectExporter>(FindObjectsSortMode.None);
            foreach (PSXObjectExporter exp in _exporters)
            {
                exp.CreatePSXTexture2D();
                exp.CreatePSXMesh();
            }
            PackTextures();
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
            vramPixels = packed._vramPixels;

        }

        void ExportFile()
        {
            string path = EditorUtility.SaveFilePanel("Select Output File", "", "output", "bin");
            int totalFaces = 0;
            using (BinaryWriter writer = new BinaryWriter(File.Open(path, FileMode.Create)))
            {
                // VramPixels are always 1MB
                for (int y = 0; y < vramPixels.GetLength(1); y++)
                {
                    for (int x = 0; x < vramPixels.GetLength(0); x++)
                    {
                        writer.Write(vramPixels[x, y].Pack());
                    }
                }
                writer.Write((ushort)_exporters.Length);
                foreach (PSXObjectExporter exporter in _exporters)
                {

                    int expander = 16 / ((int)exporter.Texture.BitDepth);

                    totalFaces += exporter.Mesh.Triangles.Count;
                    writer.Write((ushort)exporter.Mesh.Triangles.Count);
                    writer.Write((byte)exporter.Texture.BitDepth);
                    writer.Write((byte)exporter.Texture.TexpageX);
                    writer.Write((byte)exporter.Texture.TexpageY);
                    writer.Write((ushort)exporter.Texture.ClutPackingX);
                    writer.Write((ushort)exporter.Texture.ClutPackingY);
                    writer.Write((byte)0);
                    void writePSXVertex(PSXVertex vertex)
                    {
                        writer.Write((short)vertex.vx);
                        writer.Write((short)vertex.vy);
                        writer.Write((short)vertex.vz);
                        writer.Write((short)vertex.nx);
                        writer.Write((short)vertex.ny);
                        writer.Write((short)vertex.nz);
                        writer.Write((byte)(vertex.u + exporter.Texture.PackingX * expander));
                        writer.Write((byte)(vertex.v + exporter.Texture.PackingY));
                        writer.Write((byte)vertex.r);
                        writer.Write((byte)vertex.g);
                        writer.Write((byte)vertex.b);
                        for (int i = 0; i < 7; i++) writer.Write((byte)0);
                    }
                    foreach (Tri tri in exporter.Mesh.Triangles)
                    {
                        writePSXVertex(tri.v0);
                        writePSXVertex(tri.v1);
                        writePSXVertex(tri.v2);
                    }
                }
            }
            Debug.Log(totalFaces);
        }

        void OnDrawGizmos()
        {
            Gizmos.DrawIcon(transform.position, "Packages/net.psxsplash.splashedit/Icons/PSXSceneExporter.png", true);
        }
    }
}
