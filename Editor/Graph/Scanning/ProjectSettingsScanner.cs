using System.Collections.Generic;
using System.Linq;
using ArcForge.Hades.Editor.Graph.Models;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArcForge.Hades.Editor.Graph.Scanning
{
    public class ProjectSettingsScanner : IAssetScanner
    {
        public string[] SupportedExtensions => new[] { ".asset" };
        public string ScannerName => "ProjectSettingsScanner";
        public int Version => 1;

        public ScanResult Scan(string assetPath)
        {
            var result = new ScanResult();

            if (assetPath.Contains("EditorBuildSettings"))
                ScanBuildSettings(result);
            else if (assetPath.Contains("GraphicsSettings"))
                ScanGraphicsSettings(result);
            else if (assetPath.Contains("DynamicsManager") || assetPath.Contains("Physics"))
                ScanPhysicsSettings(result);
            else if (assetPath.Contains("InputManager"))
                ScanInputSettings(result);

            return result;
        }

        void ScanBuildSettings(ScanResult result)
        {
            var scenes = EditorBuildSettings.scenes;
            var node = new NodeRecord("BuildSettings")
            {
                Name = "BuildSettings",
                Path = "ProjectSettings/EditorBuildSettings.asset",
                Properties = new Dictionary<string, object>
                {
                    { "scene_count", scenes.Length },
                    { "enabled_scene_count", scenes.Count(s => s.enabled) }
                }
            };
            result.Nodes.Add(node);

            for (int i = 0; i < scenes.Length; i++)
            {
                if (!scenes[i].enabled) continue;
                var sceneGuid = scenes[i].guid.ToString();
                result.Edges.Add(new EdgeRecord("included_in_build",
                    sceneGuid, 0, node.Guid, 0)
                {
                    Properties = new Dictionary<string, object> { { "build_index", i } }
                });
            }
        }

        void ScanGraphicsSettings(ScanResult result)
        {
            var pipeline = GraphicsSettings.defaultRenderPipeline;
            if (pipeline != null)
            {
                AddRenderPipelineNode(result, AssetDatabase.GetAssetPath(pipeline),
                    pipeline.name, pipeline.GetType().Name);
                return;
            }

            // The typed accessor returns null on some URP/HDRP setups even when the raw
            // m_CustomRenderPipeline GUID is set, which made analyze_render_pipeline
            // falsely report Built-in. Fall back to the serialized GraphicsSettings object
            // and resolve the referenced pipeline asset directly. Any failure leaves
            // behavior unchanged (no node).
            try
            {
                var so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
                var prop = so.FindProperty("m_CustomRenderPipeline");
                var asset = prop?.objectReferenceValue;
                if (asset == null) return;

                var path = AssetDatabase.GetAssetPath(asset);
                if (string.IsNullOrEmpty(path)) return;

                AddRenderPipelineNode(result, path, asset.name, asset.GetType().Name);
            }
            catch (System.Exception ex)
            {
                result.Warnings.Add(new ScanWarning(WarningSeverity.Warning,
                    $"Could not read raw render pipeline setting: {ex.Message}",
                    "ProjectSettings/GraphicsSettings.asset"));
            }
        }

        void AddRenderPipelineNode(ScanResult result, string pipelinePath, string pipelineName, string pipelineType)
        {
            var pipelineGuid = AssetDatabase.AssetPathToGUID(pipelinePath);
            var node = new NodeRecord("RenderPipelineAsset", pipelineGuid)
            {
                Name = pipelineName,
                Path = pipelinePath,
                Properties = new Dictionary<string, object>
                {
                    { "pipeline_type", pipelineType }
                }
            };
            result.Nodes.Add(node);
        }

        void ScanPhysicsSettings(ScanResult result)
        {
            var node = new NodeRecord("PhysicsSettings")
            {
                Name = "PhysicsSettings",
                Path = "ProjectSettings/DynamicsManager.asset",
                Properties = new Dictionary<string, object>
                {
                    { "gravity", $"{Physics.gravity.x},{Physics.gravity.y},{Physics.gravity.z}" },
                    { "default_solver_iterations", Physics.defaultSolverIterations }
                }
            };
            result.Nodes.Add(node);
        }

        void ScanInputSettings(ScanResult result)
        {
            var node = new NodeRecord("InputSettings")
            {
                Name = "InputSettings",
                Path = "ProjectSettings/InputManager.asset"
            };
            result.Nodes.Add(node);
        }
    }
}
