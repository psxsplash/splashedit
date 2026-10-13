using System.Collections.Generic;
using SplashEdit.RuntimeCode;
using UnityEngine;
using UnityEngine.Serialization;

namespace SplashEdit.RuntimeCode
{
    public enum PSXCollisionType
    {
        None = 0,
        Static = 1,
        Dynamic = 2
    }

    public enum VertexColorMode
    {
        BakedLighting = 0,
        FlatColor = 1,
        MeshVertexColors = 2
    }

    public enum PSXDynamicLighting
    {
        Auto = 0,  // lit at runtime if a Realtime/Mixed Point Light's range reaches it at export
        On = 1,    // always lit at runtime, e.g. a mesh that moves into lights
        Off = 2,   // never lit at runtime; every Point Light is baked instead
        [InspectorName("On (smooth)")]
        OnSmooth = 3  // always lit, per vertex rather than per triangle: smoother, about 9x the cost
    }

    public enum PSXUVMode
    {
        Exact = 0,
        [InspectorName("Half-texel bleed")]
        HalfTexelBleed = 1  // low-side vertices of each triangle move one texel down
    }

    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    [Icon("Packages/net.psxsplash.splashedit/Icons/PSXObjectExporter.png")]
    public class PSXObjectExporter : MonoBehaviour, IPSXExportable
    {
        public LuaFile LuaFile => luaFile;

        [FormerlySerializedAs("IsActive")]
        [SerializeField] private bool isActive = true;
        public bool IsActive => isActive;

        public List<PSXTexture2D> Textures { get; set; } = new List<PSXTexture2D>();
        public PSXMesh Mesh { get; protected set; }

        [FormerlySerializedAs("BitDepth")]
        [SerializeField] private PSXBPP bitDepth = PSXBPP.TEX_8BIT;

        [Tooltip("Exact maps each UV to the texel edge it sits on; the PS1 then draws the texture half a texel " +
                 "left of and above where Unity draws it. Half-texel bleed moves the vertices at each triangle's " +
                 "smallest U and V one texel down, so half of the texel outside the UV shows on every side and " +
                 "nothing inside is cut. Meant for atlas tiles with a bleed border, one tile per face.")]
        [SerializeField] private PSXUVMode uvMode = PSXUVMode.Exact;

        [SerializeField] private LuaFile luaFile;

        [FormerlySerializedAs("collisionType")]
        [SerializeField] private PSXCollisionType collisionType = PSXCollisionType.None;

        [SerializeField] private VertexColorMode vertexColorMode = VertexColorMode.BakedLighting;
        [SerializeField] private Color32 flatVertexColor = new Color32(128, 128, 128, 255);

        [Tooltip("Smooth normals for lighting. Disable for flat/faceted shading.")]
        [SerializeField] private bool smoothNormals = true;

        [Tooltip("Auto lights this mesh at runtime when a Realtime or Mixed Point Light's Range reaches it at export. " +
                 "Use On for meshes that move into lights, Off to bake Point Lights into it instead. " +
                 "Runtime lighting is one colour per triangle; On (smooth) lights each vertex instead, " +
                 "which looks smoother on big triangles and costs about nine times as much.")]
        [SerializeField] private PSXDynamicLighting dynamicLighting = PSXDynamicLighting.Auto;

        [Tooltip("Mark as platform: all boundary edges of nav regions from this mesh allow walkoff. Agent radius is not enforced at the edges.")]
        [SerializeField] private bool isPlatform = false;
        [SerializeField] private int uvOffsetMaterial = 0;

        public PSXBPP BitDepth => bitDepth;
        public PSXUVMode UVMode => uvMode;
        public PSXCollisionType CollisionType => collisionType;
        public VertexColorMode ColorMode => vertexColorMode;
        public Color32 FlatVertexColor => flatVertexColor;
        public bool SmoothNormals => smoothNormals;
        public PSXDynamicLighting DynamicLighting => dynamicLighting;

        /// <summary>Set by the scene exporter before <see cref="CreatePSXMesh"/>.</summary>
        public bool IsDynamicLit { get; set; }
        public bool IsDynamicLitSmooth => IsDynamicLit && dynamicLighting == PSXDynamicLighting.OnSmooth;
        public bool IsPlatform => isPlatform;
        public int UVOffsetMaterial => uvOffsetMaterial;

#if UNITY_6000_4_OR_NEWER
        private readonly Dictionary<(EntityId, PSXBPP), PSXTexture2D> cache = new();
#else
        private readonly Dictionary<(int, PSXBPP), PSXTexture2D> cache = new();
#endif

        public void CreatePSXTextures2D()
        {
            Renderer renderer = GetComponent<Renderer>();
            Textures.Clear();
            if (renderer == null) return;

            Material[] materials = renderer.sharedMaterials;
            foreach (Material mat in materials)
            {
                if (mat == null || mat.mainTexture == null) continue;

                Texture mainTexture = mat.mainTexture;
                Texture2D tex2D = mainTexture is Texture2D existing
                    ? existing
                    : ConvertToTexture2D(mainTexture);

                if (tex2D == null) continue;

#if UNITY_6000_4_OR_NEWER
                if (cache.TryGetValue((tex2D.GetEntityId(), bitDepth), out var cached))
#else
                if (cache.TryGetValue((tex2D.GetInstanceID(), bitDepth), out var cached))
#endif
                {
                    Textures.Add(cached);
                }
                else
                {
                    var tex = PSXTexture2D.CreateFromTexture2D(tex2D, bitDepth);
                    tex.OriginalTexture = tex2D;
#if UNITY_6000_4_OR_NEWER
                    cache.Add((tex2D.GetEntityId(), bitDepth), tex);
#else
                    cache.Add((tex2D.GetInstanceID(), bitDepth), tex);
#endif
                    Textures.Add(tex);
                }
            }
        }

        private static Texture2D ConvertToTexture2D(Texture src)
        {
            Texture2D texture2D = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);

            RenderTexture currentActiveRT = RenderTexture.active;
            RenderTexture.active = src as RenderTexture;

            texture2D.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
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

#if UNITY_EDITOR
        // Scene view: a mesh reached by more runtime Point Lights than the PS1 can
        // apply gets a red box and a label, drawn like the room/portal previews.
        void OnDrawGizmos()
        {
            var scene = FindFirstObjectByType<PSXSceneExporter>();
            if (scene != null && !scene.PreviewPointLights) return;
            var ml = PSXPointLightExporter.Analyze(this, PSXPointLightExporter.CollectCached());
            if (!ml.OverCap || !PSXPointLightExporter.TryGetWorldBounds(this, out Bounds b)) return;
            Gizmos.color = new Color(1f, 0.25f, 0.1f, 0.15f);
            Gizmos.DrawCube(b.center, b.size);
            Gizmos.color = new Color(1f, 0.25f, 0.1f, 0.8f);
            Gizmos.DrawWireCube(b.center, b.size);
            UnityEditor.Handles.Label(b.center,
                $"{ml.Reaching.Count} point lights, PS1 uses {PSXPointLightExporter.MaxLightsPerMesh}",
                new GUIStyle { normal = { textColor = new Color(1f, 0.4f, 0.3f) } });
        }

        // Selected: a line to each runtime Point Light that reaches the mesh,
        // yellow for the ones the PS1 applies and red for the ones it drops.
        void OnDrawGizmosSelected()
        {
            var scene = FindFirstObjectByType<PSXSceneExporter>();
            if (scene != null && !scene.PreviewPointLights) return;
            var ml = PSXPointLightExporter.Analyze(this, PSXPointLightExporter.CollectCached());
            if (!ml.RuntimeLit || !PSXPointLightExporter.TryGetWorldBounds(this, out Bounds b)) return;
            int i = 0;
            foreach (Light l in ml.Reaching)
            {
                Gizmos.color = i++ < PSXPointLightExporter.MaxLightsPerMesh ? new Color(1f, 0.85f, 0.2f, 0.8f)
                                                                              : new Color(1f, 0.25f, 0.1f, 0.8f);
                Gizmos.DrawLine(b.center, l.transform.position);
            }
        }
#endif

        public void CreatePSXMesh(float GTEScaling)
        {
            Renderer renderer = GetComponent<Renderer>();
            if (renderer != null)
            {
                Mesh = PSXMesh.CreateFromUnityRenderer(renderer, GTEScaling, transform, Textures, vertexColorMode, flatVertexColor, smoothNormals,
                    bakePointLights: !IsDynamicLit, uvMode: uvMode);
            }
        }
    }
}