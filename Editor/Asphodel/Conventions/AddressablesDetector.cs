using ArcForge.Hades.Editor.Graph;

namespace ArcForge.Hades.Editor.Asphodel.Conventions
{
    /// <summary>Detects Addressables adoption. (Resources.Load usage is not captured by the graph.)</summary>
    public sealed class AddressablesDetector : IConventionDetector
    {
        const int MinEntries = 10;
        public string Key => "asset_loading";

        public ConventionResult Detect(GraphDatabase db)
        {
            var counts = db.GetTypeCounts();
            counts.TryGetValue("AddressableGroup", out var groups);
            counts.TryGetValue("AddressableEntry", out var entries);
            if (entries < MinEntries) return ConventionResult.NotFired();

            return new ConventionResult
            {
                Fired = true,
                TargetFile = "conventions",
                Confidence = System.Math.Min(1.0, 0.6 + entries / 200.0),
                Statement = "Uses Addressables for asset loading.",
                Evidence = $"{groups} addressable group(s), {entries} entries."
            };
        }
    }
}
