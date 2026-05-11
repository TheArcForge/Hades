using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Models;
using System.Collections.Generic;

namespace ArcForge.Hades.Editor.Tests.Graph.Models
{
    public class NodeRecordTests
    {
        [Test]
        public void Constructor_SetsRequiredFields()
        {
            var node = new NodeRecord("Scene", "abc123")
            {
                Name = "MainMenu",
                Path = "Assets/Scenes/MainMenu.unity"
            };

            Assert.AreEqual("Scene", node.Type);
            Assert.AreEqual("abc123", node.Guid);
            Assert.AreEqual("MainMenu", node.Name);
            Assert.AreEqual("Assets/Scenes/MainMenu.unity", node.Path);
        }

        [Test]
        public void Properties_SerializesToJson()
        {
            var node = new NodeRecord("Component", "abc123")
            {
                FileId = 100100000,
                Properties = new Dictionary<string, object>
                {
                    { "is_enabled", true },
                    { "execution_order", 100 }
                }
            };

            var json = node.PropertiesJson;
            Assert.IsTrue(json.Contains("is_enabled"));
            Assert.IsTrue(json.Contains("100"));
        }

        [Test]
        public void PropertiesJson_NullProperties_ReturnsNull()
        {
            var node = new NodeRecord("Scene", "abc123");
            Assert.IsNull(node.PropertiesJson);
        }

        [Test]
        public void Type_IsRequired()
        {
            Assert.Throws<System.ArgumentNullException>(() => new NodeRecord(null, "abc"));
            Assert.Throws<System.ArgumentException>(() => new NodeRecord("", "abc"));
        }
    }
}
