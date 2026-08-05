using System.Collections.Generic;
using System.Linq;
using ArcForge.Hades.Editor.Graph;

namespace ArcForge.Hades.Editor.Asphodel.Conventions
{
    public sealed class ScriptableObjectConfigDetector : IConventionDetector
    {
        const int MinInstances = 10;
        const int MinTypes = 3;
        public string Key => "so_config";

        static bool IsChannel(string t) =>
            !string.IsNullOrEmpty(t) && (t.EndsWith("Channel") || t.EndsWith("EventChannel") || t.EndsWith("Event"));

        public ConventionResult Detect(GraphDatabase db)
        {
            var byType = new Dictionary<string, int>();
            foreach (var so in db.FindNodesByType("ScriptableObject"))
            {
                string t = null;
                if (so.Properties != null && so.Properties.TryGetValue("so_type", out var v)) t = v?.ToString();
                t = t ?? so.Name;
                if (IsChannel(t)) continue;
                byType[t] = byType.TryGetValue(t, out var c) ? c + 1 : 1;
            }

            var configTypes = byType.Where(kv => kv.Value >= 2).ToList();
            var instances = configTypes.Sum(kv => kv.Value);
            if (instances < MinInstances || configTypes.Count < MinTypes) return ConventionResult.NotFired();

            var examples = configTypes.OrderByDescending(kv => kv.Value).Take(3)
                .Select(kv => kv.Key.Split('.').Last());
            return new ConventionResult
            {
                Fired = true,
                TargetFile = "patterns",
                Confidence = System.Math.Min(1.0, 0.6 + instances / 100.0),
                Statement = "Stores configuration/data in ScriptableObjects.",
                Evidence = $"{instances} instances across {configTypes.Count} types (e.g. {string.Join(", ", examples)})."
            };
        }
    }
}
