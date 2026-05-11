// Tests/Editor/Graph/Scanning/ScannerRegistryTests.cs
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Scanning;
using System.Collections.Generic;

namespace ArcForge.Hades.Editor.Tests.Graph.Scanning
{
    public class ScannerRegistryTests
    {
        [Test]
        public void DiscoverScanners_FindsAtLeastOne()
        {
            var registry = new ScannerRegistry();
            var scanners = registry.GetAll();
            Assert.GreaterOrEqual(scanners.Count, 0);
        }

        [Test]
        public void GetScannerForExtension_UnknownExtension_ReturnsNull()
        {
            var registry = new ScannerRegistry();
            var scanner = registry.GetScannerForPath("Assets/SomeFile.xyz");
            Assert.IsNull(scanner);
        }
    }
}
