// Editor/Asphodel/Inference/InferenceConfig.cs
namespace ArcForge.Hades.Editor.Asphodel.Inference
{
    public class InferenceConfig
    {
        public bool Enabled = true;
        public float PromotionConfidenceThreshold = 0.9f;
        public int PromotionSampleMinimum = 50;
        public int DeferredCooldownDays = 14;
        public int MaxTraceLookbackDays = 90;
        public bool AcceptanceRateEnabled = true;
        public bool TopicClusterEnabled = true;
        public bool TimeOfDayEnabled = true;
        public bool FailureCorrelationEnabled = true;

        public static InferenceConfig LoadFromDirectory(string arcforgeDir)
        {
            var config = new InferenceConfig();
            var configPath = System.IO.Path.Combine(arcforgeDir, "config.yaml");
            if (!System.IO.File.Exists(configPath)) return config;

            foreach (var line in System.IO.File.ReadAllLines(configPath))
            {
                var trimmed = line.Trim();
                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx <= 0) continue;

                var key = trimmed.Substring(0, colonIdx).Trim();
                var value = trimmed.Substring(colonIdx + 1).Trim();

                switch (key)
                {
                    case "enabled":
                        if (bool.TryParse(value, out var e)) config.Enabled = e;
                        break;
                    case "promotion_confidence_threshold":
                        if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var pct))
                            config.PromotionConfidenceThreshold = pct;
                        break;
                    case "promotion_sample_minimum":
                        if (int.TryParse(value, out var psm)) config.PromotionSampleMinimum = psm;
                        break;
                    case "deferred_cooldown_days":
                        if (int.TryParse(value, out var dcd)) config.DeferredCooldownDays = dcd;
                        break;
                    case "max_trace_lookback_days":
                        if (int.TryParse(value, out var mtl)) config.MaxTraceLookbackDays = mtl;
                        break;
                    case "acceptance_rate":
                        if (bool.TryParse(value, out var ar)) config.AcceptanceRateEnabled = ar;
                        break;
                    case "topic_cluster":
                        if (bool.TryParse(value, out var tc)) config.TopicClusterEnabled = tc;
                        break;
                    case "time_of_day":
                        if (bool.TryParse(value, out var tod)) config.TimeOfDayEnabled = tod;
                        break;
                    case "failure_correlation":
                        if (bool.TryParse(value, out var fc)) config.FailureCorrelationEnabled = fc;
                        break;
                }
            }

            return config;
        }
    }
}
