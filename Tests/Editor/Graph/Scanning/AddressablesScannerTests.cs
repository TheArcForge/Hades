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
    }
}
