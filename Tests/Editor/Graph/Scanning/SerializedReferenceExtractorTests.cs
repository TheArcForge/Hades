using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Scanning;

namespace ArcForge.Hades.Editor.Tests.Graph.Scanning
{
    public class SerializedReferenceExtractorTests
    {
        [Test]
        public void IsValidAssetGuidHex_ValidGuid_ReturnsTrue()
        {
            Assert.IsTrue(SerializedReferenceExtractor.IsValidAssetGuidHex("0123456789abcdef0123456789abcdef"));
        }

        [Test]
        public void IsValidAssetGuidHex_WrongLength_ReturnsFalse()
        {
            Assert.IsFalse(SerializedReferenceExtractor.IsValidAssetGuidHex("abc123"));
        }

        [Test]
        public void IsValidAssetGuidHex_NonHexChar_ReturnsFalse()
        {
            Assert.IsFalse(SerializedReferenceExtractor.IsValidAssetGuidHex("g123456789abcdef0123456789abcdef"));
        }

        [Test]
        public void IsValidAssetGuidHex_NullOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(SerializedReferenceExtractor.IsValidAssetGuidHex(null));
            Assert.IsFalse(SerializedReferenceExtractor.IsValidAssetGuidHex(""));
        }

        [Test]
        public void IsAddressableGuidField_AllowlistedNames_ReturnTrue()
        {
            Assert.IsTrue(SerializedReferenceExtractor.IsAddressableGuidField("m_AssetGUID"));
            Assert.IsTrue(SerializedReferenceExtractor.IsAddressableGuidField("m_SubObjectGUID"));
        }

        [Test]
        public void IsAddressableGuidField_OtherName_ReturnsFalse()
        {
            Assert.IsFalse(SerializedReferenceExtractor.IsAddressableGuidField("m_SomeString"));
        }
    }
}
