// Editor/Asphodel/MemoryValidator.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;
using Debug = UnityEngine.Debug;

namespace ArcForge.Hades.Editor.Asphodel
{
    public class MemoryValidator
    {
        readonly MemoryManager _manager;
        readonly GraphDatabase _db;
        int _queryBudgetMs;

        public MemoryValidator(MemoryManager manager, GraphDatabase db, int queryBudgetMs = 1000)
        {
            _manager = manager;
            _db = db;
            _queryBudgetMs = queryBudgetMs;
        }

        public List<ValidationResult> ValidateAll()
        {
            var results = new List<ValidationResult>();
            foreach (var file in _manager.ListFiles())
            {
                var name = file.Filename.EndsWith(".md")
                    ? file.Filename.Substring(0, file.Filename.Length - 3)
                    : file.Filename;
                results.Add(ValidateFile(name));
            }
            return results;
        }

        public ValidationResult ValidateFile(string name)
        {
            using (var span = CharonEmitter.StartSpan("memory.validate", SpanKind.Internal))
            {
                span.SetAttribute("file_path", name + ".md");

                var file = _manager.ReadFile(name);
                if (file == null)
                {
                    span.SetStatus(SpanStatus.Error);
                    return new ValidationResult { Filename = name, Status = "error", Warnings = new[] { "File not found" } };
                }

                var rules = ValidationRuleParser.Parse(file.Body);
                span.SetAttribute("rules_checked", (long)rules.Count);

                int passed = 0;
                int warning = 0;
                int skipped = 0;
                var warnings = new List<string>();
                var body = file.Body;

                body = ClearOldWarnings(body);

                foreach (var rule in rules)
                {
                    try
                    {
                        var count = ExecuteQuery(rule);
                        if (count >= rule.MinCount)
                        {
                            passed++;
                        }
                        else
                        {
                            warning++;
                            var msg = $"{rule.FailureMessage}\nFound {count} matching assets.";
                            warnings.Add(msg);
                            body = InsertWarningAfterRule(body, rule, count);
                        }
                    }
                    catch (TimeoutException)
                    {
                        skipped++;
                        warnings.Add($"Query timed out (>{_queryBudgetMs}ms): {rule.Query}");
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        warnings.Add($"Query error: {ex.Message}");
                    }
                }

                var status = warning > 0 ? "warning" : "ok";

                file.Frontmatter["validation_status"] = status;
                file.Frontmatter["last_validated_against_graph"] = DateTime.UtcNow.ToString("o");
                file.Body = body;
                _manager.WriteMemoryFile(file);

                span.SetAttribute("validation_result", status);

                return new ValidationResult
                {
                    Filename = name,
                    Status = status,
                    RulesChecked = rules.Count,
                    RulesPassed = passed,
                    RulesWarning = warning,
                    RulesSkipped = skipped,
                    Warnings = warnings.ToArray()
                };
            }
        }

        int ExecuteQuery(ValidationRule rule)
        {
            if (_db == null) return 0;

            var sw = Stopwatch.StartNew();

            var match = Regex.Match(rule.Query, @"^(\w+)\((.+)\)$");
            if (!match.Success)
                throw new ArgumentException($"Invalid query format: {rule.Query}");

            var method = match.Groups[1].Value;
            var argsRaw = match.Groups[2].Value;
            var args = SplitArgs(argsRaw);

            int result;
            switch (method)
            {
                case "search_by_name":
                    if (args.Length < 1) throw new ArgumentException("search_by_name requires at least 1 argument");
                    var pattern = args[0].Replace("*", "%");
                    var typeFilter = args.Length > 1 ? args[1] : null;
                    result = _db.SearchByName(pattern, typeFilter).Count;
                    break;

                case "find_nodes_by_type":
                    if (args.Length < 1) throw new ArgumentException("find_nodes_by_type requires 1 argument");
                    result = _db.FindNodesByType(args[0]).Count;
                    break;

                default:
                    throw new ArgumentException($"Unknown query method: {method}");
            }

            sw.Stop();
            if (sw.ElapsedMilliseconds > _queryBudgetMs)
                throw new TimeoutException($"Query exceeded budget: {sw.ElapsedMilliseconds}ms > {_queryBudgetMs}ms");

            return result;
        }

        static string[] SplitArgs(string argsStr)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            int depth = 0;

            foreach (var ch in argsStr)
            {
                if (ch == '(' ) depth++;
                else if (ch == ')') depth--;
                else if (ch == ',' && depth == 0)
                {
                    args.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
                current.Append(ch);
            }

            if (current.Length > 0)
                args.Add(current.ToString().Trim());

            return args.ToArray();
        }

        static string ClearOldWarnings(string body)
        {
            return Regex.Replace(body,
                @"\n?<!-- HADES VALIDATION WARNING \([^)]+\):\n[^-]*?-->\n?",
                "\n",
                RegexOptions.Singleline);
        }

        static string InsertWarningAfterRule(string body, ValidationRule rule, int actualCount)
        {
            var lines = body.Split('\n');
            if (rule.SourceLineEnd >= lines.Length) return body;

            var warningComment = $"\n<!-- HADES VALIDATION WARNING ({DateTime.UtcNow:yyyy-MM-dd}):\n{rule.FailureMessage}\nFound {actualCount} matching assets. -->";

            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append('\n');

                if (i == rule.SourceLineEnd)
                    sb.Append(warningComment);
            }
            return sb.ToString();
        }
    }
}
