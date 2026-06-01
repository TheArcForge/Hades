using System.Collections.Generic;
using System.Linq;
using ArcForge.Hades.Editor.Graph.Models;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    public class SceneScanner : IAssetScanner
    {
        public string[] SupportedExtensions => new[] { ".unity" };
        public string ScannerName => "SceneScanner";
        public int Version => 1;

        public ScanResult Scan(string assetPath)
        {
            var result = new ScanResult();
            var sceneGuid = AssetDatabase.AssetPathToGUID(assetPath);

            var sceneNode = new NodeRecord("Scene", sceneGuid)
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(assetPath),
                Path = assetPath
            };
            result.Nodes.Add(sceneNode);

            Scene? loadedScene = TryGetOpenScene(assetPath);
            bool needsClose = false;

            if (!loadedScene.HasValue)
            {
                loadedScene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
                needsClose = true;
            }

            try
            {
                if (loadedScene.HasValue)
                {
                    var rootObjects = loadedScene.Value.GetRootGameObjects();
                    foreach (var rootGO in rootObjects)
                    {
                        ScanGameObject(rootGO, sceneGuid, sceneNode, result);
                    }
                }
            }
            finally
            {
                if (needsClose && loadedScene.HasValue)
                {
                    EditorSceneManager.CloseScene(loadedScene.Value, true);
                }
            }

            return result;
        }

        Scene? TryGetOpenScene(string assetPath)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.path == assetPath && scene.isLoaded)
                    return scene;
            }
            return null;
        }

        void ScanGameObject(GameObject go, string sceneGuid, NodeRecord parentNode, ScanResult result)
        {
            var goFileId = go.GetInstanceID();
            var goNode = new NodeRecord("GameObject")
            {
                Name = go.name,
                FileId = goFileId,
                Properties = new Dictionary<string, object>
                {
                    { "active", go.activeSelf },
                    { "layer", LayerMask.LayerToName(go.layer) },
                    { "tag", go.tag }
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
                        $"Missing component on {go.name}", parentNode.Path));
                    continue;
                }

                ScanComponent(comp, goNode, sceneGuid, result);
            }

            for (int i = 0; i < go.transform.childCount; i++)
            {
                ScanGameObject(go.transform.GetChild(i).gameObject, sceneGuid, goNode, result);
            }
        }

        void ScanComponent(Component comp, NodeRecord goNode, string sceneGuid, ScanResult result)
        {
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
                goNode.Guid, goNode.FileId ?? 0,
                compNode.Guid, compFileId));

            if (!compType.Namespace?.StartsWith("UnityEngine") ?? false)
            {
                var scriptTypeGuid = ScanResolver.GetScriptGuid(compType);
                if (scriptTypeGuid != null)
                {
                    result.Edges.Add(new EdgeRecord("instance_of",
                        compNode.Guid, compFileId,
                        scriptTypeGuid, 0));
                }
            }

            ScanSerializedReferences(comp, compNode, result);
        }

        void ScanSerializedReferences(Component comp, NodeRecord compNode, ScanResult result)
        {
            var so = new SerializedObject(comp);
            var prop = so.GetIterator();

            while (prop.NextVisible(true))
            {
                if (prop.propertyType == SerializedPropertyType.ObjectReference && prop.objectReferenceValue != null)
                {
                    var refGuid = ScanResolver.GetAssetGuidUnderAssets(prop.objectReferenceValue);
                    if (refGuid != null)
                    {
                        result.Edges.Add(new EdgeRecord("references",
                            compNode.Guid, compNode.FileId ?? 0,
                            refGuid, 0)
                        {
                            Properties = new Dictionary<string, object>
                            {
                                { "field", prop.name }
                            }
                        });
                    }
                }
            }
        }
    }
}
