using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ArcForge.Hades.Editor.Graph.Models;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    public class ShaderScanner : IAssetScanner
    {
        public string[] SupportedExtensions => new[] { ".shader", ".shadergraph" };
        public string ScannerName => "ShaderScanner";
        public int Version => 1;

        static readonly Regex PropertyRegex = new Regex(
            @"(\w+)\s*\(""([^""]*)""\s*,\s*(\w+)", RegexOptions.Compiled);

        public ScanResult Scan(string assetPath)
        {
            var result = new ScanResult();
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);

            var isShaderGraph = assetPath.EndsWith(".shadergraph");

            var node = new NodeRecord("Shader", guid)
            {
                Name = shader != null ? shader.name : Path.GetFileNameWithoutExtension(assetPath),
                Path = assetPath,
                Properties = new Dictionary<string, object>
                {
                    { "shader_type", isShaderGraph ? "ShaderGraph" : "Code" }
                }
            };

            if (!isShaderGraph && File.Exists(assetPath))
            {
                var content = File.ReadAllText(assetPath);
                var props = new List<string>();
                foreach (Match m in PropertyRegex.Matches(content))
                {
                    props.Add($"{m.Groups[1].Value}:{m.Groups[3].Value}");
                }
                if (props.Count > 0)
                    node.Properties["properties"] = props;
            }

            if (shader != null)
            {
                node.Properties["property_count"] = shader.GetPropertyCount();
            }

            result.Nodes.Add(node);
            return result;
        }
    }
}
