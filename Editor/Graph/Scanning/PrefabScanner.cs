using System.Collections.Generic;
using System.Linq;
using ArcForge.Hades.Editor.Graph.Models;
using UnityEditor;

using UnityEngine;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    public class PrefabScanner : IAssetScanner
    {
        public string[] SupportedExtensions => new[] { ".prefab" };
        public string ScannerName => "PrefabScanner";
        public int Version => 1;

        public ScanResult Scan(string assetPath)
        {
            var result = new ScanResult();
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            var prefabName = System.IO.Path.GetFileNameWithoutExtension(assetPath);

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefabAsset == null)
            {
                result.Warnings.Add(new ScanWarning(WarningSeverity.Error,
                    $"Could not load prefab: {assetPath}", assetPath));
                return result;
            }

            bool isVariant = PrefabUtility.GetPrefabAssetType(prefabAsset) == PrefabAssetType.Variant;
            var nodeType = isVariant ? "PrefabVariant" : "Prefab";

            var prefabNode = new NodeRecord(nodeType, guid)
            {
                Name = prefabName,
                Path = assetPath,
                Properties = new Dictionary<string, object>
                {
                    { "is_variant", isVariant }
                }
            };
            result.Nodes.Add(prefabNode);

            if (isVariant)
            {
                var baseObj = PrefabUtility.GetCorrespondingObjectFromOriginalSource(prefabAsset);
                if (baseObj != null)
                {
                    var basePath = AssetDatabase.GetAssetPath(baseObj);
                    var baseGuid = AssetDatabase.AssetPathToGUID(basePath);
                    result.Edges.Add(new EdgeRecord("inherits_from", guid, 0, baseGuid, 0));

                    var overrides = PrefabUtility.GetObjectOverrides(prefabAsset, false);
                    if (overrides.Count > 0)
                    {
                        prefabNode.Properties["override_count"] = overrides.Count;
                    }
                }
            }

            var openStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            GameObject rootToScan;
            bool needsUnload = false;

            if (openStage != null && openStage.assetPath == assetPath)
            {
                rootToScan = openStage.prefabContentsRoot;
            }
            else
            {
                rootToScan = PrefabUtility.LoadPrefabContents(assetPath);
                needsUnload = true;
            }

            try
            {
                if (rootToScan != null)
                {
                    var nestedSeen = new HashSet<string>();
                    ScanGameObject(rootToScan, guid, prefabNode, result, true, nestedSeen);
                }
            }
            finally
            {
                if (needsUnload && rootToScan != null)
                {
                    PrefabUtility.UnloadPrefabContents(rootToScan);
                }
            }

            return result;
        }

        void ScanGameObject(GameObject go, string prefabGuid, NodeRecord parentNode, ScanResult result,
            bool isRoot, HashSet<string> nestedSeen)
        {
            var goFileId = go.GetInstanceID();
            var goNode = new NodeRecord("GameObject")
            {
                Name = go.name,
                FileId = goFileId,
                Properties = new Dictionary<string, object>
                {
                    { "active", go.activeSelf }
                }
            };
            result.Nodes.Add(goNode);

            result.Edges.Add(new EdgeRecord("contains",
                parentNode.Guid, parentNode.FileId ?? 0,
                goNode.Guid, goFileId));

            // Nested-prefab linkage: a non-root GameObject that is itself the root of another
            // prefab instance creates a prefab→prefab dependency (m_SourcePrefab). The scan
            // root is skipped — for variants its source is already recorded via inherits_from.
            // Conservative: instance-root detection only; a recursive serialized-ref walk is
            // deferred. Deduped so repeated instances of one prefab yield a single edge.
            if (!isRoot && PrefabUtility.IsAnyPrefabInstanceRoot(go))
            {
                var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (!string.IsNullOrEmpty(sourcePath))
                {
                    var sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
                    if (!string.IsNullOrEmpty(sourceGuid) && sourceGuid != prefabGuid
                        && nestedSeen.Add(sourceGuid))
                    {
                        result.Edges.Add(new EdgeRecord("nests_prefab",
                            prefabGuid, 0, sourceGuid, 0));
                    }
                }
            }

            var components = go.GetComponents<Component>();
            foreach (var comp in components)
            {
                if (comp == null)
                {
                    result.Warnings.Add(new ScanWarning(WarningSeverity.Warning,
                        $"Missing component on {go.name} in prefab"));
                    continue;
                }

                var compType = comp.GetType();
                var compFileId = comp.GetInstanceID();

                var compNode = new NodeRecord("Component")
                {
                    Name = compType.Name,
                    FileId = compFileId,
                    Properties = new Dictionary<string, object>
                    {
                        { "component_type", compType.FullName },
                        { "is_built_in", compType.Namespace?.StartsWith("UnityEngine") ?? false }
                    }
                };
                result.Nodes.Add(compNode);

                result.Edges.Add(new EdgeRecord("contains",
                    goNode.Guid, goFileId, compNode.Guid, compFileId));

                if (!compType.Namespace?.StartsWith("UnityEngine") ?? false)
                {
                    var scriptGuid = ScanResolver.GetScriptGuid(compType);
                    if (scriptGuid != null)
                        result.Edges.Add(new EdgeRecord("instance_of", compNode.Guid, compFileId, scriptGuid, 0));
                }

                ScanSerializedReferences(comp, compNode, result);
            }

            for (int i = 0; i < go.transform.childCount; i++)
            {
                ScanGameObject(go.transform.GetChild(i).gameObject, prefabGuid, goNode, result, false, nestedSeen);
            }
        }

        void ScanSerializedReferences(Component comp, NodeRecord compNode, ScanResult result)
        {
            var so = new SerializedObject(comp);
            SerializedReferenceExtractor.Extract(so, compNode, result, useTypedAssetEdges: true);
        }
    }
}
