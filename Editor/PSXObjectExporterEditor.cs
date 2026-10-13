using UnityEngine;
using UnityEditor;
using SplashEdit.RuntimeCode;
using System.Linq;

namespace SplashEdit.EditorCode
{
    [CustomEditor(typeof(PSXObjectExporter))]
    [CanEditMultipleObjects]
    public class PSXObjectExporterEditor : UnityEditor.Editor
    {
        private SerializedProperty isActiveProp;
        private SerializedProperty bitDepthProp;
        private SerializedProperty uvModeProp;
        private SerializedProperty luaFileProp;
        private SerializedProperty collisionTypeProp;
        private SerializedProperty vertexColorModeProp;
        private SerializedProperty flatVertexColorProp;
        private SerializedProperty smoothNormalsProp;
        private SerializedProperty dynamicLightingProp;
        private SerializedProperty isPlatformProp;
        private SerializedProperty uvOffsetMaterialProp;

        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private int triangleCount;
        private int vertexCount;

        private bool showExport = true;
        private bool showCollision = true;

        private void OnEnable()
        {
            isActiveProp = serializedObject.FindProperty("isActive");
            bitDepthProp = serializedObject.FindProperty("bitDepth");
            uvModeProp = serializedObject.FindProperty("uvMode");
            luaFileProp = serializedObject.FindProperty("luaFile");
            collisionTypeProp = serializedObject.FindProperty("collisionType");
            vertexColorModeProp = serializedObject.FindProperty("vertexColorMode");
            flatVertexColorProp = serializedObject.FindProperty("flatVertexColor");
            smoothNormalsProp = serializedObject.FindProperty("smoothNormals");
            dynamicLightingProp = serializedObject.FindProperty("dynamicLighting");
            isPlatformProp = serializedObject.FindProperty("isPlatform");
            uvOffsetMaterialProp = serializedObject.FindProperty("uvOffsetMaterial");

            CacheMeshInfo();
        }

        private void CacheMeshInfo()
        {
            var exporter = target as PSXObjectExporter;
            if (exporter == null) return;
            meshFilter = exporter.GetComponent<MeshFilter>();
            meshRenderer = exporter.GetComponent<MeshRenderer>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                triangleCount = meshFilter.sharedMesh.triangles.Length / 3;
                vertexCount = meshFilter.sharedMesh.vertexCount;
            }
        }

        // The lighting status follows lights the user drags around the scene.
        public override bool RequiresConstantRepaint() => PSXPointLightExporter.CollectCached().Length > 0;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawHeader();
            EditorGUILayout.Space(4);

            if (!isActiveProp.boolValue)
            {
                EditorGUILayout.LabelField("Object will be skipped during export.", PSXEditorStyles.InfoBox);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            DrawMeshSummary();
            PSXEditorStyles.DrawSeparator(6, 6);
            DrawExportSection();
            PSXEditorStyles.DrawSeparator(6, 6);
            DrawCollisionSection();
            PSXEditorStyles.DrawSeparator(6, 6);
            DrawActions();

            serializedObject.ApplyModifiedProperties();
        }

        private new void DrawHeader()
        {
            EditorGUILayout.BeginVertical(PSXEditorStyles.CardStyle);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(isActiveProp, GUIContent.none, GUILayout.Width(18));
            var exporter = target as PSXObjectExporter;
            EditorGUILayout.LabelField(exporter.gameObject.name, PSXEditorStyles.CardHeaderStyle);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawMeshSummary()
        {
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                EditorGUILayout.LabelField("No mesh on this object.", PSXEditorStyles.InfoBox);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"{triangleCount} tris", PSXEditorStyles.RichLabel, GUILayout.Width(60));
            EditorGUILayout.LabelField($"{vertexCount} verts", PSXEditorStyles.RichLabel, GUILayout.Width(70));

            int subMeshCount = meshFilter.sharedMesh.subMeshCount;
            if (subMeshCount > 1)
                EditorGUILayout.LabelField($"{subMeshCount} submeshes", PSXEditorStyles.RichLabel, GUILayout.Width(90));

            int matCount = meshRenderer != null ? meshRenderer.sharedMaterials.Length : 0;
            int textured = meshRenderer != null
                ? meshRenderer.sharedMaterials.Count(m => m != null && m.mainTexture != null)
                : 0;
            if (textured > 0)
                EditorGUILayout.LabelField($"{textured}/{matCount} textured", PSXEditorStyles.RichLabel);
            else
                EditorGUILayout.LabelField("untextured", PSXEditorStyles.RichLabel);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawExportSection()
        {
            showExport = EditorGUILayout.Foldout(showExport, "Export", true, PSXEditorStyles.FoldoutHeader);
            if (!showExport) return;

            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(bitDepthProp, new GUIContent("Bit Depth"));
            EditorGUILayout.PropertyField(uvModeProp, new GUIContent("UV Mode",
                "Exact: each UV maps to the texel edge it sits on, and the PS1 draws the texture half a texel " +
                "left of and above where Unity draws it. Half-texel bleed: the vertices at each triangle's smallest U " +
                "and V move one texel down, so half of the texel outside the UV shows on every side and nothing inside " +
                "is cut. For atlas tiles with a bleed border, one tile per face."));

            EditorGUILayout.PropertyField(smoothNormalsProp, new GUIContent("Smooth Normals",
                "Smooth normals for lighting. Disable for flat/faceted shading."));

            EditorGUILayout.PropertyField(vertexColorModeProp, new GUIContent("Vertex Colors"));
            var vcMode = (VertexColorMode)vertexColorModeProp.enumValueIndex;
            if (vcMode == VertexColorMode.FlatColor)
            {
                EditorGUILayout.PropertyField(flatVertexColorProp, new GUIContent("Flat Color"));
            }
            else if (vcMode == VertexColorMode.MeshVertexColors)
            {
                var exporter = target as PSXObjectExporter;
                var mf = exporter.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && (mf.sharedMesh.colors == null || mf.sharedMesh.colors.Length == 0))
                {
                    EditorGUILayout.HelpBox("This mesh has no vertex colors. Will fall back to gray (128,128,128).", MessageType.Warning);
                }
            }

            EditorGUILayout.PropertyField(dynamicLightingProp, new GUIContent("Dynamic Lighting",
                "Auto: lit at runtime when a Realtime or Mixed Point Light's Range reaches this mesh at export. " +
                "On: always lit at runtime (for meshes that move into lights). Off: Point Lights are baked instead. " +
                "Runtime lighting gives each triangle one colour; On (smooth) lights each vertex, which looks " +
                "smoother on big triangles and costs about nine times as much."));
            DrawLightingStatus((PSXDynamicLighting)dynamicLightingProp.enumValueIndex, vcMode);

            EditorGUILayout.PropertyField(uvOffsetMaterialProp, new GUIContent("UV Offset Material"));
            EditorGUILayout.PropertyField(luaFileProp, new GUIContent("Lua Script"));

            if (luaFileProp.objectReferenceValue != null)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15);
                if (GUILayout.Button("Edit", PSXEditorStyles.SecondaryButton, GUILayout.Width(50)))
                    AssetDatabase.OpenAsset(luaFileProp.objectReferenceValue);
                if (GUILayout.Button("Clear", PSXEditorStyles.SecondaryButton, GUILayout.Width(50)))
                    luaFileProp.objectReferenceValue = null;
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15);
                if (GUILayout.Button("Create Lua Script", PSXEditorStyles.SecondaryButton, GUILayout.Width(130)))
                    CreateNewLuaScript();
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.indentLevel--;
        }

        // Live answer to "will a point light light this?", from the same code
        // the exporter runs.
        private void DrawLightingStatus(PSXDynamicLighting mode, VertexColorMode vcMode)
        {
            var exporter = target as PSXObjectExporter;
            Light[] lights = PSXPointLightExporter.CollectCached();
            var ml = PSXPointLightExporter.Analyze(exporter, lights, includeBaked: true);
            if (!ml.HasMesh) return;

            bool anyRuntime = lights.Any(l => l != null);
            bool forcedOn = mode == PSXDynamicLighting.On || mode == PSXDynamicLighting.OnSmooth;
            bool lit = anyRuntime && (forcedOn ||
                                      (mode == PSXDynamicLighting.Auto && ml.Reaching.Count > 0));
            bool bakes = vcMode == VertexColorMode.BakedLighting;
            string Names(System.Collections.Generic.IEnumerable<Light> ls) => string.Join(", ", ls.Select(l => l.name));

            string text;
            if (lit && ml.Reaching.Count > 0)
                text = $"Lit at runtime by {Names(ml.Kept)}.";
            else if (lit)
                text = "Lit at runtime. No Realtime or Mixed Point Light reaches it in the scene as it is now; " +
                       "it lights up when one moves into range.";
            else if (forcedOn)
                text = "Set to On, but the scene has no Realtime or Mixed Point Light to light it with.";
            else if (ml.Reaching.Count > 0)
                text = bakes
                    ? $"Not lit at runtime: Dynamic Lighting is Off, so {Names(ml.Reaching)} are baked into its vertex colours."
                    : $"Not lit by {Names(ml.Reaching)}: Dynamic Lighting is Off and Vertex Colors is not Baked Lighting, " +
                      "so nothing bakes them either. Set Dynamic Lighting to Auto to light it.";
            else
                text = "No Realtime or Mixed Point Light reaches this mesh, so it keeps its baked vertex colours.";
            if (ml.BakedReaching.Count > 0)
                text += bakes
                    ? $" Baked into its vertex colours: {Names(ml.BakedReaching)}."
                    : $" {Names(ml.BakedReaching)} (Mode Baked) need Vertex Colors set to Baked Lighting to show up.";
            if (lit && mode == PSXDynamicLighting.OnSmooth)
                text += " Lit per vertex (On (smooth)).";
            GUILayout.Label(text, PSXEditorStyles.InfoBox);

            if (lit && ml.Reaching.Count > PSXPointLightExporter.MaxLightsPerMesh)
            {
                EditorGUILayout.HelpBox(
                    $"{ml.Reaching.Count} Realtime/Mixed Point Lights reach this mesh and the PS1 applies " +
                    $"{PSXPointLightExporter.MaxLightsPerMesh}, so {Names(ml.Ignored)} will not light it. Lower a light's " +
                    "Range, set a light that never moves to Mode Baked, or split this mesh into smaller pieces.",
                    MessageType.Warning);
            }
        }

        private void DrawCollisionSection()
        {
            showCollision = EditorGUILayout.Foldout(showCollision, "Collision", true, PSXEditorStyles.FoldoutHeader);
            if (!showCollision) return;

            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(collisionTypeProp, new GUIContent("Type"));

            var collType = (PSXCollisionType)collisionTypeProp.enumValueIndex;
            if (collType == PSXCollisionType.Static)
            {
                EditorGUILayout.LabelField(
                    "<color=#88cc88>Only bakes holes in the navregions</color>",
                    PSXEditorStyles.RichLabel);
            }
            else if (collType == PSXCollisionType.Dynamic)
            {
                EditorGUILayout.LabelField(
                    "<color=#88aaff>Runtime AABB collider. Pushes player back + fires Lua events.</color>",
                    PSXEditorStyles.RichLabel);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(isPlatformProp, new GUIContent("Is Platform",
                "All boundary edges of nav regions from this mesh allow walkoff. The player can walk off any edge and fall."));

            EditorGUI.indentLevel--;
        }

        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select Scene Exporter", PSXEditorStyles.SecondaryButton))
            {
                var se = FindFirstObjectByType<PSXSceneExporter>();
                if (se != null)
                    Selection.activeGameObject = se.gameObject;
                else
                    EditorUtility.DisplayDialog("Not Found", "No PSXSceneExporter in scene.", "OK");
            }
            EditorGUILayout.EndHorizontal();
        }

        private void CreateNewLuaScript()
        {
            var exporter = target as PSXObjectExporter;
            string defaultName = exporter.gameObject.name.ToLower().Replace(" ", "_");
            string path = EditorUtility.SaveFilePanelInProject(
                "Create Lua Script", defaultName + ".lua", "lua",
                "Create a new Lua script for this object");

            if (string.IsNullOrEmpty(path)) return;

            string template =
                $"function onCreate(self)\nend\n\nfunction onUpdate(self, dt)\nend\n";
            System.IO.File.WriteAllText(path, template);
            AssetDatabase.Refresh();

            var luaFile = AssetDatabase.LoadAssetAtPath<LuaFile>(path);
            if (luaFile != null)
            {
                luaFileProp.objectReferenceValue = luaFile;
                serializedObject.ApplyModifiedProperties();
            }
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawColliderGizmo(PSXObjectExporter exporter, GizmoType gizmoType)
        {
            if (exporter.CollisionType != PSXCollisionType.Dynamic) return;

            MeshFilter mf = exporter.GetComponent<MeshFilter>();
            Mesh mesh = mf?.sharedMesh;
            if (mesh == null) return;

            Bounds local = mesh.bounds;
            Matrix4x4 worldMatrix = exporter.transform.localToWorldMatrix;

            Vector3 ext = local.extents;
            Vector3 center = local.center;
            Vector3 aabbMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 aabbMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = center + new Vector3(
                    (i & 1) != 0 ? ext.x : -ext.x,
                    (i & 2) != 0 ? ext.y : -ext.y,
                    (i & 4) != 0 ? ext.z : -ext.z
                );
                Vector3 world = worldMatrix.MultiplyPoint3x4(corner);
                aabbMin = Vector3.Min(aabbMin, world);
                aabbMax = Vector3.Max(aabbMax, world);
            }

            bool selected = (gizmoType & GizmoType.Selected) != 0;
            Gizmos.color = selected ? new Color(0.2f, 0.8f, 1f, 0.8f) : new Color(0.2f, 0.8f, 1f, 0.3f);
            Vector3 c = (aabbMin + aabbMax) * 0.5f;
            Vector3 s = aabbMax - aabbMin;
            Gizmos.DrawWireCube(c, s);
        }
    }
}
