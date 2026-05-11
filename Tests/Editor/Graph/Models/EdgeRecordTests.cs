using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Models;
using System.Collections.Generic;

namespace ArcForge.Hades.Editor.Tests.Graph.Models
{
    public class EdgeRecordTests
    {
        [Test]
        public void Constructor_SetsFields()
        {
            var edge = new EdgeRecord("contains", sourceGuid: "aaa", sourceFileId: 0, targetGuid: "bbb", targetFileId: 0);

            Assert.AreEqual("contains", edge.Type);
            Assert.AreEqual("aaa", edge.SourceGuid);
            Assert.AreEqual("bbb", edge.TargetGuid);
        }

        [Test]
        public void Properties_SerializesToJson()
        {
            var edge = new EdgeRecord("references", "aaa", 0, "bbb", 0)
            {
                Properties = new Dictionary<string, object>
                {
                    { "field", "playerController" }
                }
            };

            var json = edge.PropertiesJson;
            Assert.IsTrue(json.Contains("playerController"));
        }

        [Test]
        public void Type_IsRequired()
        {
            Assert.Throws<System.ArgumentNullException>(() => new EdgeRecord(null, "a", 0, "b", 0));
        }
    }
}
