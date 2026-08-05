using ArcForge.Hades.Editor.Graph;

namespace ArcForge.Hades.Editor.Asphodel.Conventions
{
    public sealed class PrefabVariantDetector : IConventionDetector
    {
        const int MinTotal = 5;
        const double MinRatio = 0.2;
        public string Key => "prefab_variants";

        public ConventionResult Detect(GraphDatabase db)
        {
            var counts = db.GetTypeCounts();
            counts.TryGetValue("Prefab", out var prefabs);
            counts.TryGetValue("PrefabVariant", out var variants);
            var total = prefabs + variants;
            if (total < MinTotal) return ConventionResult.NotFired();
            var ratio = variants / (double)total;
            if (ratio < MinRatio) return ConventionResult.NotFired();

            return new ConventionResult
            {
                Fired = true,
                TargetFile = "patterns",
                Confidence = System.Math.Min(1.0, 0.6 + ratio),
                Statement = "Relies on prefab variants for shared-base composition.",
                Evidence = $"{variants} of {total} prefabs are variants ({ratio:P0})."
            };
        }
    }
}
