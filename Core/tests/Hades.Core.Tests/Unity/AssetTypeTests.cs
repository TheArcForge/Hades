using Hades.Core.Unity;

namespace Hades.Core.Tests.Unity;

/// <summary>
/// The friendly, file-level asset classification behind asset_get_info's "type" and asset_find's
/// type filter — deliberately extension-based rather than content-based. A material or controller
/// asset is always exactly one YAML document, so its own declared type would work just as well,
/// but a prefab or scene's FIRST document is not reliably "the file's type" (document order is not
/// hierarchy order — see ReadThrough.BuildHierarchy), so extension is the only signal correct for
/// every asset kind, not just some of them. Mirrors AssetIndexer's own Extensions list plus ".cs",
/// which ScriptIndexer covers separately — exactly the file kinds Hades already treats specially.
/// </summary>
public class AssetTypeTests
{
    [Theory]
    [InlineData("Assets/Scripts/Health.cs", "Script")]
    [InlineData("Assets/Scenes/Main.unity", "Scene")]
    [InlineData("Assets/Prefabs/Enemy.prefab", "Prefab")]
    [InlineData("Assets/Materials/M_Enemy.mat", "Material")]
    [InlineData("Assets/Animations/SmokeTest.controller", "AnimatorController")]
    [InlineData("Assets/Settings/PC_RPAsset.asset", "ScriptableObject")]
    public void RecognisedExtensions_MapToTheirFriendlyKind(string path, string expected)
    {
        Assert.Equal(expected, AssetType.FromPath(path));
    }

    [Theory]
    [InlineData("Assets/Textures/Rock.png")]
    [InlineData("Assets/Audio/Hit.wav")]
    [InlineData("Assets/Models/Enemy.fbx")]
    [InlineData("Assets/Notes.txt")]
    [InlineData("Assets/NoExtensionAtAll")]
    public void UnrecognisedExtensions_FallBackToAGenericAssetKindRatherThanGuessing(string path)
    {
        Assert.Equal("Asset", AssetType.FromPath(path));
    }

    [Fact]
    public void ExtensionMatchingIsCaseInsensitive()
    {
        // Unity itself is case-preserving but not case-sensitive about extensions on most
        // filesystems; a caller-supplied path should not silently miss a known kind over casing.
        Assert.Equal("Prefab", AssetType.FromPath("Assets/Enemy.PREFAB"));
    }
}
