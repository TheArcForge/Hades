using System;
using System.Collections.Generic;
using System.Linq;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;
using ArcForge.Hades.Editor.MCP;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class GraphQueryTools
    {
        static ConfidenceBlock BuildConfidence()
        {
            var db = GraphDatabase.Instance;
            if (db == null)
                return ConfidenceBlock.Low("error").WithFactor("graph_availability", "unavailable");

            if (db.IsRebuildInProgress())
                return ConfidenceBlock.Medium("partial")
                    .WithFactor("graph_freshness", "rebuilding")
                    .WithRecommendation("Retry after rebuild completes for full results");

            return ConfidenceBlock.High().WithFactor("graph_freshness", "current");
        }

        [MCPTool("get_project_summary",
            "Returns a structured summary of the Unity project including asset counts, render pipeline, and key directories. Use depth 'shallow' for counts only, 'medium' for top-level breakdown, 'deep' for full detail.")]
        public static MCPToolResult GetProjectSummary(
            [MCPToolParam("Detail level: shallow, medium, or deep", required: false)] string depth = "shallow")
        {
            var db = GraphDatabase.Instance;
            if (db == null) return MCPToolResult.Error("Graph database not initialized");

            var counts = db.GetTypeCounts();
            var result = new JObject
            {
                ["project_name"] = db.FindNodesByType("Project").FirstOrDefault()?.Name ?? "Unknown",
                ["scene_count"] = GetCount(counts, "Scene"),
                ["prefab_count"] = GetCount(counts, "Prefab") + GetCount(counts, "PrefabVariant"),
                ["script_count"] = GetCount(counts, "Script"),
                ["scriptable_object_count"] = GetCount(counts, "ScriptableObject"),
                ["material_count"] = GetCount(counts, "Material"),
                ["total_node_count"] = db.GetNodeCount(),
                ["total_edge_count"] = db.GetEdgeCount()
            };

            var pipeline = db.FindNodesByType("RenderPipelineAsset").FirstOrDefault();
            if (pipeline != null)
                result["render_pipeline"] = pipeline.Properties?.ContainsKey("pipeline_type") == true
                    ? pipeline.Properties["pipeline_type"]?.ToString()
                    : pipeline.Name;

            if (depth == "medium" || depth == "deep")
            {
                var scenes = db.FindNodesByType("Scene");
                result["scenes"] = new JArray(scenes.Select(s => new JObject
                {
                    ["name"] = s.Name,
                    ["path"] = s.Path
                }));
            }

            return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
        }

        [MCPTool("get_scene_summary",
            "Returns the structure of a specific scene: top-level GameObjects, component breakdown, and notable assets. Use depth 'shallow' for overview, 'deep' for full hierarchy.")]
        public static MCPToolResult GetSceneSummary(
            [MCPToolParam("Path to the scene file (e.g. Assets/Scenes/Main.unity)", required: true)] string scene_path,
            [MCPToolParam("Detail level: shallow or deep", required: false)] string depth = "shallow")
        {
            var db = GraphDatabase.Instance;
            if (db == null) return MCPToolResult.Error("Graph database not initialized");

            var sceneNodes = db.FindNodesByType("Scene").Where(n => n.Path == scene_path).ToList();
            if (sceneNodes.Count == 0)
                return MCPToolResult.SuccessWithConfidence(
                    new { message = $"Scene not found: {scene_path}", results = new object[0] },
                    BuildConfidence());

            var scene = sceneNodes[0];
            var gameObjects = db.FindEdgesFrom(scene.Id, "contains");

            var result = new JObject
            {
                ["scene_name"] = scene.Name,
                ["scene_path"] = scene.Path,
                ["top_level_gameobject_count"] = gameObjects.Count,
                ["gameobjects"] = new JArray(gameObjects.Select(go => new JObject
                {
                    ["name"] = go.TargetName,
                    ["type"] = go.TargetType
                }))
            };

            return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
        }

        [MCPTool("analyze_render_pipeline",
            "Returns information about the project's render pipeline configuration including pipeline type, features, and quality settings.")]
        public static MCPToolResult AnalyzeRenderPipeline()
        {
            var db = GraphDatabase.Instance;
            if (db == null) return MCPToolResult.Error("Graph database not initialized");

            var pipelines = db.FindNodesByType("RenderPipelineAsset");
            if (pipelines.Count == 0)
                return MCPToolResult.SuccessWithConfidence(
                    new { message = "No render pipeline asset found (using Built-in Render Pipeline)" },
                    BuildConfidence());

            var pipeline = pipelines[0];
            var result = new JObject
            {
                ["name"] = pipeline.Name,
                ["path"] = pipeline.Path,
                ["type"] = pipeline.Properties?.ContainsKey("pipeline_type") == true
                    ? pipeline.Properties["pipeline_type"]?.ToString() : "Unknown"
            };

            return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
        }

        [MCPTool("hades_status",
            "Returns the current state of the Hades graph: node/edge counts, last rebuild time, scanner versions, and whether a rebuild is in progress.")]
        public static MCPToolResult HadesStatus()
        {
            var db = GraphDatabase.Instance;
            if (db == null) return MCPToolResult.Error("Graph database not initialized");

            var result = new JObject
            {
                ["node_count"] = db.GetNodeCount(),
                ["edge_count"] = db.GetEdgeCount(),
                ["is_rebuilding"] = db.IsRebuildInProgress(),
                ["last_full_rebuild"] = db.GetMetadata("last_full_rebuild_at"),
                ["last_incremental_update"] = db.GetMetadata("last_incremental_at"),
                ["type_counts"] = JObject.FromObject(db.GetTypeCounts()),
                ["version"] = "0.4.0",
                ["memory_files"] = Asphodel.AsphodeInitializer.Manager?.ListFiles()?.Count ?? 0
            };

            return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
        }

        [MCPTool("hades_rebuild_graph",
            "Triggers a full rebuild of the Hades knowledge graph. Use this if the graph seems stale or out of sync with the project. " +
            "Uses parallel scanning for .cs files (thread pool) and batched processing for other assets. " +
            "Blocks until rebuild completes (progress bar shown in Unity).")]
        public static MCPToolResult HadesRebuildGraph(
            [MCPToolParam("Set to 'true' to also rescan all packages (normally cached)", required: false)] string include_packages = "")
        {
            var handler = ArcForge.Hades.Editor.Graph.Updates.GraphUpdateHandler.Instance;
            if (handler == null) return MCPToolResult.Error("Graph update handler not initialized");

            var builder = handler.GetBuilder();
            if (builder == null) return MCPToolResult.Error("Graph builder not available");

            if (builder.GetStatus() != ArcForge.Hades.Editor.Graph.BuildStatus.Idle)
                return MCPToolResult.Success(new { status = "already_rebuilding", message = "A rebuild is already in progress" });

            if (include_packages == "true")
                builder.ScanPackages();

            builder.RebuildParallel();

            var db = GraphDatabase.Instance;
            return MCPToolResult.Success(new
            {
                status = "completed",
                nodes = db?.GetNodeCount() ?? 0,
                edges = db?.GetEdgeCount() ?? 0,
                message = $"Rebuild complete: {db?.GetNodeCount() ?? 0} nodes, {db?.GetEdgeCount() ?? 0} edges"
            });
        }

        [MCPTool("find_prefabs_with_component",
            "Finds all prefabs that contain a given component type. Returns prefab names, paths, and where the component appears.")]
        public static MCPToolResult FindPrefabsWithComponent(
            [MCPToolParam("Component type name (e.g. PlayerHealth)", required: true)] string component_type)
        {
            var db = GraphDatabase.Instance;
            if (db == null) return MCPToolResult.Error("Graph database not initialized");

            var components = db.FindNodesByType("Component")
                .Where(c => c.Name == component_type).ToList();

            var prefabs = new List<JObject>();
            foreach (var comp in components)
            {
                var containers = db.FindNodesWithEdgeTo(comp.Id, "contains");
                foreach (var go in containers)
                {
                    var parentContainers = db.FindNodesWithEdgeTo(go.Id, "contains");
                    foreach (var parent in parentContainers)
                    {
                        if (parent.Type == "Prefab" || parent.Type == "PrefabVariant")
                        {
                            prefabs.Add(new JObject
                            {
                                ["name"] = parent.Name,
                                ["path"] = parent.Path,
                                ["gameobject"] = go.Name
                            });
                        }
                    }
                }
            }

            var result = new JObject
            {
                ["component_type"] = component_type,
                ["count"] = prefabs.Count,
                ["prefabs"] = new JArray(prefabs.GroupBy(p => p["path"].ToString()).Select(g => g.First()))
            };

            return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
        }

        [MCPTool("find_components_using_pattern",
            "Finds components that match a pre-defined pattern (e.g. Singleton, EventChannel, ObjectPool).")]
        public static MCPToolResult FindComponentsUsingPattern(
            [MCPToolParam("Pattern name (e.g. Singleton, EventChannel, ObjectPool)", required: true)] string pattern_name)
        {
            var db = GraphDatabase.Instance;
            if (db == null) return MCPToolResult.Error("Graph database not initialized");

            var scriptTypes = db.FindNodesByType("ScriptType");
            var matches = new List<JObject>();

            foreach (var st in scriptTypes)
            {
                var baseType = st.Properties?.ContainsKey("base_type") == true
                    ? st.Properties["base_type"]?.ToString() : null;

                bool isMatch = false;
                if (st.Name.Contains(pattern_name)) isMatch = true;
                if (baseType != null && baseType.Contains(pattern_name)) isMatch = true;

                if (isMatch)
                {
                    matches.Add(new JObject
                    {
                        ["name"] = st.Name,
                        ["path"] = st.Path,
                        ["base_type"] = baseType
                    });
                }
            }

            var result = new JObject
            {
                ["pattern"] = pattern_name,
                ["count"] = matches.Count,
                ["matches"] = new JArray(matches)
            };

            return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
        }

        [MCPTool("find_references_to",
            "Finds all assets and components that reference a given asset. Returns the referencing nodes with their paths.")]
        public static MCPToolResult FindReferencesTo(
            [MCPToolParam("Path of the target asset (e.g. Assets/Scripts/PlayerHealth.cs)", required: true)] string target_path)
        {
            var db = GraphDatabase.Instance;
            if (db == null) return MCPToolResult.Error("Graph database not initialized");

            var targets = db.SearchByName(null, null)
                .Where(n => n.Path == target_path).ToList();

            if (targets.Count == 0)
                targets = db.FindNodesByType("ScriptType")
                    .Where(n => n.Path == target_path).ToList();

            var references = new List<JObject>();
            foreach (var target in targets)
            {
                var refs = db.FindNodesWithEdgeTo(target.Id);
                foreach (var refNode in refs)
                {
                    references.Add(new JObject
                    {
                        ["name"] = refNode.Name,
                        ["type"] = refNode.Type,
                        ["path"] = refNode.Path
                    });
                }
            }

            var result = new JObject
            {
                ["target"] = target_path,
                ["reference_count"] = references.Count,
                ["references"] = new JArray(references)
            };

            return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
        }

        [MCPTool("find_orphan_scripts",
            "Finds C# scripts that are not referenced by any component, prefab, or scene. These may be unused and safe to remove.")]
        public static MCPToolResult FindOrphanScripts()
        {
            var db = GraphDatabase.Instance;
            if (db == null) return MCPToolResult.Error("Graph database not initialized");

            var scriptTypes = db.FindNodesByType("ScriptType");
            var orphans = new List<JObject>();

            foreach (var st in scriptTypes)
            {
                var instanceEdges = db.FindNodesWithEdgeTo(st.Id, "instance_of");
                var referenceEdges = db.FindNodesWithEdgeTo(st.Id, "references");

                if (instanceEdges.Count == 0 && referenceEdges.Count == 0)
                {
                    orphans.Add(new JObject
                    {
                        ["name"] = st.Name,
                        ["path"] = st.Path
                    });
                }
            }

            var confidence = BuildConfidence()
                .WithFactor("static_analysis_coverage", "partial",
                    new List<string> { "reflection", "DI containers", "dynamic instantiation" })
                .WithRecommendation("Scripts may be used via reflection or DI that static analysis cannot detect");

            var result = new JObject
            {
                ["orphan_count"] = orphans.Count,
                ["orphan_scripts"] = new JArray(orphans)
            };

            return MCPToolResult.SuccessWithConfidence(result, confidence);
        }

        [MCPTool("search_by_name",
            "Searches across all graph nodes by name pattern (SQL LIKE syntax: % for wildcard). Optionally filter by node type.")]
        public static MCPToolResult SearchByName(
            [MCPToolParam("Name pattern with SQL LIKE wildcards (e.g. Player%)", required: true)] string name_pattern,
            [MCPToolParam("Filter by node type (e.g. Prefab, Script, Scene). Empty for all types.", required: false)] string type_filter = "")
        {
            var db = GraphDatabase.Instance;
            if (db == null) return MCPToolResult.Error("Graph database not initialized");

            var filter = string.IsNullOrEmpty(type_filter) ? null : type_filter;
            var nodes = db.SearchByName(name_pattern, filter);

            var result = new JObject
            {
                ["pattern"] = name_pattern,
                ["type_filter"] = type_filter ?? "all",
                ["count"] = nodes.Count,
                ["matches"] = new JArray(nodes.Select(n => new JObject
                {
                    ["name"] = n.Name,
                    ["type"] = n.Type,
                    ["path"] = n.Path
                }))
            };

            return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
        }

        [MCPTool("trace_dependencies",
            "Traces forward dependencies from an asset recursively. Shows what the asset depends on, up to max_depth hops.")]
        public static MCPToolResult TraceDependencies(
            [MCPToolParam("Path of the asset to trace from", required: true)] string asset_path,
            [MCPToolParam("Maximum traversal depth (default 5)", required: false)] int max_depth = 5)
        {
            var db = GraphDatabase.Instance;
            if (db == null) return MCPToolResult.Error("Graph database not initialized");

            var startNodes = db.SearchByName(null, null).Where(n => n.Path == asset_path).ToList();
            if (startNodes.Count == 0)
                startNodes = db.FindNodesByType("Scene").Where(n => n.Path == asset_path).ToList();
            if (startNodes.Count == 0)
                startNodes = db.FindNodesByType("Prefab").Where(n => n.Path == asset_path).ToList();

            if (startNodes.Count == 0)
                return MCPToolResult.SuccessWithConfidence(
                    new { message = $"Asset not found: {asset_path}", dependencies = new object[0] },
                    BuildConfidence());

            var deps = db.TraverseDependencies(startNodes[0].Id, max_depth);

            var result = new JObject
            {
                ["asset"] = asset_path,
                ["max_depth"] = max_depth,
                ["dependency_count"] = deps.Count,
                ["dependencies"] = new JArray(deps.Select(d => new JObject
                {
                    ["name"] = d.Name,
                    ["type"] = d.Type,
                    ["path"] = d.Path
                }))
            };

            return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
        }

        [MCPTool("get_recently_changed",
            "Returns assets that were updated in the graph within the last N hours. Useful for understanding recent project activity.")]
        public static MCPToolResult GetRecentlyChanged(
            [MCPToolParam("Number of hours to look back", required: true)] int hours)
        {
            var db = GraphDatabase.Instance;
            if (db == null) return MCPToolResult.Error("Graph database not initialized");

            var nodes = db.GetRecentlyChanged(hours);

            var result = new JObject
            {
                ["hours"] = hours,
                ["count"] = nodes.Count,
                ["assets"] = new JArray(nodes
                    .Where(n => n.Path != null)
                    .GroupBy(n => n.Path)
                    .Select(g => new JObject
                    {
                        ["name"] = g.First().Name,
                        ["type"] = g.First().Type,
                        ["path"] = g.Key
                    }))
            };

            return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
        }

        [MCPTool("query_graph",
            "Escape hatch for complex graph queries. Accepts a structured JSON query with from, where, select, and limit fields.")]
        public static MCPToolResult QueryGraph(
            [MCPToolParam("Structured query as JSON string", required: true)] string query)
        {
            var db = GraphDatabase.Instance;
            if (db == null) return MCPToolResult.Error("Graph database not initialized");

            try
            {
                var q = JObject.Parse(query);
                var fromToken = q["from"];
                var fromType = fromToken is JValue
                    ? fromToken.ToString()
                    : fromToken?["type"]?.ToString();
                var selectFields = q["select"]?.ToObject<string[]>() ?? new[] { "name", "type", "path" };
                var limit = q["limit"]?.Value<int>() ?? 100;

                if (string.IsNullOrEmpty(fromType))
                    return MCPToolResult.Error("Query must specify 'from' as a type name string or object with 'type' field");

                var nodes = db.FindNodesByType(fromType);

                // Name-based filtering (supports SQL LIKE patterns with %)
                var whereClause = q["where"];
                var nameLike = whereClause?["name_like"]?.ToString();
                if (!string.IsNullOrEmpty(nameLike))
                {
                    var pattern = nameLike.Replace("%", "");
                    var startsWild = nameLike.StartsWith("%");
                    var endsWild = nameLike.EndsWith("%");

                    nodes = nodes.Where(n =>
                    {
                        if (n.Name == null) return false;
                        if (startsWild && endsWild)
                            return n.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (startsWild)
                            return n.Name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);
                        if (endsWild)
                            return n.Name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
                        return string.Equals(n.Name, pattern, StringComparison.OrdinalIgnoreCase);
                    }).ToList();
                }

                var pathLike = whereClause?["path_like"]?.ToString();
                if (!string.IsNullOrEmpty(pathLike))
                {
                    var pattern = pathLike.Replace("%", "");
                    var startsWild = pathLike.StartsWith("%");
                    var endsWild = pathLike.EndsWith("%");

                    nodes = nodes.Where(n =>
                    {
                        if (n.Path == null) return false;
                        if (startsWild && endsWild)
                            return n.Path.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (startsWild)
                            return n.Path.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);
                        if (endsWild)
                            return n.Path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
                        return string.Equals(n.Path, pattern, StringComparison.OrdinalIgnoreCase);
                    }).ToList();
                }

                var whereEdges = whereClause?["edges"] as JArray;
                if (whereEdges != null)
                {
                    var filtered = new List<NodeRecord>();
                    foreach (var node in nodes)
                    {
                        bool matches = true;
                        foreach (JObject edgeFilter in whereEdges)
                        {
                            var edgeType = edgeFilter["type"]?.ToString();
                            var targetType = edgeFilter["target"]?["type"]?.ToString();
                            var targetName = edgeFilter["target"]?["name"]?.ToString();

                            var edges = db.FindEdgesFrom(node.Id, edgeType);

                            if (targetType != null)
                                edges = edges.Where(e => e.TargetType == targetType).ToList();
                            if (targetName != null)
                                edges = edges.Where(e => e.TargetName == targetName).ToList();

                            if (edges.Count == 0) { matches = false; break; }
                        }
                        if (matches) filtered.Add(node);
                    }
                    nodes = filtered;
                }

                var rows = nodes.Take(limit).Select(n =>
                {
                    var row = new JObject();
                    foreach (var field in selectFields)
                    {
                        switch (field)
                        {
                            case "name": row["name"] = n.Name; break;
                            case "type": row["type"] = n.Type; break;
                            case "path": row["path"] = n.Path; break;
                            case "id": row["id"] = n.Id; break;
                            case "guid": row["guid"] = n.Guid; break;
                        }
                    }
                    return row;
                });

                var result = new JObject
                {
                    ["count"] = nodes.Count,
                    ["rows"] = new JArray(rows)
                };

                return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                return MCPToolResult.Error($"Invalid query JSON: {ex.Message}");
            }
        }

        static long GetCount(Dictionary<string, long> counts, string type)
        {
            return counts.ContainsKey(type) ? counts[type] : 0;
        }
    }
}
