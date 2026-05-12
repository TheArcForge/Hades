// Editor/Asphodel/ValidationRuleParser.cs
using System.Collections.Generic;
using UnityEngine;

namespace ArcForge.Hades.Editor.Asphodel
{
    public static class ValidationRuleParser
    {
        const string OpenTag = "<!-- hades-validation";
        const string CloseTag = "-->";

        public static List<ValidationRule> Parse(string body)
        {
            var rules = new List<ValidationRule>();
            if (string.IsNullOrEmpty(body)) return rules;

            var lines = body.Split('\n');

            int i = 0;
            while (i < lines.Length)
            {
                var trimmed = lines[i].TrimEnd('\r').Trim();
                if (trimmed == OpenTag)
                {
                    int startLine = i;
                    var kvPairs = new Dictionary<string, string>();
                    i++;

                    while (i < lines.Length)
                    {
                        var line = lines[i].TrimEnd('\r').Trim();
                        if (line == CloseTag)
                        {
                            break;
                        }

                        var colonIdx = line.IndexOf(':');
                        if (colonIdx > 0)
                        {
                            var key = line.Substring(0, colonIdx).Trim();
                            var val = line.Substring(colonIdx + 1).Trim();
                            kvPairs[key] = val;
                        }
                        i++;
                    }

                    int endLine = i;

                    string queryType, query, failureMessage;
                    int minCount;

                    if (kvPairs.TryGetValue("query_type", out queryType) &&
                        kvPairs.TryGetValue("query", out query) &&
                        kvPairs.TryGetValue("failure_message", out failureMessage))
                    {
                        string minCountStr;
                        if (!kvPairs.TryGetValue("min_count", out minCountStr) ||
                            !int.TryParse(minCountStr, out minCount))
                            minCount = 1;

                        rules.Add(new ValidationRule
                        {
                            QueryType = queryType,
                            Query = query,
                            MinCount = minCount,
                            FailureMessage = failureMessage,
                            SourceLineStart = startLine,
                            SourceLineEnd = endLine
                        });
                    }
                    else
                    {
                        Debug.LogWarning($"[Hades Asphodel] Malformed validation rule at line {startLine}, skipping");
                    }
                }
                i++;
            }

            return rules;
        }
    }
}
