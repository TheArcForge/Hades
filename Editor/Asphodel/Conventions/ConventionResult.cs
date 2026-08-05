namespace ArcForge.Hades.Editor.Asphodel.Conventions
{
    /// <summary>Output of one convention detector for one graph scan.</summary>
    public sealed class ConventionResult
    {
        public bool Fired;            // signal strong enough to surface?
        public string Statement;      // human-readable convention, e.g. "Uses ScriptableObject event channels…"
        public string Evidence;       // "6 channel assets across 3 types, referenced by 14 components"
        public double Confidence;     // 0..1, prevalence-based
        public string TargetFile;     // Tier-1 target on accept: "patterns" | "conventions"

        public static ConventionResult NotFired() => new ConventionResult { Fired = false };
    }
}
