using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ArcForge.Hades.Editor.Graph;

namespace ArcForge.Hades.Editor.Asphodel.Conventions
{
    public sealed class NamingConventionDetector : IConventionDetector
    {
        const int MinBucket = 5;
        public string Key => "naming";

        static string LastToken(string name)
        {
            // Split on CamelCase boundaries; return the final token (e.g. "AudioManager" -> "Manager").
            var tokens = Regex.Matches(name, "[A-Z][a-z0-9]+|[A-Z]+(?![a-z])")
                              .Select(m => m.Value).ToList();
            return tokens.Count > 0 ? tokens[tokens.Count - 1] : null;
        }

        public ConventionResult Detect(GraphDatabase db)
        {
            var buckets = new Dictionary<string, int>();
            foreach (var t in db.FindNodesByTypeAndTier("ScriptType", "project"))
            {
                var tok = LastToken(t.Name ?? "");
                if (string.IsNullOrEmpty(tok) || tok.Length < 3) continue;
                buckets[tok] = buckets.TryGetValue(tok, out var c) ? c + 1 : 1;
            }

            var strong = buckets.Where(kv => kv.Value >= MinBucket)
                                .OrderByDescending(kv => kv.Value).ToList();
            if (strong.Count == 0) return ConventionResult.NotFired();

            var parts = strong.Take(3).Select(kv => $"'{kv.Key}' ({kv.Value})");
            return new ConventionResult
            {
                Fired = true,
                TargetFile = "conventions",
                Confidence = System.Math.Min(1.0, 0.6 + strong[0].Value / 40.0),
                Statement = $"Naming conventions: common type suffixes {string.Join(", ", parts)}.",
                Evidence = $"{strong.Count} suffix bucket(s) at or above {MinBucket} project types."
            };
        }
    }
}
