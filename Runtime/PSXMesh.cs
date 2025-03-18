using System.Collections.Generic;
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
    }

    /// <summary>
    /// A mesh structure that holds a list of triangles converted from a Unity mesh into the PSX format.
    /// </summary>
    [System.Serializable]
    public class PSXMesh
    {
        public List<Tri> Triangles;

        /// <summary>
        /// Creates a PSXMesh from a Unity Mesh by converting its vertices, normals, UVs, and applying shading.
        /// </summary>
        /// <param name="mesh">The Unity mesh to convert.</param>
        /// <param name="textureWidth">Width of the texture (default is 256).</param>
        /// <param name="textureHeight">Height of the texture (default is 256).</param>
        /// <param name="transform">Optional transform to convert vertices to world space.</param>
        /// <returns>A new PSXMesh containing the converted triangles.</returns>
        public static PSXMesh CreateFromUnityMesh(Mesh mesh, int textureWidth = 256, int textureHeight = 256, Transform transform = null)
        {
            PSXMesh psxMesh = new PSXMesh { Triangles = new List<Tri>() };

            // Get mesh data arrays.
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uv = mesh.uv;
            int[] indices = mesh.triangles;

            // Determine the primary light's direction and color for shading.
            Light mainLight = RenderSettings.sun;
            Vector3 lightDir = mainLight ? mainLight.transform.forward : Vector3.down; // Fixed: Removed negation.
            Color lightColor = mainLight ? mainLight.color * mainLight.intensity : Color.white;

            // Iterate over each triangle (group of 3 indices).
            for (int i = 0; i < indices.Length; i += 3)
            {
                int vid0 = indices[i];
                int vid1 = indices[i + 1];
                int vid2 = indices[i + 2];

                // Transform vertices to world space if a transform is provided.
                Vector3 v0 = transform ? transform.TransformPoint(vertices[vid0]) : vertices[vid0];
                Vector3 v1 = transform ? transform.TransformPoint(vertices[vid1]) : vertices[vid1];
                Vector3 v2 = transform ? transform.TransformPoint(vertices[vid2]) : vertices[vid2];

                // Convert vertices to PSX format including fixed-point conversion and shading.
                PSXVertex psxV0 = ConvertToPSXVertex(v0, normals[vid0], uv[vid0], lightDir, lightColor, textureWidth, textureHeight);
                PSXVertex psxV1 = ConvertToPSXVertex(v1, normals[vid1], uv[vid1], lightDir, lightColor, textureWidth, textureHeight);
                PSXVertex psxV2 = ConvertToPSXVertex(v2, normals[vid2], uv[vid2], lightDir, lightColor, textureWidth, textureHeight);

                // Add the constructed triangle to the mesh.
                psxMesh.Triangles.Add(new Tri { v0 = psxV0, v1 = psxV1, v2 = psxV2 });
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
        private static PSXVertex ConvertToPSXVertex(Vector3 vertex, Vector3 normal, Vector2 uv, Vector3 lightDir, Color lightColor, int textureWidth, int textureHeight)
        {
            // Calculate light intensity based on the angle between the normalized normal and light direction.
            float lightIntensity = Mathf.Clamp01(Vector3.Dot(normal.normalized, lightDir));
            // Remap the intensity to a specific range for a softer shading effect.
            lightIntensity = Mathf.Lerp(0.4f, 0.7f, lightIntensity);

            // Compute the final shaded color by multiplying the light color by the intensity.
            Color shadedColor = lightColor * lightIntensity;

            static short clampPosition(float v) => (short)(Mathf.Clamp(v, -4f, 3.999f) * 4096);
            static byte clamp0255(float v) => (byte)(Mathf.Clamp(v, 0, 255));
            PSXVertex psxVertex = new PSXVertex
            {
                // Convert position to fixed-point, clamping values to a defined range.
                vx = clampPosition(vertex.x),
                vy = clampPosition(-vertex.y),
                vz = clampPosition(vertex.z),

                // Convert normals to fixed-point.
                nx = clampPosition(normal.x),
                ny = clampPosition(-normal.y),
                nz = clampPosition(normal.z),

                // Map UV coordinates to a byte range after scaling based on texture dimensions.
                u = clamp0255(uv.x * (textureWidth - 1)),
                v = clamp0255((1.0f - uv.y) * (textureHeight - 1)),

                // Convert the computed color to a byte range.
                r = clamp0255(shadedColor.r * 255),
                g = clamp0255(shadedColor.g * 255),
                b = clamp0255(shadedColor.b * 255)
            };

            return psxVertex;
        }
    }
}
