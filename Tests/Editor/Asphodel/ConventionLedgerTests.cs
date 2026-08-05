using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel.Conventions;

namespace ArcForge.Hades.Editor.Tests.Asphodel
{
    public class ConventionLedgerTests
    {
        string _dir;
        [SetUp] public void S() { _dir = Path.Combine(Path.GetTempPath(), $"hades_led_{System.Guid.NewGuid()}"); Directory.CreateDirectory(_dir); }
        [TearDown] public void T() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

        [Test]
        public void RoundTrips()
        {
            var l = ConventionLedger.Load(_dir);
            l.Set("naming", "dismissed", 0.7);
            l.Save(_dir);

            var l2 = ConventionLedger.Load(_dir);
            Assert.AreEqual("dismissed", l2.Status("naming"));
            Assert.AreEqual(0.7, l2.Confidence("naming"), 0.001);
        }

        [Test]
        public void UnknownKey_DefaultsToNone()
        {
            Assert.AreEqual("none", ConventionLedger.Load(_dir).Status("nope"));
        }

        [Test]
        public void CorruptFile_LoadsEmpty()
        {
            File.WriteAllText(Path.Combine(_dir, ".conventions-state.json"), "{ not json");
            Assert.AreEqual("none", ConventionLedger.Load(_dir).Status("naming"));
        }
    }
}
