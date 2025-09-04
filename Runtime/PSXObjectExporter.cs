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

        public List<PSXTexture2D> Textures { get; set; } = new List<PSXTexture2D>();
        public PSXMesh Mesh { get; protected set; }
        
        [Header("Export Settings")]
        [FormerlySerializedAs("BitDepth")]
        [SerializeField] private PSXBPP bitDepth = PSXBPP.TEX_8BIT;
        [SerializeField] private LuaFile luaFile;
        
        [Header("BSP Settings")]
        [SerializeField] private Mesh _modifiedMesh; // Mesh after BSP processing
        
        [Header("Gizmo Settings")]
        [FormerlySerializedAs("PreviewNormals")]
        [SerializeField] private bool previewNormals = false;
        [SerializeField] private float normalPreviewLength = 0.5f;

        private readonly Dictionary<(int, PSXBPP), PSXTexture2D> cache = new();

        public Mesh ModifiedMesh 
        {
            get => _modifiedMesh;
            set => _modifiedMesh = value;
        }

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

                    Gizmos.color = Color.green;

                    for (int i = 0; i < vertices.Length; i++)
                    {
                        Vector3 worldVertex = transform.TransformPoint(vertices[i]);
                        Vector3 worldNormal = transform.TransformDirection(normals[i]);

                        Gizmos.DrawLine(worldVertex, worldVertex + worldNormal * normalPreviewLength);
                    }
                }
            }
        }

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

                        if (mainTexture is Texture2D existingTex2D)
                        {
                            tex2D = existingTex2D;
                        }
                        else
                        {
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
                                tex.OriginalTexture = tex2D;
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
            Texture2D texture2D = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);

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

        public void CreatePSXMesh(float GTEScaling, bool useBSP = false)
        {
            Renderer renderer = GetComponent<Renderer>();
            if (renderer != null)
            {
                if (useBSP && _modifiedMesh != null)
                {
                    // Create a temporary GameObject with the modified mesh but same materials
                    GameObject tempGO = new GameObject("TempBSPMesh");
                    tempGO.transform.position = transform.position;
                    tempGO.transform.rotation = transform.rotation;
                    tempGO.transform.localScale = transform.localScale;
                    
                    MeshFilter tempMF = tempGO.AddComponent<MeshFilter>();
                    tempMF.sharedMesh = _modifiedMesh;
                    
                    MeshRenderer tempMR = tempGO.AddComponent<MeshRenderer>();
                    tempMR.sharedMaterials = renderer.sharedMaterials;
                    
                    Mesh = PSXMesh.CreateFromUnityRenderer(tempMR, GTEScaling, transform, Textures);
                    
                    // Clean up
                    GameObject.DestroyImmediate(tempGO);
                }
                else
                {
                    Mesh = PSXMesh.CreateFromUnityRenderer(renderer, GTEScaling, transform, Textures);
                }
            }
        }
    }
}