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
                    ScanGameObject(rootToScan, guid, prefabNode, result);
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

        void ScanGameObject(GameObject go, string prefabGuid, NodeRecord parentNode, ScanResult result)
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
                    var scriptGuids = AssetDatabase.FindAssets($"t:MonoScript {compType.Name}");
                    foreach (var scriptGuid in scriptGuids)
                    {
                        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(scriptGuid));
                        if (script != null && script.GetClass() == compType)
                        {
                            result.Edges.Add(new EdgeRecord("instance_of", compNode.Guid, compFileId, scriptGuid, 0));
                            break;
                        }
                    }
                }

                ScanSerializedReferences(comp, compNode, result);
            }

            for (int i = 0; i < go.transform.childCount; i++)
            {
                ScanGameObject(go.transform.GetChild(i).gameObject, prefabGuid, goNode, result);
            }
        }

        void ScanSerializedReferences(Component comp, NodeRecord compNode, ScanResult result)
        {
            var so = new SerializedObject(comp);
            var prop = so.GetIterator();

            while (prop.NextVisible(true))
            {
                if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (prop.objectReferenceValue == null) continue;

                var refPath = AssetDatabase.GetAssetPath(prop.objectReferenceValue);
                if (string.IsNullOrEmpty(refPath) || !refPath.StartsWith("Assets/")) continue;

                var refGuid = AssetDatabase.AssetPathToGUID(refPath);

                if (prop.objectReferenceValue is Material)
                    result.Edges.Add(new EdgeRecord("uses_material", compNode.Guid, compNode.FileId ?? 0, refGuid, 0));
                else if (prop.objectReferenceValue is Mesh)
                    result.Edges.Add(new EdgeRecord("uses_mesh", compNode.Guid, compNode.FileId ?? 0, refGuid, 0));
                else if (prop.objectReferenceValue is AudioClip)
                    result.Edges.Add(new EdgeRecord("uses_audio", compNode.Guid, compNode.FileId ?? 0, refGuid, 0));
                else
                    result.Edges.Add(new EdgeRecord("references", compNode.Guid, compNode.FileId ?? 0, refGuid, 0)
                    {
                        Properties = new Dictionary<string, object> { { "field", prop.name } }
                    });
            }
        }
    }
}
