using System.Collections.Generic;
using System.Linq;
using ArcForge.Hades.Editor.Graph;

namespace ArcForge.Hades.Editor.Asphodel.Conventions
{
    /// <summary>Detects the ScriptableObject event-channel communication pattern.</summary>
    public sealed class EventChannelDetector : IConventionDetector
    {
        public string Key => "event_channels";

        static bool IsChannelTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            var t = typeName.Trim();
            return t.EndsWith("Channel") || t.EndsWith("EventChannel") || t.EndsWith("Event");
        }

        public ConventionResult Detect(GraphDatabase db)
        {
            var sos = db.FindNodesByType("ScriptableObject");
            var channelTypes = new HashSet<string>();
            int referencedInstances = 0;
            var examples = new List<string>();

            foreach (var so in sos)
            {
                string soType = null;
                if (so.Properties != null && so.Properties.TryGetValue("so_type", out var v)) soType = v?.ToString();
                var typeName = soType ?? so.Name;                       // fall back to instance name
                if (!IsChannelTypeName(typeName)) continue;

                // Is it referenced by anything (a component wiring the channel)?
                var referrers = db.FindNodesWithEdgeTo(so.Id, "references");
                if (referrers.Count == 0) continue;

                channelTypes.Add(soType ?? so.Name);
                referencedInstances++;
                if (examples.Count < 3) examples.Add(so.Name);
            }

            if (channelTypes.Count < 2 || referencedInstances < 3) return ConventionResult.NotFired();

            var confidence = System.Math.Min(1.0, 0.6 + referencedInstances * 0.05);
            return new ConventionResult
            {
                Fired = true,
                TargetFile = "patterns",
                Confidence = confidence,
                Statement = $"Uses ScriptableObject event channels for decoupled communication (e.g. {string.Join(", ", examples)}).",
                Evidence = $"{referencedInstances} referenced channel assets across {channelTypes.Count} types."
            };
        }
    }
}
