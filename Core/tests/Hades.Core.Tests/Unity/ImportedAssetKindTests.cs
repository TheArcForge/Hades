using Hades.Core.Unity;

namespace Hades.Core.Tests.Unity;

/// <summary>
/// The extension-to-graph-node-Kind mapping for binary/imported assets — the single source of
/// truth <see cref="Indexing.BinaryAssetIndexer"/> (which extensions to walk, what Kind to write)
/// and <see cref="Observation.ProjectSweeper"/> (which extensions count as a graph event) both
/// share. Same extension-based-not-content-based reasoning as <see cref="AssetType"/>, and
/// deliberately a DIFFERENT vocabulary from it: <see cref="AssetType"/> answers "what file-level
/// kind is this for asset_find" (a small closed set: Script/Scene/Prefab/Material/
/// AnimatorController/ScriptableObject/Asset); this answers "what graph node Kind should this
/// binary asset get" (Texture2D/Model/AudioClip/Font/Shader/ShaderGraph/ComputeShader/
/// AnimationClip) — the two are never meant to collide, since every extension recognised here
/// falls through <see cref="AssetType"/>'s own switch to its generic "Asset" catch-all today.
/// </summary>
public class ImportedAssetKindTests
{
    [Theory]
    [InlineData("Assets/Textures/Rock.png", "Texture2D")]
    [InlineData("Assets/Textures/Rock.tga", "Texture2D")]
    [InlineData("Assets/Textures/Rock.psd", "Texture2D")]
    [InlineData("Assets/Textures/Rock.jpg", "Texture2D")]
    [InlineData("Assets/Textures/Rock.jpeg", "Texture2D")]
    [InlineData("Assets/Textures/Sky.exr", "Texture2D")]
    [InlineData("Assets/Models/Enemy.fbx", "Model")]
    [InlineData("Assets/Models/Enemy.obj", "Model")]
    [InlineData("Assets/Audio/Hit.wav", "AudioClip")]
    [InlineData("Assets/Audio/Hit.mp3", "AudioClip")]
    [InlineData("Assets/Audio/Hit.ogg", "AudioClip")]
    [InlineData("Assets/Audio/Hit.aiff", "AudioClip")]
    [InlineData("Assets/Fonts/Body.ttf", "Font")]
    [InlineData("Assets/Fonts/Body.otf", "Font")]
    [InlineData("Assets/Shaders/Custom.shader", "Shader")]
    [InlineData("Assets/Shaders/Custom.shadergraph", "ShaderGraph")]
    [InlineData("Assets/Shaders/Blur.compute", "ComputeShader")]
    [InlineData("Assets/Animations/Idle.anim", "AnimationClip")]
    public void RecognisedExtensions_MapToTheirGraphNodeKind(string path, string expected)
    {
        Assert.Equal(expected, ImportedAssetKind.KindForPath(path));
    }

    [Theory]
    [InlineData("Assets/Scripts/Health.cs")]
    [InlineData("Assets/Prefabs/Enemy.prefab")]
    [InlineData("Assets/Notes.txt")]
    [InlineData("Assets/NoExtensionAtAll")]
    public void UnrecognisedExtensions_ReturnNullRatherThanGuessing(string path)
    {
        Assert.Null(ImportedAssetKind.KindForPath(path));
    }

    [Fact]
    public void ExtensionMatchingIsCaseInsensitive()
    {
        Assert.Equal("Texture2D", ImportedAssetKind.KindForPath("Assets/Rock.PNG"));
    }

    [Fact]
    public void ExtensionsExposesEveryRecognisedExtensionExactlyOnce()
    {
        string[] expected =
        [
            ".png", ".tga", ".psd", ".jpg", ".jpeg", ".exr",
            ".fbx", ".obj",
            ".wav", ".mp3", ".ogg", ".aiff",
            ".ttf", ".otf",
            ".shader", ".shadergraph", ".compute",
            ".anim",
        ];

        Assert.Equal(expected.Length, ImportedAssetKind.Extensions.Count);
        foreach (var ext in expected)
            Assert.Contains(ext, ImportedAssetKind.Extensions);
    }
}
