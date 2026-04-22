using UnityEditor;
using UnityEngine;
using SplashEdit.RuntimeCode;
using System.Linq;

namespace SplashEdit.EditorCode
{
    /// <summary>
    /// Minimal menu items — everything goes through the unified Control Panel.
    /// Only keeps: Control Panel shortcut + GameObject creation helpers.
    /// </summary>
    public static class PSXMenuItems
    {
        private const string MENU_ROOT = "PlayStation 1/";
        
        // ───── Main Entry Point ─────
        
        [MenuItem(MENU_ROOT + "SplashEdit Control Panel %#l", false, 0)]
        public static void OpenControlPanel()
        {
            SplashControlPanel.ShowWindow();
        }

        [MenuItem(MENU_ROOT + "About SplashEdit", false, 100)]
        public static void OpenAboutWindow()
        {
            PSXAboutWindow.ShowWindow();
        }
        
        // ───── GameObject Menu ─────
        
        [MenuItem("GameObject/PlayStation 1/Scene Exporter", false, 10)]
        public static void CreateSceneExporter(MenuCommand menuCommand)
        {
            var existing = Object.FindFirstObjectByType<PSXSceneExporter>();
            if (existing != null)
            {
                EditorUtility.DisplayDialog(
                    "Scene Exporter Exists",
                    "A PSXSceneExporter already exists in this scene.\n\n" +
                    "Only one exporter is needed per scene.",
                    "OK");
                Selection.activeGameObject = existing.gameObject;
                return;
            }
            
            var go = new GameObject("PSXSceneExporter");
            go.AddComponent<PSXSceneExporter>();
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create PSX Scene Exporter");
            Selection.activeGameObject = go;
        }
        
        [MenuItem("GameObject/PlayStation 1/Exportable Object", false, 12)]
        public static void CreateExportableObject(MenuCommand menuCommand)
        {
            var go = new GameObject("PSXObject");
            go.AddComponent<PSXObjectExporter>();
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create PSX Object");
            Selection.activeGameObject = go;
        }
        
        // ───── Context Menu ─────
        
        [MenuItem("CONTEXT/MeshFilter/Add PSX Object Exporter")]
        public static void AddPSXObjectExporterFromMesh(MenuCommand command)
        {
            var meshFilter = command.context as MeshFilter;
            if (meshFilter != null && meshFilter.GetComponent<PSXObjectExporter>() == null)
            {
                Undo.AddComponent<PSXObjectExporter>(meshFilter.gameObject);
            }
        }
        
        [MenuItem("CONTEXT/MeshRenderer/Add PSX Object Exporter")]
        public static void AddPSXObjectExporterFromRenderer(MenuCommand command)
        {
            var renderer = command.context as MeshRenderer;
            if (renderer != null && renderer.GetComponent<PSXObjectExporter>() == null)
            {
                Undo.AddComponent<PSXObjectExporter>(renderer.gameObject);
            }
        }
    }
}
