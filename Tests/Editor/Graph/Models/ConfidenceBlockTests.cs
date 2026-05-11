using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Models;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests.Graph.Models
{
    public class ConfidenceBlockTests
    {
        [Test]
        public void HighConfidence_DefaultFactoryMethod()
        {
            var block = ConfidenceBlock.High();

            Assert.AreEqual("high", block.Level);
            Assert.AreEqual("complete", block.ResultStatus);
            Assert.IsEmpty(block.Factors);
            Assert.IsEmpty(block.Recommendations);
        }

        [Test]
        public void WithFactor_AddsFactor()
        {
            var block = ConfidenceBlock.High()
                .WithFactor("graph_freshness", "current");

            Assert.AreEqual(1, block.Factors.Count);
            Assert.AreEqual("graph_freshness", block.Factors[0].Factor);
            Assert.AreEqual("current", block.Factors[0].Value);
        }

        [Test]
        public void WithFactor_BlindSpots()
        {
            var block = ConfidenceBlock.Medium("partial")
                .WithFactor("static_analysis_coverage", "partial",
                    blindSpots: new List<string> { "reflection", "DI container" });

            Assert.AreEqual("medium", block.Level);
            Assert.AreEqual("partial", block.ResultStatus);
            Assert.AreEqual(2, block.Factors[0].BlindSpots.Count);
        }

        [Test]
        public void ToJson_ProducesExpectedShape()
        {
            var block = ConfidenceBlock.High()
                .WithFactor("graph_freshness", "current");

            var json = block.ToJson();
            var obj = JObject.Parse(json);

            Assert.AreEqual("high", obj["level"].ToString());
            Assert.AreEqual("complete", obj["result_status"].ToString());
            Assert.AreEqual("graph_freshness", obj["factors"][0]["factor"].ToString());
        }
    }
}
