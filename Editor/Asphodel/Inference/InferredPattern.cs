// Editor/Asphodel/Inference/InferredPattern.cs
using System;
using System.Security.Cryptography;
using System.Text;

namespace ArcForge.Hades.Editor.Asphodel.Inference
{
    public enum PromotionStatus
    {
        Pending,
        Proposed,
        Accepted,
        Dismissed,
        Deferred
    }

    public class InferredPattern
    {
        public string Id;
        public string AnalyzerName;
        public string PatternKey;
        public string Description;
        public string TargetFile;
        public float Confidence;
        public int SampleSize;
        public DateTimeOffset FirstObserved;
        public DateTimeOffset LastConfirmed;
        public PromotionStatus PromotionStatus;
        public string ConflictsWith;
        public string ConflictDetail;

        public static string ComputeId(string analyzerName, string patternKey)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(analyzerName + ":" + patternKey));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("status: inferred");
            sb.AppendLine($"analyzer: {AnalyzerName}");
            sb.AppendLine($"confidence: {Confidence.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine($"sample_size: {SampleSize}");
            sb.AppendLine($"first_observed: {FirstObserved:yyyy-MM-dd}");
            sb.AppendLine($"last_confirmed: {LastConfirmed:yyyy-MM-dd}");
            sb.AppendLine($"promotion_status: {PromotionStatus.ToString().ToLowerInvariant()}");
            if (!string.IsNullOrEmpty(ConflictsWith))
            {
                sb.AppendLine($"conflicts_with: {ConflictsWith}");
                sb.AppendLine($"conflict_detail: {ConflictDetail}");
            }
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("INFERRED PATTERN (not confirmed by team)");
            sb.AppendLine();
            sb.AppendLine(Description);
            sb.AppendLine();
            sb.AppendLine($"Observed in {SampleSize} traces with {(Confidence * 100):F0}% confidence.");
            return sb.ToString();
        }

        public static InferredPattern FromMemoryFile(MemoryFile file)
        {
            if (file == null) return null;
            var fm = file.Frontmatter;
            if (!fm.ContainsKey("status") || fm["status"] != "inferred") return null;

            var pattern = new InferredPattern();
            pattern.AnalyzerName = fm.ContainsKey("analyzer") ? fm["analyzer"] : "";
            pattern.Confidence = fm.ContainsKey("confidence") &&
                float.TryParse(fm["confidence"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0f;
            pattern.SampleSize = fm.ContainsKey("sample_size") &&
                int.TryParse(fm["sample_size"], out var s) ? s : 0;
            pattern.FirstObserved = fm.ContainsKey("first_observed") &&
                DateTimeOffset.TryParse(fm["first_observed"], out var fo) ? fo : DateTimeOffset.UtcNow;
            pattern.LastConfirmed = fm.ContainsKey("last_confirmed") &&
                DateTimeOffset.TryParse(fm["last_confirmed"], out var lc) ? lc : DateTimeOffset.UtcNow;
            pattern.PromotionStatus = fm.ContainsKey("promotion_status") &&
                Enum.TryParse<PromotionStatus>(fm["promotion_status"], true, out var ps)
                    ? ps : PromotionStatus.Pending;
            pattern.ConflictsWith = fm.ContainsKey("conflicts_with") ? fm["conflicts_with"] : null;
            pattern.ConflictDetail = fm.ContainsKey("conflict_detail") ? fm["conflict_detail"] : null;

            var bodyLines = file.Body.Split('\n');
            var descLines = new StringBuilder();
            bool pastHeader = false;
            foreach (var line in bodyLines)
            {
                if (!pastHeader)
                {
                    if (line.Trim().StartsWith("INFERRED PATTERN")) { pastHeader = true; continue; }
                    continue;
                }
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Observed in ") && trimmed.Contains("traces with")) continue;
                if (!string.IsNullOrEmpty(trimmed)) descLines.AppendLine(trimmed);
            }
            pattern.Description = descLines.ToString().Trim();

            var baseName = System.IO.Path.GetFileNameWithoutExtension(file.Filename);
            var dashIdx = baseName.LastIndexOf('-');
            if (dashIdx > 0)
            {
                pattern.Id = baseName.Substring(dashIdx + 1);
                pattern.PatternKey = pattern.Description;
            }

            return pattern;
        }
    }
}
