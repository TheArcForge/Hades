using Hades.Core.Reading;

namespace Hades.Core.Tests.Reading;

/// <summary>
/// The read-through mechanism behind project_get_settings, tag_list, layer_list and
/// scene_list_build: reading one of the fixed ProjectSettings/*.asset files' full field tree, the
/// same single-document ReadNode walk GetComponentProperties uses, but for a file that is never
/// caller-supplied (there is no path argument on any of these four tools — the file is always one
/// of Unity's own fixed ProjectSettings filenames) and carries no fileId to select by, since each
/// of these files is exactly one YAML document.
/// </summary>
public class ProjectSettingsReadingTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

    public ProjectSettingsReadingTests() => Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));

    void Write(string relative, string body)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
    }

    // Mirrors the real Hades-Unity-Client TagManager.asset exactly: one custom layer at index 8,
    // empty slots elsewhere, a 32-entry fixed-length layers array. Real fixed-length-array shape
    // matters more here than realism of the tag/layer names themselves.
    const string TagManagerBody = """
        TagManager:
          serializedVersion: 3
          tags: []
          layers:
          - Default
          - TransparentFX
          - Ignore Raycast
          -
          - Water
          - UI
          -
          -
          - SmokeTestLayer
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
        """;

    [Fact]
    public void GetSettingsAsset_ReadsTagManagerLayersAsA32EntryListWithEmptySlotsPreserved()
    {
        Write("ProjectSettings/TagManager.asset", Header + "--- !u!78 &1\n" + TagManagerBody);

        var document = ReadThrough.GetSettingsAsset(_projectRoot, "ProjectSettings/TagManager.asset");

        var layers = Assert.IsType<List<object?>>(document["layers"]);
        Assert.Equal(32, layers.Count);
        Assert.Equal("Default", layers[0]);
        Assert.Equal("SmokeTestLayer", layers[8]);
        // Every other slot is present (the array is not shortened) and empty, not null/dropped.
        Assert.Equal("", layers[3]);
        Assert.Equal("", layers[31]);
    }

    [Fact]
    public void GetSettingsAsset_ReadsTagManagerTagsAsAList()
    {
        Write("ProjectSettings/TagManager.asset", Header + "--- !u!78 &1\nTagManager:\n  tags:\n  - Player\n  - Enemy\n  layers: []\n");

        var document = ReadThrough.GetSettingsAsset(_projectRoot, "ProjectSettings/TagManager.asset");

        var tags = Assert.IsType<List<object?>>(document["tags"]);
        Assert.Equal(["Player", "Enemy"], tags);
    }

    [Fact]
    public void GetSettingsAsset_AnEmptyFlowSequenceReadsAsAnEmptyListNotAbsent()
    {
        // tags: [] is the common case (few projects define custom tags) - must not be confused
        // with the key being missing entirely.
        Write("ProjectSettings/TagManager.asset", Header + "--- !u!78 &1\nTagManager:\n  tags: []\n  layers: []\n");

        var document = ReadThrough.GetSettingsAsset(_projectRoot, "ProjectSettings/TagManager.asset");

        Assert.Empty(Assert.IsType<List<object?>>(document["tags"]));
    }

    [Fact]
    public void GetSettingsAsset_ReadsEditorBuildSettingsScenesPreservingOrderAndEnabledFlag()
    {
        Write("ProjectSettings/EditorBuildSettings.asset", Header + """
            --- !u!1045 &1
            EditorBuildSettings:
              serializedVersion: 2
              m_Scenes:
              - enabled: 1
                path: Assets/Scenes/Main.unity
                guid: 99c9720ab356a0642a771bea13969a05
              - enabled: 0
                path: Assets/Scenes/Disabled.unity
                guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
              m_configObjects: {}
            """);

        var document = ReadThrough.GetSettingsAsset(_projectRoot, "ProjectSettings/EditorBuildSettings.asset");

        var scenes = Assert.IsType<List<object?>>(document["m_Scenes"]);
        Assert.Equal(2, scenes.Count);

        var first = Assert.IsType<Dictionary<string, object?>>(scenes[0]);
        Assert.Equal("1", first["enabled"]);
        Assert.Equal("Assets/Scenes/Main.unity", first["path"]);

        var second = Assert.IsType<Dictionary<string, object?>>(scenes[1]);
        Assert.Equal("0", second["enabled"]);
        Assert.Equal("Assets/Scenes/Disabled.unity", second["path"]);
    }

    [Fact]
    public void GetSettingsAsset_ReadsProjectSettingsScalarFields()
    {
        Write("ProjectSettings/ProjectSettings.asset", Header + """
            --- !u!129 &1
            PlayerSettings:
              productGUID: 15c012f27331e49229cef25e74537816
              companyName: DefaultCompany
              productName: Hades-Unity-Client
              bundleVersion: 0.1.0
            """);

        var document = ReadThrough.GetSettingsAsset(_projectRoot, "ProjectSettings/ProjectSettings.asset");

        Assert.Equal("DefaultCompany", document["companyName"]);
        Assert.Equal("Hades-Unity-Client", document["productName"]);
        Assert.Equal("0.1.0", document["bundleVersion"]);
    }

    [Fact]
    public void GetSettingsAsset_MissingFileGivesAClearNoLongerOnDiskErrorNotAnUnhandledException()
    {
        var ex = Assert.Throws<FileNotFoundException>(
            () => ReadThrough.GetSettingsAsset(_projectRoot, "ProjectSettings/TagManager.asset"));
        Assert.Contains("TagManager.asset", ex.Message);
    }

    [Fact]
    public void GetSettingsAsset_MalformedContentThrowsInvalidDataDescribingTheFile()
    {
        Write("ProjectSettings/TagManager.asset", Header + "--- !u!78 &1\nTagManager:\n  tags: [\n");

        var ex = Assert.Throws<InvalidDataException>(
            () => ReadThrough.GetSettingsAsset(_projectRoot, "ProjectSettings/TagManager.asset"));
        Assert.Contains("TagManager.asset", ex.Message);
    }

    [Fact]
    public void GetSettingsAsset_ABinaryOrUnrecognisedFileIsReportedAsUnreadable()
    {
        Write("ProjectSettings/TagManager.asset", "\0\x01\x02 not Unity YAML at all");

        Assert.Throws<InvalidDataException>(
            () => ReadThrough.GetSettingsAsset(_projectRoot, "ProjectSettings/TagManager.asset"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }
}
