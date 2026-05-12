// Tests/Editor/Asphodel/ValidationRuleParserTests.cs
using System.Linq;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel;

namespace ArcForge.Hades.Editor.Tests.Asphodel
{
    public class ValidationRuleParserTests
    {
        [Test]
        public void Parse_SingleRule_ExtractsCorrectly()
        {
            var body = "### SO Event Channels\n\n<!-- hades-validation\nquery_type: exists\nquery: search_by_name(*Channel, ScriptableObject)\nmin_count: 3\nfailure_message: Found fewer than 3 SO event channels.\n-->\n\nWe use SO event channels.";

            var rules = ValidationRuleParser.Parse(body);

            Assert.AreEqual(1, rules.Count);
            Assert.AreEqual("exists", rules[0].QueryType);
            Assert.AreEqual("search_by_name(*Channel, ScriptableObject)", rules[0].Query);
            Assert.AreEqual(3, rules[0].MinCount);
            Assert.AreEqual("Found fewer than 3 SO event channels.", rules[0].FailureMessage);
        }

        [Test]
        public void Parse_MultipleRules_ExtractsAll()
        {
            var body = "### Pattern A\n\n<!-- hades-validation\nquery_type: exists\nquery: find_nodes_by_type(Prefab)\nmin_count: 1\nfailure_message: No prefabs.\n-->\n\n### Pattern B\n\n<!-- hades-validation\nquery_type: exists\nquery: search_by_name(Player%, Script)\nmin_count: 2\nfailure_message: No player scripts.\n-->\n";

            var rules = ValidationRuleParser.Parse(body);
            Assert.AreEqual(2, rules.Count);
            Assert.AreEqual("find_nodes_by_type(Prefab)", rules[0].Query);
            Assert.AreEqual("search_by_name(Player%, Script)", rules[1].Query);
        }

        [Test]
        public void Parse_NoRules_ReturnsEmpty()
        {
            var body = "# Just markdown\n\nNo validation here.";
            var rules = ValidationRuleParser.Parse(body);
            Assert.AreEqual(0, rules.Count);
        }

        [Test]
        public void Parse_MalformedRule_SkipsIt()
        {
            var body = "<!-- hades-validation\nthis is broken\n-->\n";
            var rules = ValidationRuleParser.Parse(body);
            Assert.AreEqual(0, rules.Count);
        }

        [Test]
        public void Parse_RecordsSourceLines()
        {
            var body = "Line 0\n<!-- hades-validation\nquery_type: exists\nquery: find_nodes_by_type(Scene)\nmin_count: 1\nfailure_message: No scenes.\n-->\nLine after";

            var rules = ValidationRuleParser.Parse(body);
            Assert.AreEqual(1, rules.Count);
            Assert.AreEqual(1, rules[0].SourceLineStart);
            Assert.AreEqual(6, rules[0].SourceLineEnd);
        }
    }
}
