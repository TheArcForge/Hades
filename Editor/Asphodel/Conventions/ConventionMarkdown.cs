namespace ArcForge.Hades.Editor.Asphodel.Conventions
{
    /// <summary>Markdown for the Tier-2 inferred view and the promotion-proposal body.</summary>
    public static class ConventionMarkdown
    {
        public static string Marker(string key) => $"<!-- hades-convention:{key} -->";

        // The always-current Tier-2 file: not authoritative, rewritten each run, self-validating.
        public static string Tier2(string key, ConventionResult r)
        {
            return
$@"---
status: inferred
source: convention-inferrer
detector: {key}
confidence: {r.Confidence:F2}
target_file: {r.TargetFile}
---
INFERRED CONVENTION (auto-derived from the project graph — not authoritative)

{r.Statement}

Evidence: {r.Evidence}

{Marker(key)}
";
        }

        // The body appended to Tier-1 on accept. Carries the marker so the inferrer can tell it was promoted.
        public static string ProposalBody(string key, ConventionResult r)
        {
            return $"{r.Statement}\n\n_Evidence: {r.Evidence}_\n{Marker(key)}";
        }
    }
}
