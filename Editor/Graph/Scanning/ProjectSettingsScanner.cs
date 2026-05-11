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
                var pipelinePath = AssetDatabase.GetAssetPath(pipeline);
                var pipelineGuid = AssetDatabase.AssetPathToGUID(pipelinePath);

                var node = new NodeRecord("RenderPipelineAsset", pipelineGuid)
                {
                    Name = pipeline.name,
                    Path = pipelinePath,
                    Properties = new Dictionary<string, object>
                    {
                        { "pipeline_type", pipeline.GetType().Name }
                    }
                };
                result.Nodes.Add(node);
            }
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
