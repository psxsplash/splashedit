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

        public int TextureIndex;
        public readonly PSXVertex[] Vertexes => new PSXVertex[] { v0, v1, v2 };
    }

    /// <summary>
    /// A mesh structure that holds a list of triangles converted from a Unity mesh into the PSX format.
    /// </summary>
    [System.Serializable]
    public class PSXMesh
    {
        public List<Tri> Triangles;

        private static Vector3[] RecalculateSmoothNormals(Mesh mesh)
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
        /// Creates a PSXMesh from a Unity Mesh by converting its vertices, normals, UVs, and applying shading.
        /// </summary>
        /// <param name="mesh">The Unity mesh to convert.</param>
        /// <param name="textureWidth">Width of the texture (default is 256).</param>
        /// <param name="textureHeight">Height of the texture (default is 256).</param>
        /// <param name="transform">Optional transform to convert vertices to world space.</param>
        /// <returns>A new PSXMesh containing the converted triangles.</returns>
        public static PSXMesh CreateFromUnityRenderer(Renderer renderer, float GTEScaling, Transform transform, List<PSXTexture2D> textures)
        {
            PSXMesh psxMesh = new PSXMesh { Triangles = new List<Tri>() };
            Material[] materials = renderer.sharedMaterials;
            Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;

            for (int submeshIndex = 0; submeshIndex < materials.Length; submeshIndex++)
            {
                int[] submeshTriangles = mesh.GetTriangles(submeshIndex);
                Material material = materials[submeshIndex];
                Texture2D texture = material.mainTexture as Texture2D;

                // Find texture index instead of the texture itself
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

                if (textureIndex == -1)
                {
                    continue;
                }

                // Get mesh data arrays
                mesh.RecalculateNormals();
                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                Vector3[] smoothNormals = RecalculateSmoothNormals(mesh);
                Vector2[] uv = mesh.uv;

                PSXVertex convertData(int index)
                {
                    Vector3 v = Vector3.Scale(vertices[index], transform.lossyScale);
                    Vector3 wv = transform.TransformPoint(vertices[index]);
                    Vector3 wn = transform.TransformDirection(smoothNormals[index]).normalized;
                    Color c = PSXLightingBaker.ComputeLighting(wv, wn);
                    return ConvertToPSXVertex(v, GTEScaling, normals[index], uv[index], textures[textureIndex]?.Width, textures[textureIndex]?.Height, c);
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

        public static PSXMesh CreateFromUnityMesh(Mesh mesh, Renderer renderer, float GTEScaling, Transform transform, List<PSXTexture2D> textures)
        {
            PSXMesh psxMesh = new PSXMesh { Triangles = new List<Tri>() };
            Material[] materials = renderer.sharedMaterials;

            // Ensure mesh has required data
            if (mesh.normals == null || mesh.normals.Length == 0)
            {
                mesh.RecalculateNormals();
            }

            if (mesh.uv == null || mesh.uv.Length == 0)
            {
                Vector2[] uvs = new Vector2[mesh.vertices.Length];
                mesh.uv = uvs;
            }

            // Precompute smooth normals for the entire mesh
            Vector3[] smoothNormals = RecalculateSmoothNormals(mesh);

            // Precompute world positions and normals for all vertices
            Vector3[] worldVertices = new Vector3[mesh.vertices.Length];
            Vector3[] worldNormals = new Vector3[mesh.normals.Length];

            for (int i = 0; i < mesh.vertices.Length; i++)
            {
                worldVertices[i] = transform.TransformPoint(mesh.vertices[i]);
                worldNormals[i] = transform.TransformDirection(mesh.normals[i]).normalized;
            }

            for (int submeshIndex = 0; submeshIndex < mesh.subMeshCount; submeshIndex++)
            {
                int materialIndex = Mathf.Min(submeshIndex, materials.Length - 1);
                Material material = materials[materialIndex];
                Texture2D texture = material.mainTexture as Texture2D;

                // Find texture index
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

                // Get mesh data arrays
                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                Vector2[] uv = mesh.uv;

                PSXVertex convertData(int index)
                {
                    Vector3 v = Vector3.Scale(vertices[index], transform.lossyScale);

                    // Use precomputed world position and normal for consistent lighting
                    Vector3 wv = worldVertices[index];
                    Vector3 wn = worldNormals[index];

                    // For split triangles, use the original vertex's lighting if possible
                    Color c = PSXLightingBaker.ComputeLighting(wv, wn);

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
        /// <param name="normal">The normal vector at the vertex.</param>
        /// <param name="uv">Texture coordinates for the vertex.</param>
        /// <param name="lightDir">The light direction used for shading calculations.</param>
        /// <param name="lightColor">The color of the light affecting the vertex.</param>
        /// <param name="textureWidth">Width of the texture for UV scaling.</param>
        /// <param name="textureHeight">Height of the texture for UV scaling.</param>
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

                // Map UV coordinates to a byte range after scaling based on texture dimensions.
                u = (byte)Mathf.Clamp(uv.x * (width - 1), 0, 255),
                v = (byte)Mathf.Clamp((1.0f - uv.y) * (height - 1), 0, 255),

                // Apply lighting to the colors.
                r = Utils.ColorUnityToPSX(color.r),
                g = Utils.ColorUnityToPSX(color.g),
                b = Utils.ColorUnityToPSX(color.b),
            };

            return psxVertex;
        }
    }
}
