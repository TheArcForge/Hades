// Editor/Asphodel/AsphodeMenuItems.cs
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Asphodel
{
    public static class AsphodeMenuItems
    {
        [MenuItem("Hades/Validate Memory", priority = 200)]
        public static void ValidateMemory()
        {
            var validator = AsphodeInitializer.Validator;
            if (validator == null)
            {
                Debug.LogError("[Hades Asphodel] Validator not initialized");
                return;
            }

            var results = validator.ValidateAll();
            int warnings = 0;
            foreach (var r in results)
                warnings += r.RulesWarning;

            Debug.Log($"[Hades Asphodel] Validation complete: {results.Count} files, {warnings} warnings");
        }

        [MenuItem("Hades/Open Memory Folder", priority = 201)]
        public static void OpenMemoryFolder()
        {
            var manager = AsphodeInitializer.Manager;
            if (manager == null)
            {
                Debug.LogError("[Hades Asphodel] Memory not initialized");
                return;
            }

            EditorUtility.RevealInFinder(manager.MemoryDir);
        }
    }
}
