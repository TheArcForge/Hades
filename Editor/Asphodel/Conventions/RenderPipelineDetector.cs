using System.Linq;
using ArcForge.Hades.Editor.Graph;

namespace ArcForge.Hades.Editor.Asphodel.Conventions
{
    public sealed class RenderPipelineDetector : IConventionDetector
    {
        public string Key => "render_pipeline";

        public ConventionResult Detect(GraphDatabase db)
        {
            var rp = db.FindNodesByType("RenderPipelineAsset").FirstOrDefault();
            if (rp == null) return ConventionResult.NotFired();

            string typeName = null;
            if (rp.Properties != null && rp.Properties.TryGetValue("pipeline_type", out var v)) typeName = v?.ToString();
            typeName = typeName ?? "";

            string label =
                typeName.Contains("Universal") ? "URP (Universal Render Pipeline)" :
                typeName.Contains("HD") ? "HDRP (High Definition Render Pipeline)" :
                typeName;
            if (string.IsNullOrEmpty(label)) return ConventionResult.NotFired();

            return new ConventionResult
            {
                Fired = true,
                TargetFile = "conventions",
                Confidence = 0.95,
                Statement = $"Targets {label}.",
                Evidence = $"RenderPipelineAsset pipeline_type = {typeName}."
            };
        }
    }
}
