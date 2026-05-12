// Tests/Editor/Asphodel/FrontmatterParserTests.cs
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel;

namespace ArcForge.Hades.Editor.Tests.Asphodel
{
    public class FrontmatterParserTests
    {
        [Test]
        public void Parse_ValidFrontmatter_ExtractsFrontmatterAndBody()
        {
            var markdown = "---\nlast_reviewed: 2026-05-12\nvalidation_status: ok\n---\n# Decisions\n\nSome content.";
            var result = FrontmatterParser.Parse(markdown);

            Assert.AreEqual("2026-05-12", result.Frontmatter["last_reviewed"]);
            Assert.AreEqual("ok", result.Frontmatter["validation_status"]);
            Assert.IsTrue(result.Body.StartsWith("# Decisions"));
        }

        [Test]
        public void Parse_NoFrontmatter_ReturnsEmptyFrontmatterAndFullBody()
        {
            var markdown = "# Just a heading\n\nSome text.";
            var result = FrontmatterParser.Parse(markdown);

            Assert.AreEqual(0, result.Frontmatter.Count);
            Assert.AreEqual(markdown, result.Body);
        }

        [Test]
        public void Parse_EmptyContent_ReturnsEmptyResult()
        {
            var result = FrontmatterParser.Parse("");

            Assert.AreEqual(0, result.Frontmatter.Count);
            Assert.AreEqual("", result.Body);
        }

        [Test]
        public void Parse_MalformedFrontmatter_ReturnsEmptyFrontmatterAndFullBody()
        {
            var markdown = "---\nthis is not yaml at all ::::\n---\n# Content";
            var result = FrontmatterParser.Parse(markdown);

            Assert.AreEqual(0, result.Frontmatter.Count);
            Assert.IsTrue(result.Body.Contains("# Content"));
        }

        [Test]
        public void Parse_FrontmatterWithColonsInValue_ParsesCorrectly()
        {
            var markdown = "---\nlast_validated_against_graph: 2026-05-12T10:30:00\n---\nBody";
            var result = FrontmatterParser.Parse(markdown);

            Assert.AreEqual("2026-05-12T10:30:00", result.Frontmatter["last_validated_against_graph"]);
        }

        [Test]
        public void Parse_UnclosedFrontmatter_TreatsAsNoFrontmatter()
        {
            var markdown = "---\nkey: value\n# No closing delimiter";
            var result = FrontmatterParser.Parse(markdown);

            Assert.AreEqual(0, result.Frontmatter.Count);
        }

        [Test]
        public void Roundtrip_PreservesBodyContent()
        {
            var original = "---\nvalidation_status: ok\n---\n# Title\n\nBody with **markdown**.";
            var parsed = FrontmatterParser.Parse(original);
            parsed.Frontmatter["validation_status"] = "warning";
            var output = parsed.ToMarkdown();
            var reparsed = FrontmatterParser.Parse(output);

            Assert.AreEqual("warning", reparsed.Frontmatter["validation_status"]);
            Assert.IsTrue(reparsed.Body.Contains("Body with **markdown**."));
        }

        [Test]
        public void Parse_BodyWithTripleDashInContent_DoesNotConfuse()
        {
            var markdown = "---\nkey: val\n---\n# Title\n\nHere is a ---\nhorizontal rule.";
            var result = FrontmatterParser.Parse(markdown);

            Assert.AreEqual("val", result.Frontmatter["key"]);
            Assert.IsTrue(result.Body.Contains("---"));
        }
    }
}
