// Editor/MCP/Tools/AsphodeTools.cs
using System.Collections.Generic;
using System.Linq;
using ArcForge.Hades.Editor.Asphodel;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;
using ArcForge.Hades.Editor.MCP;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class AsphodeTools
    {
        static MemoryManager _testManager;
        static MemoryValidator _testValidator;

        static MemoryManager GetManager() => _testManager ?? AsphodeInitializer.Manager;
        static MemoryValidator GetValidator()
        {
            if (_testValidator != null) return _testValidator;
            if (AsphodeInitializer.Validator != null) return AsphodeInitializer.Validator;

            // Lazy init: validator may not have been created if GraphDatabase wasn't ready at startup
            var manager = GetManager();
            var db = GraphDatabase.Instance;
            if (manager != null && db != null)
            {
                AsphodeInitializer.InitValidator(manager, db);
                return AsphodeInitializer.Validator;
            }

            return null;
        }

        public static void SetTestManager(MemoryManager m) { _testManager = m; }
        public static void SetTestValidator(MemoryValidator v) { _testValidator = v; }
        public static void ClearTestOverrides() { _testManager = null; _testValidator = null; }

        static ConfidenceBlock BuildConfidence()
        {
            var manager = GetManager();
            if (manager == null)
                return ConfidenceBlock.Low("error").WithFactor("memory_availability", "unavailable");

            return ConfidenceBlock.High().WithFactor("memory_source", "tier1_explicit");
        }

        [MCPTool("get_memory_summary",
            "Returns a brief summary of all project memory files (decisions, patterns, conventions, etc.) for context injection. Includes validation status and any warnings.")]
        public static MCPToolResult GetMemorySummary()
        {
            using (var span = CharonEmitter.StartSpan("memory.read.tier1", SpanKind.Internal))
            {
                var manager = GetManager();
                if (manager == null) return MCPToolResult.Error("Memory not initialized");

                var files = manager.ListFiles();
                span.SetAttribute("file_count", (long)files.Count);

                var summaries = new JArray();
                long totalSize = 0;

                foreach (var file in files)
                {
                    var bodyPreview = file.Body.Length > 500 ? file.Body.Substring(0, 500) + "..." : file.Body;
                    totalSize += file.Body.Length;

                    var entry = new JObject
                    {
                        ["filename"] = file.Filename,
                        ["validation_status"] = file.ValidationStatus,
                        ["last_reviewed"] = file.LastReviewed,
                        ["preview"] = bodyPreview.Trim()
                    };

                    if (file.ValidationStatus == "warning" || file.ValidationStatus == "error")
                        entry["has_warnings"] = true;

                    summaries.Add(entry);
                }

                var inferredDir = System.IO.Path.Combine(manager.MemoryDir, "inferred");
                if (System.IO.Directory.Exists(inferredDir))
                {
                    foreach (var filePath in System.IO.Directory.GetFiles(inferredDir, "*.md"))
                    {
                        var content = System.IO.File.ReadAllText(filePath);
                        var memFile = FrontmatterParser.Parse(content);
                        var confidence = memFile.Frontmatter.ContainsKey("confidence") ? memFile.Frontmatter["confidence"] : "?";
                        var sampleSize = memFile.Frontmatter.ContainsKey("sample_size") ? memFile.Frontmatter["sample_size"] : "?";
                        var analyzer = memFile.Frontmatter.ContainsKey("analyzer") ? memFile.Frontmatter["analyzer"] : "unknown";
                        var preview = memFile.Body.Length > 100 ? memFile.Body.Substring(0, 100) + "..." : memFile.Body;

                        var inferredEntry = new Newtonsoft.Json.Linq.JObject
                        {
                            ["filename"] = System.IO.Path.GetFileName(filePath),
                            ["tier"] = "inferred",
                            ["analyzer"] = analyzer,
                            ["confidence"] = confidence,
                            ["sample_size"] = sampleSize,
                            ["preview"] = preview.Trim()
                        };
                        summaries.Add(inferredEntry);
                    }
                }

                span.SetAttribute("content_size", totalSize);

                var result = new JObject
                {
                    ["files"] = summaries,
                    ["total_files"] = files.Count
                };

                return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
            }
        }

        [MCPTool("recall_memory",
            "Searches project memory files for content matching the query. Returns relevant sections from memory files.")]
        public static MCPToolResult RecallMemory(
            [MCPToolParam("Search query — keywords to find in memory files", required: true)] string query)
        {
            using (var span = CharonEmitter.StartSpan("memory.read.tier1", SpanKind.Internal))
            {
                span.SetAttribute("query", query);

                var manager = GetManager();
                if (manager == null) return MCPToolResult.Error("Memory not initialized");

                var files = manager.ListFiles();
                var matches = new JArray();
                var terms = query.ToLowerInvariant().Split(' ');

                foreach (var file in files)
                {
                    var lines = file.Body.Split('\n');
                    var matchedSections = new List<string>();

                    for (int i = 0; i < lines.Length; i++)
                    {
                        var lower = lines[i].ToLowerInvariant();
                        bool hit = false;
                        foreach (var term in terms)
                        {
                            if (lower.Contains(term)) { hit = true; break; }
                        }

                        if (hit)
                        {
                            int start = System.Math.Max(0, i - 2);
                            int end = System.Math.Min(lines.Length - 1, i + 5);
                            var section = string.Join("\n", lines, start, end - start + 1);
                            matchedSections.Add(section);
                            i = end;
                        }
                    }

                    if (matchedSections.Count > 0)
                    {
                        matches.Add(new JObject
                        {
                            ["filename"] = file.Filename,
                            ["validation_status"] = file.ValidationStatus,
                            ["sections"] = new JArray(matchedSections.ToArray())
                        });
                    }
                }

                var inferredDir = System.IO.Path.Combine(manager.MemoryDir, "inferred");
                if (System.IO.Directory.Exists(inferredDir))
                {
                    foreach (var filePath in System.IO.Directory.GetFiles(inferredDir, "*.md"))
                    {
                        var content = System.IO.File.ReadAllText(filePath);
                        if (content.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) < 0) continue;

                        var memFile = FrontmatterParser.Parse(content);
                        var confidence = memFile.Frontmatter.ContainsKey("confidence") ? memFile.Frontmatter["confidence"] : "?";
                        var analyzer = memFile.Frontmatter.ContainsKey("analyzer") ? memFile.Frontmatter["analyzer"] : "unknown";

                        var matchEntry = new Newtonsoft.Json.Linq.JObject
                        {
                            ["filename"] = System.IO.Path.GetFileName(filePath),
                            ["tier"] = "inferred",
                            ["analyzer"] = analyzer,
                            ["confidence"] = confidence,
                            ["validation_status"] = "inferred",
                            ["sections"] = new Newtonsoft.Json.Linq.JArray { memFile.Body.Trim() }
                        };
                        matches.Add(matchEntry);
                    }
                }

                span.SetAttribute("matches", (long)matches.Count);

                var result = new JObject
                {
                    ["query"] = query,
                    ["match_count"] = matches.Count,
                    ["matches"] = matches
                };

                return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
            }
        }

        [MCPTool("propose_memory_update",
            "Proposes an update to a project memory file. The proposal is queued for human review — it does NOT modify the file directly.")]
        public static MCPToolResult ProposeMemoryUpdate(
            [MCPToolParam("Target memory file name (e.g. patterns, decisions)", required: true)] string file,
            [MCPToolParam("Proposed content to add (markdown)", required: true)] string content,
            [MCPToolParam("Rationale for the proposed update", required: true)] string rationale)
        {
            using (var span = CharonEmitter.StartSpan("memory.write.tier1.proposal", SpanKind.Internal))
            {
                span.SetAttribute("file_path", file + ".md");
                span.SetAttribute("rationale", rationale);

                var manager = GetManager();
                if (manager == null) return MCPToolResult.Error("Memory not initialized");

                var id = manager.CreateProposal(file, content, rationale);

                span.SetAttribute("proposal_id", id);

                var result = new JObject
                {
                    ["status"] = "created",
                    ["proposal_id"] = id,
                    ["target_file"] = file + ".md",
                    ["message"] = "Proposal queued for human review in .arcforge/memory/proposals/"
                };

                return MCPToolResult.SuccessWithConfidence(result, BuildConfidence());
            }
        }

        [MCPTool("validate_memory",
            "Triggers validation of memory files against the current graph state. Checks validation rules and updates status.")]
        public static MCPToolResult ValidateMemoryTool(
            [MCPToolParam("Optional: specific file to validate (e.g. patterns). Empty for all files.", required: false)] string file = "")
        {
            using (var span = CharonEmitter.StartSpan("memory.validate", SpanKind.Internal))
            {
                var validator = GetValidator();
                if (validator == null) return MCPToolResult.Error("Validator not initialized");

                if (!string.IsNullOrEmpty(file))
                {
                    span.SetAttribute("file_path", file + ".md");
                    var result = validator.ValidateFile(file);

                    return MCPToolResult.SuccessWithConfidence(new JObject
                    {
                        ["filename"] = result.Filename,
                        ["status"] = result.Status,
                        ["rules_checked"] = result.RulesChecked,
                        ["rules_passed"] = result.RulesPassed,
                        ["rules_warning"] = result.RulesWarning,
                        ["warnings"] = new JArray(result.Warnings)
                    }, BuildConfidence());
                }
                else
                {
                    var results = validator.ValidateAll();
                    span.SetAttribute("files_validated", (long)results.Count);

                    var arr = new JArray();
                    foreach (var r in results)
                    {
                        arr.Add(new JObject
                        {
                            ["filename"] = r.Filename,
                            ["status"] = r.Status,
                            ["rules_checked"] = r.RulesChecked,
                            ["rules_warning"] = r.RulesWarning
                        });
                    }

                    return MCPToolResult.SuccessWithConfidence(new JObject
                    {
                        ["results"] = arr,
                        ["total_files"] = results.Count
                    }, BuildConfidence());
                }
            }
        }
    }
}
