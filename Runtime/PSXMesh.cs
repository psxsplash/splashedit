using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SplashEdit.RuntimeCode
{
    /// <summary>
    /// Represents a vertex formatted for the PSX (PlayStation) style rendering.
    /// </summary>
    public struct PSXVertex
    {
        // Position components in fixed-point format.
        public short vx, vy, vz;
        // Normal vector components in fixed-point format.
        public short nx, ny, nz;
        // Texture coordinates.
        public byte u, v;
        // Vertex color components.
        public byte r, g, b;
    }

    /// <summary>
    /// Represents a triangle defined by three PSX vertices.
    /// </summary>
    public struct Tri
    {
        public PSXVertex v0;
        public PSXVertex v1;
        public PSXVertex v2;

        /// <summary>
        /// Index into the texture list for this triangle's material.
        /// -1 means untextured (vertex-color only, rendered as POLY_G3).
        /// </summary>
        public int TextureIndex;
        
        /// <summary>
        /// Whether this triangle is untextured (vertex-color only).
        /// Untextured triangles are rendered as GouraudTriangle (POLY_G3) on PS1.
        /// </summary>
        public bool IsUntextured => TextureIndex == -1;
    }

    /// <summary>
    /// A mesh structure that holds a list of triangles converted from a Unity mesh into the PSX format.
    /// </summary>
    [System.Serializable]
    public class PSXMesh
    {
        public List<Tri> Triangles;

        internal static Vector3[] RecalculateSmoothNormals(Mesh mesh)
        {
            Vector3[] normals = new Vector3[mesh.vertexCount];
            Dictionary<Vector3, List<int>> vertexMap = new Dictionary<Vector3, List<int>>();

            for (int i = 0; i < mesh.vertexCount; i++)
            {
                Vector3 vertex = mesh.vertices[i];
                if (!vertexMap.ContainsKey(vertex))
                {
                    vertexMap[vertex] = new List<int>();
                }
                vertexMap[vertex].Add(i);
            }

            foreach (var kvp in vertexMap)
            {
                Vector3 smoothNormal = Vector3.zero;
                foreach (int index in kvp.Value)
                {
                    smoothNormal += mesh.normals[index];
                }
                smoothNormal.Normalize();

                foreach (int index in kvp.Value)
                {
                    normals[index] = smoothNormal;
                }
            }

            return normals;
        }


        /// <summary>
        /// Creates a PSXMesh from a Unity Renderer by extracting its mesh and materials.
        /// </summary>
        /// <param name="bakePointLights">False when the mesh is lit at runtime, which leaves runtime (Realtime or Mixed) point lights out of the bake.</param>
        public static PSXMesh CreateFromUnityRenderer(Renderer renderer, float GTEScaling, Transform transform, List<PSXTexture2D> textures,
            VertexColorMode colorMode = VertexColorMode.BakedLighting, Color32? flatColor = null, bool smoothNormals = true,
            bool bakePointLights = true)
        {
            Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            return BuildFromMesh(mesh, renderer, GTEScaling, transform, textures, colorMode, flatColor, smoothNormals, bakePointLights);
        }

        /// <summary>
        /// Creates a PSXMesh from a supplied Unity Mesh with the renderer's materials.
        /// </summary>
        public static PSXMesh CreateFromUnityMesh(Mesh mesh, Renderer renderer, float GTEScaling, Transform transform, List<PSXTexture2D> textures,
            VertexColorMode colorMode = VertexColorMode.BakedLighting, Color32? flatColor = null, bool smoothNormals = true)
        {
            return BuildFromMesh(mesh, renderer, GTEScaling, transform, textures, colorMode, flatColor, smoothNormals);
        }

        private static PSXMesh BuildFromMesh(Mesh mesh, Renderer renderer, float GTEScaling, Transform transform, List<PSXTexture2D> textures,
            VertexColorMode colorMode = VertexColorMode.BakedLighting, Color32? flatColor = null, bool smoothNormals = true,
            bool bakePointLights = true)
        {
            PSXMesh psxMesh = new PSXMesh { Triangles = new List<Tri>() };
            Material[] materials = renderer.sharedMaterials;

            // Guard: only recalculate normals if missing
            if (mesh.normals == null || mesh.normals.Length == 0)
                mesh.RecalculateNormals();

            if (mesh.uv == null || mesh.uv.Length == 0)
                mesh.uv = new Vector2[mesh.vertices.Length];

            Vector3[] normalsForLighting = smoothNormals ? RecalculateSmoothNormals(mesh) : mesh.normals;

            // Cache lights once for the entire mesh (only needed for baked lighting)
            Light[] sceneLights = colorMode == VertexColorMode.BakedLighting
                ? Object.FindObjectsByType<Light>(FindObjectsSortMode.None)
                    .Where(l => l.enabled && (bakePointLights || !PSXPointLightExporter.IsRuntime(l))).ToArray()
                : null;

            // Mesh vertex colors (only for MeshVertexColors mode)
            Color[] meshColors = colorMode == VertexColorMode.MeshVertexColors ? mesh.colors : null;
            bool hasMeshColors = meshColors != null && meshColors.Length == mesh.vertexCount;

            // Resolved flat color
            Color32 resolvedFlat = flatColor ?? new Color32(128, 128, 128, 255);

            // Precompute world positions and normals for all vertices
            Vector3[] worldVertices = new Vector3[mesh.vertices.Length];
            Vector3[] worldNormals = new Vector3[mesh.normals.Length];
            for (int i = 0; i < mesh.vertices.Length; i++)
            {
                worldVertices[i] = transform.TransformPoint(mesh.vertices[i]);
                worldNormals[i] = transform.TransformDirection(normalsForLighting[i]).normalized;
            }

            for (int submeshIndex = 0; submeshIndex < mesh.subMeshCount; submeshIndex++)
            {
                int materialIndex = Mathf.Min(submeshIndex, materials.Length - 1);
                Material material = materials[materialIndex];
                Texture2D texture = material != null ? material.mainTexture as Texture2D : null;

                int textureIndex = -1;
                if (texture != null)
                {
                    for (int i = 0; i < textures.Count; i++)
                    {
                        if (textures[i].OriginalTexture == texture)
                        {
                            textureIndex = i;
                            break;
                        }
                    }
                }

                int[] submeshTriangles = mesh.GetTriangles(submeshIndex);
                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                Vector2[] uv = mesh.uv;

                PSXVertex convertData(int index)
                {
                    Vector3 v = Vector3.Scale(vertices[index], transform.lossyScale);
                    Color c;

                    switch (colorMode)
                    {
                        case VertexColorMode.FlatColor:
                            c = new Color(resolvedFlat.r / 255f, resolvedFlat.g / 255f, resolvedFlat.b / 255f);
                            break;
                        case VertexColorMode.MeshVertexColors:
                            c = hasMeshColors ? meshColors[index] : new Color(0.5f, 0.5f, 0.5f);
                            break;
                        default: // BakedLighting
                        {
                            Vector3 wv = worldVertices[index];
                            Vector3 wn = worldNormals[index];
                            c = PSXLightingBaker.ComputeLighting(wv, wn, sceneLights);
                            break;
                        }
                    }

                    if (textureIndex == -1)
                    {
                        Color matColor = Color.white;
                        if (material != null)
                        {
                            if (material.HasProperty("_BaseColor"))
                                matColor = material.GetColor("_BaseColor");
                            else if (material.HasProperty("_Color"))
                                matColor = material.color;
                        }
                        c = new Color(c.r * matColor.r, c.g * matColor.g, c.b * matColor.b);
                        return ConvertToPSXVertex(v, GTEScaling, normals[index], Vector2.zero, null, null, c);
                    }

                    return ConvertToPSXVertex(v, GTEScaling, normals[index], uv[index],
                        textures[textureIndex]?.Width, textures[textureIndex]?.Height, c);
                }

                for (int i = 0; i < submeshTriangles.Length; i += 3)
                {
                    int vid0 = submeshTriangles[i];
                    int vid1 = submeshTriangles[i + 1];
                    int vid2 = submeshTriangles[i + 2];

                    Vector3 faceNormal = Vector3.Cross(vertices[vid1] - vertices[vid0], vertices[vid2] - vertices[vid0]).normalized;

                    if (Vector3.Dot(faceNormal, normals[vid0]) < 0)
                    {
                        (vid1, vid2) = (vid2, vid1);
                    }

                    psxMesh.Triangles.Add(new Tri
                    {
                        v0 = convertData(vid0),
                        v1 = convertData(vid1),
                        v2 = convertData(vid2),
                        TextureIndex = textureIndex
                    });
                }
            }

            return psxMesh;
        }

        /// <summary>
        /// Converts a Unity vertex into a PSXVertex by applying fixed-point conversion, shading, and UV mapping.
        /// </summary>
        /// <param name="vertex">The position of the vertex.</param>
        /// <param name="GTEScaling">World-to-GTE scaling factor.</param>
        /// <param name="normal">The normal vector at the vertex.</param>
        /// <param name="uv">Texture coordinates for the vertex.</param>
        /// <param name="textureWidth">Width of the texture for UV scaling.</param>
        /// <param name="textureHeight">Height of the texture for UV scaling.</param>
        /// <param name="color">Pre-computed vertex color from lighting.</param>
        /// <returns>A PSXVertex with converted coordinates, normals, UVs, and color.</returns>
        private static PSXVertex ConvertToPSXVertex(Vector3 vertex, float GTEScaling, Vector3 normal, Vector2 uv, int? textureWidth, int? textureHeight, Color color)
        {
            int width = textureWidth ?? 0;
            int height = textureHeight ?? 0;
            PSXVertex psxVertex = new PSXVertex
            {
                // Convert position to fixed-point, clamping values to a defined range.
                vx = PSXTrig.ConvertCoordinateToPSX(vertex.x, GTEScaling),
                vy = PSXTrig.ConvertCoordinateToPSX(-vertex.y, GTEScaling),
                vz = PSXTrig.ConvertCoordinateToPSX(vertex.z, GTEScaling),

                // Convert normals to fixed-point.
                nx = PSXTrig.ConvertCoordinateToPSX(normal.x),
                ny = PSXTrig.ConvertCoordinateToPSX(-normal.y),
                nz = PSXTrig.ConvertCoordinateToPSX(normal.z),

                // Unity UVs run from texel edge to texel edge, and the GPU samples
                // texel u at integer u and leaves out the right and bottom edge, so
                // k/width maps to texel k and 1.0 to width.
                u = (byte)Mathf.Clamp(Mathf.RoundToInt(uv.x * width), 0, 255),
                v = (byte)Mathf.Clamp(Mathf.RoundToInt((1.0f - uv.y) * height), 0, 255),

                // Apply lighting to the colors.
                r = Utils.ColorUnityToPSX(color.r),
                g = Utils.ColorUnityToPSX(color.g),
                b = Utils.ColorUnityToPSX(color.b),
            };

            return psxVertex;
        }
    }
}
