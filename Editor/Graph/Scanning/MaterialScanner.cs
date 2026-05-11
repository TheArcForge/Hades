using System.Collections.Generic;
using ArcForge.Hades.Editor.Graph.Models;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    public class MaterialScanner : IAssetScanner
    {
        public string[] SupportedExtensions => new[] { ".mat" };
        public string ScannerName => "MaterialScanner";
        public int Version => 1;

        public ScanResult Scan(string assetPath)
        {
            var result = new ScanResult();
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);

            if (material == null) return result;

            var node = new NodeRecord("Material", guid)
            {
                Name = material.name,
                Path = assetPath,
                Properties = new Dictionary<string, object>
                {
                    { "shader_name", material.shader?.name ?? "None" },
                    { "render_queue", material.renderQueue }
                }
            };

            if (material.HasProperty("_Color"))
            {
                var color = material.GetColor("_Color");
                node.Properties["color"] = $"#{ColorUtility.ToHtmlStringRGBA(color)}";
            }

            result.Nodes.Add(node);

            if (material.shader != null)
            {
                var shaderPath = AssetDatabase.GetAssetPath(material.shader);
                if (!string.IsNullOrEmpty(shaderPath) && shaderPath.StartsWith("Assets/"))
                {
                    var shaderGuid = AssetDatabase.AssetPathToGUID(shaderPath);
                    result.Edges.Add(new EdgeRecord("uses_shader", guid, 0, shaderGuid, 0));
                }
            }

            var texPropertyNames = material.GetTexturePropertyNames();
            foreach (var texProp in texPropertyNames)
            {
                var tex = material.GetTexture(texProp);
                if (tex == null) continue;

                var texPath = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(texPath) || !texPath.StartsWith("Assets/")) continue;

                var texGuid = AssetDatabase.AssetPathToGUID(texPath);
                result.Edges.Add(new EdgeRecord("uses_texture", guid, 0, texGuid, 0)
                {
                    Properties = new Dictionary<string, object> { { "property", texProp } }
                });
            }

            return result;
        }
    }
}
