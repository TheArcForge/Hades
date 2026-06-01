using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    /// <summary>
    /// Per-rebuild memoization caches shared by SceneScanner and PrefabScanner.
    ///
    /// Both scanners previously resolved, for EVERY non-builtin component instance, the
    /// component's MonoScript GUID via AssetDatabase.FindAssets + LoadAssetAtPath +
    /// GetClass() — potentially 100k+ asset-database searches on a large project, even
    /// though the Type → script-GUID mapping is constant across a rebuild. They also
    /// re-resolved the same referenced-asset GUID once per reference. These caches collapse
    /// that work to once per distinct Type / referenced object.
    ///
    /// Cached values (including nulls) are kept for the whole rebuild. Static state resets
    /// on domain reload (script recompile) — exactly when the script mapping could change —
    /// and Clear() is called at the start of each rebuild as defensive insurance.
    /// </summary>
    public static class ScanResolver
    {
        static readonly Dictionary<Type, string> ScriptGuidCache = new Dictionary<Type, string>();
        static readonly Dictionary<int, string> AssetGuidCache = new Dictionary<int, string>();

        /// <summary>
        /// Returns the MonoScript GUID backing the given component Type, or null if none
        /// matches. Resolved once per distinct Type, then served from cache.
        /// </summary>
        public static string GetScriptGuid(Type componentType)
        {
            if (ScriptGuidCache.TryGetValue(componentType, out var cached))
                return cached;

            string guid = ResolveScriptGuid(componentType);
            ScriptGuidCache[componentType] = guid;
            return guid;
        }

        static string ResolveScriptGuid(Type componentType)
        {
            var candidates = AssetDatabase.FindAssets($"t:MonoScript {componentType.Name}");
            foreach (var candidate in candidates)
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(candidate));
                if (script != null && script.GetClass() == componentType)
                    return candidate;
            }
            return null;
        }

        /// <summary>
        /// Returns the GUID of the asset backing <paramref name="obj"/> when it is a
        /// persistent asset under "Assets/", otherwise null (preserving the prior
        /// Assets/-only reference filter). Resolved once per distinct object, then cached.
        /// </summary>
        public static string GetAssetGuidUnderAssets(UnityEngine.Object obj)
        {
            if (obj == null) return null;

            int id = obj.GetInstanceID();
            if (AssetGuidCache.TryGetValue(id, out var cached))
                return cached;

            string guid = null;
            var path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/"))
                guid = AssetDatabase.AssetPathToGUID(path);

            AssetGuidCache[id] = guid;
            return guid;
        }

        public static void Clear()
        {
            ScriptGuidCache.Clear();
            AssetGuidCache.Clear();
        }
    }
}
