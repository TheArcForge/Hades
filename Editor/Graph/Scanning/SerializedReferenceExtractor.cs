using System;
using System.Collections.Generic;
using ArcForge.Hades.Editor.Graph.Models;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    /// <summary>
    /// Shared serialized-reference walk used by the Scene, Prefab, and ScriptableObject
    /// scanners. Emits two kinds of edges from a SerializedObject's visible properties:
    ///
    ///  - ObjectReference (existing behavior): a `references` edge (or, when
    ///    <paramref name="useTypedAssetEdges"/> is true, the Prefab scanner's typed
    ///    uses_material / uses_mesh / uses_audio variants) to the referenced asset under
    ///    Assets/. Per-scanner typing is preserved exactly — the helper introduces no
    ///    typed variants where they did not previously exist.
    ///
    ///  - String-GUID (new): Addressables store AssetReference targets as string GUIDs
    ///    (m_AssetGUID / m_SubObjectGUID) that never produced an edge. When such a field
    ///    holds a valid 32-char hex GUID resolving to an asset under Assets/, emit a
    ///    `references` edge flagged { addressable = true, field }. Consumers
    ///    (find_references_to / trace_dependencies / coverage) need no changes.
    ///
    /// All per-property work is guarded: a bad GUID, unresolved path, or exception affects
    /// only that property, never the whole scan.
    /// </summary>
    public static class SerializedReferenceExtractor
    {
        static readonly HashSet<string> AddressableGuidFields = new HashSet<string>
        {
            "m_AssetGUID",
            "m_SubObjectGUID",
        };

        public static bool IsAddressableGuidField(string fieldName) =>
            fieldName != null && AddressableGuidFields.Contains(fieldName);

        public static bool IsValidAssetGuidHex(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 32) return false;
            foreach (var c in s)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        /// <param name="ownerNode">
        /// The node the edges originate from. Its Guid/FileId are used verbatim as the edge
        /// source, matching each scanner's existing owner identity (component FileId for
        /// Scene/Prefab; asset Guid for ScriptableObject).
        /// </param>
        /// <param name="useTypedAssetEdges">
        /// True only for PrefabScanner, which classifies Material/Mesh/AudioClip references
        /// into typed edges. Scene/ScriptableObject pass false (plain `references`).
        /// </param>
        public static void Extract(SerializedObject so, NodeRecord ownerNode, ScanResult result,
            bool useTypedAssetEdges)
        {
            var prop = so.GetIterator();
            while (prop.NextVisible(true))
            {
                try
                {
                    if (prop.propertyType == SerializedPropertyType.ObjectReference)
                        ExtractObjectReference(prop, ownerNode, result, useTypedAssetEdges);
                    else if (prop.propertyType == SerializedPropertyType.String
                             && IsAddressableGuidField(prop.name))
                        ExtractAddressableGuid(prop, ownerNode, result);
                }
                catch
                {
                    // Per-property guard: never fail the whole scan for one bad property.
                }
            }
        }

        static void ExtractObjectReference(SerializedProperty prop, NodeRecord owner,
            ScanResult result, bool useTypedAssetEdges)
        {
            var value = prop.objectReferenceValue;
            if (value == null) return;

            var refGuid = ScanResolver.GetAssetGuidUnderAssets(value);
            if (refGuid == null) return;

            var sourceGuid = owner.Guid;
            var sourceFileId = owner.FileId ?? 0;

            if (useTypedAssetEdges)
            {
                if (value is Material)
                {
                    result.Edges.Add(new EdgeRecord("uses_material", sourceGuid, sourceFileId, refGuid, 0));
                    return;
                }
                if (value is Mesh)
                {
                    result.Edges.Add(new EdgeRecord("uses_mesh", sourceGuid, sourceFileId, refGuid, 0));
                    return;
                }
                if (value is AudioClip)
                {
                    result.Edges.Add(new EdgeRecord("uses_audio", sourceGuid, sourceFileId, refGuid, 0));
                    return;
                }
            }

            result.Edges.Add(new EdgeRecord("references", sourceGuid, sourceFileId, refGuid, 0)
            {
                Properties = new Dictionary<string, object> { { "field", prop.name } }
            });
        }

        static void ExtractAddressableGuid(SerializedProperty prop, NodeRecord owner, ScanResult result)
        {
            var guid = prop.stringValue;
            if (!IsValidAssetGuidHex(guid)) return;

            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/")) return;

            // `address` enrichment is intentionally omitted: at extract time (Phase C, before
            // the Addressables settings pass) the entry index does not yet exist, so the
            // address is not resolvable. The spec permits omitting it.
            result.Edges.Add(new EdgeRecord("references", owner.Guid, owner.FileId ?? 0, guid, 0)
            {
                Properties = new Dictionary<string, object>
                {
                    { "addressable", true },
                    { "field", prop.name },
                }
            });
        }
    }
}
