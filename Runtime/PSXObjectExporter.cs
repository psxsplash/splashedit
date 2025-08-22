using System.Collections.Generic;
using Splashedit.RuntimeCode;
using UnityEngine;
using UnityEngine.Serialization;

namespace SplashEdit.RuntimeCode
{
    [RequireComponent(typeof(Renderer))]
    public class PSXObjectExporter : MonoBehaviour
    {
        public LuaFile LuaFile => luaFile;

        public bool IsActive = true;

        public List<PSXTexture2D> Textures { get; set; } = new List<PSXTexture2D>(); // Stores the converted PlayStation-style texture
        public PSXMesh Mesh { get; protected set; } // Stores the converted PlayStation-style mesh
        [Header("Export Settings")]
        [FormerlySerializedAs("BitDepth")]
        [SerializeField] private PSXBPP bitDepth = PSXBPP.TEX_8BIT; // Defines the bit depth of the texture (e.g., 4BPP, 8BPP)
        [SerializeField] private LuaFile luaFile;
        [Header("Gizmo Settings")]
        [FormerlySerializedAs("PreviewNormals")]
        [SerializeField] private bool previewNormals = false;
        [SerializeField] private float normalPreviewLength = 0.5f; // Length of the normal lines

        private readonly Dictionary<(int, PSXBPP), PSXTexture2D> cache = new();

        private void OnDrawGizmos()
        {
            if (previewNormals)
            {
                MeshFilter filter = GetComponent<MeshFilter>();

                if (filter != null)
                {
                    Mesh mesh = filter.sharedMesh;
                    Vector3[] vertices = mesh.vertices;
                    Vector3[] normals = mesh.normals;

                    Gizmos.color = Color.green; // Normal color

                    for (int i = 0; i < vertices.Length; i++)
                    {
                        Vector3 worldVertex = transform.TransformPoint(vertices[i]); // Convert to world space
                        Vector3 worldNormal = transform.TransformDirection(normals[i]); // Transform normal to world space

                        Gizmos.DrawLine(worldVertex, worldVertex + worldNormal * normalPreviewLength);
                    }
                }

            }
        }


        /// <summary>
        /// Converts the object's material texture into a PlayStation-compatible texture.
        /// </summary>
        /// 
        public void CreatePSXTextures2D()
        {
            Renderer renderer = GetComponent<Renderer>();
            Textures.Clear();
            if (renderer != null)
            {
                Material[] materials = renderer.sharedMaterials;

                foreach (Material mat in materials)
                {
                    if (mat != null && mat.mainTexture != null)
                    {
                        Texture mainTexture = mat.mainTexture;
                        Texture2D tex2D = null;

                        // Check if it's already a Texture2D
                        if (mainTexture is Texture2D existingTex2D)
                        {
                            tex2D = existingTex2D;
                        }
                        else
                        {
                            // If not a Texture2D, try to convert
                            tex2D = ConvertToTexture2D(mainTexture);
                        }

                        if (tex2D != null)
                        {
                            PSXTexture2D tex;
                            if (cache.ContainsKey((tex2D.GetInstanceID(), bitDepth)))
                            {
                                tex = cache[(tex2D.GetInstanceID(), bitDepth)];
                            }
                            else
                            {
                                tex = PSXTexture2D.CreateFromTexture2D(tex2D, bitDepth);
                                tex.OriginalTexture = tex2D; // Store reference to the original texture
                                cache.Add((tex2D.GetInstanceID(), bitDepth), tex);
                            }
                            Textures.Add(tex);
                        }
                    }
                }
            }
        }

        private Texture2D ConvertToTexture2D(Texture texture)
        {
            // Create a new Texture2D with the same dimensions and format
            Texture2D texture2D = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);

            // Read the texture pixels
            RenderTexture currentActiveRT = RenderTexture.active;
            RenderTexture.active = texture as RenderTexture;

            texture2D.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            texture2D.Apply();

            RenderTexture.active = currentActiveRT;

            return texture2D;
        }

        public PSXTexture2D GetTexture(int index)
        {
            if (index >= 0 && index < Textures.Count)
            {
                return Textures[index];
            }
            return null;
        }

        /// <summary>
        /// Converts the object's mesh into a PlayStation-compatible mesh.
        /// </summary>
        public void CreatePSXMesh(float GTEScaling)
        {
            Renderer renderer = GetComponent<Renderer>();
            if (renderer != null)
            {
                Mesh = PSXMesh.CreateFromUnityRenderer(renderer, GTEScaling, transform, Textures);
            }
        }
    }
}
