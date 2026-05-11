using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Graph.Models
{
    public class ConfidenceFactor
    {
        [JsonProperty("factor")]
        public string Factor { get; set; }

        [JsonProperty("value")]
        public string Value { get; set; }

        [JsonProperty("blind_spots", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> BlindSpots { get; set; }
    }

    public class ConfidenceBlock
    {
        [JsonProperty("level")]
        public string Level { get; set; }

        [JsonProperty("factors")]
        public List<ConfidenceFactor> Factors { get; set; } = new List<ConfidenceFactor>();

        [JsonProperty("recommendations")]
        public List<string> Recommendations { get; set; } = new List<string>();

        [JsonProperty("result_status")]
        public string ResultStatus { get; set; }

        public static ConfidenceBlock High()
        {
            return new ConfidenceBlock { Level = "high", ResultStatus = "complete" };
        }

        public static ConfidenceBlock Medium(string resultStatus = "partial")
        {
            return new ConfidenceBlock { Level = "medium", ResultStatus = resultStatus };
        }

        public static ConfidenceBlock Low(string resultStatus = "uncertain")
        {
            return new ConfidenceBlock { Level = "low", ResultStatus = resultStatus };
        }

        public ConfidenceBlock WithFactor(string factor, string value, List<string> blindSpots = null)
        {
            Factors.Add(new ConfidenceFactor
            {
                Factor = factor,
                Value = value,
                BlindSpots = blindSpots
            });
            return this;
        }

        public ConfidenceBlock WithRecommendation(string recommendation)
        {
            Recommendations.Add(recommendation);
            return this;
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }

        public JObject ToJObject()
        {
            return JObject.FromObject(this);
        }
    }
}
