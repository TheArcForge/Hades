using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Scanning;

namespace ArcForge.Hades.Editor.Tests.Graph.Scanning
{
    public class AddressablesScannerTests
    {
        [Test]
        public void ScannerName_IsCorrect()
        {
            var scanner = new AddressablesScanner();
            Assert.AreEqual("AddressablesScanner", scanner.ScannerName);
        }

        [Test]
        public void Scan_WithoutAddressables_ReturnsEmptyResult()
        {
            var scanner = new AddressablesScanner();
            var result = scanner.Scan("AddressableAssetsData/AddressableAssetSettings.asset");
            Assert.IsNotNull(result);
        }

        [Test]
        public void Scan_WithoutAddressables_ProducesNoEntryNodes()
        {
            var scanner = new AddressablesScanner();
            var result = scanner.Scan("AddressableAssetsData/AddressableAssetSettings.asset");
            // No package installed in the sandbox: the scanner must short-circuit cleanly
            // with no AddressableEntry nodes and no edges.
            Assert.IsEmpty(result.Nodes.FindAll(n => n.Type == "AddressableEntry"));
            Assert.IsEmpty(result.Edges.FindAll(e => e.Type == "addressable_for"));
        }
    }
}
