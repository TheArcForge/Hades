using System.Collections.Generic;
using ArcForge.Hades.Editor.Graph.Models;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    public class ScriptableObjectScanner : IAssetScanner
    {
        public string[] SupportedExtensions => new[] { ".asset" };
        public string ScannerName => "ScriptableObjectScanner";
        public int Version => 1;

        public ScanResult Scan(string assetPath)
        {
            var result = new ScanResult();
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);

            if (asset == null) return result;

            var soType = asset.GetType();
            var soNode = new NodeRecord("ScriptableObject", guid)
            {
                Name = asset.name,
                Path = assetPath,
                Properties = new Dictionary<string, object>
                {
                    { "so_type", soType.FullName }
                }
            };
            result.Nodes.Add(soNode);

            var scriptGuids = AssetDatabase.FindAssets($"t:MonoScript {soType.Name}");
            foreach (var scriptGuid in scriptGuids)
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(scriptGuid));
                if (script != null && script.GetClass() == soType)
                {
                    result.Edges.Add(new EdgeRecord("instance_of", guid, 0, scriptGuid, 0));
                    break;
                }
            }

            var so = new SerializedObject(asset);
            var prop = so.GetIterator();
            while (prop.NextVisible(true))
            {
                if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (prop.objectReferenceValue == null) continue;

                var refPath = AssetDatabase.GetAssetPath(prop.objectReferenceValue);
                if (string.IsNullOrEmpty(refPath) || !refPath.StartsWith("Assets/")) continue;

                var refGuid = AssetDatabase.AssetPathToGUID(refPath);
                result.Edges.Add(new EdgeRecord("references", guid, 0, refGuid, 0)
                {
                    Properties = new Dictionary<string, object> { { "field", prop.name } }
                });
            }

            return result;
        }
    }
}
