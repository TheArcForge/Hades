using ArcForge.Hades.Editor.Graph;

namespace ArcForge.Hades.Editor.Asphodel.Conventions
{
    /// <summary>
    /// A single deterministic convention signal read from the knowledge graph. Detectors are
    /// pure (read-only over the graph) and independently testable against a seeded temp DB.
    /// </summary>
    public interface IConventionDetector
    {
        /// <summary>Stable id (kebab/snake), used for file + proposal + ledger keys. e.g. "event_channels".</summary>
        string Key { get; }

        ConventionResult Detect(GraphDatabase db);
    }
}
