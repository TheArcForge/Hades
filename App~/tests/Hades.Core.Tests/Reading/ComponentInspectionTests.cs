using Hades.Core.Reading;

namespace Hades.Core.Tests.Reading;

/// <summary>
/// The read-through mechanism behind component_get_all, component_get_property and
/// component_list_properties: listing one GameObject's components, and reading one component's
/// serialized fields, both by re-parsing the named file rather than querying the graph. See
/// <see cref="ReadThrough"/>'s class doc comment for why read-through re-parses rather than
/// indexing; PathForGuid resolution of a MonoBehaviour's script (component_get_all's one graph
/// touch) is exercised at the InspectionTools HTTP level, not here - this file is scoped to what
/// ReadThrough itself does with no graph involved.
/// </summary>
public class ComponentInspectionTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

    public ComponentInspectionTests() => Directory.CreateDirectory(Path.Combine(_projectRoot, "Assets"));

    void Write(string relative, string body)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
    }

    // A GameObject ("Enemy") with a Transform, a MeshRenderer, and two MonoBehaviours - one with
    // custom scalar and reference-valued fields (mirrors Health on the real Hades-Unity-Client
    // Enemy.prefab), one whose script guid resolves to nothing (a deleted script). Component
    // fileIDs deliberately small and sequential (2-5) since only their identity matters here, not
    // their realism - realism against the actual corpus is what the real-project smoke test covers.
    const string EnemyWithComponents =
        "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  - component: {fileID: 3}\n"
      + "  - component: {fileID: 4}\n  - component: {fileID: 5}\n  m_Name: Enemy\n"
      + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n"
      + "--- !u!23 &3\nMeshRenderer:\n  m_GameObject: {fileID: 1}\n"
      + "--- !u!114 &4\nMonoBehaviour:\n  m_GameObject: {fileID: 1}\n"
      + "  m_Script: {fileID: 11500000, guid: aaaa1111aaaa1111aaaa1111aaaa1111, type: 3}\n"
      + "  maxHealth: 100\n  damageConfig: {fileID: 11400000, guid: bbbb2222bbbb2222bbbb2222bbbb2222, type: 2}\n"
      + "--- !u!114 &5\nMonoBehaviour:\n  m_GameObject: {fileID: 1}\n"
      + "  m_Script: {fileID: 11500000, guid: cccc3333cccc3333cccc3333cccc3333, type: 3}\n";

    // ---------------------------------------------------------------- GetComponents

    [Fact]
    public void GetComponents_ListsEveryComponentIncludingTransformInDocumentOrder()
    {
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        var components = ReadThrough.GetComponents(_projectRoot, "Assets/Enemy.prefab", gameObjectFileId: 1);

        Assert.Equal([2L, 3L, 4L, 5L], components.Select(c => c.FileId));
        Assert.Equal(["Transform", "MeshRenderer", "MonoBehaviour", "MonoBehaviour"], components.Select(c => c.Kind));
    }

    [Fact]
    public void GetComponents_AMonoBehaviourCapturesItsRawScriptGuid()
    {
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        var components = ReadThrough.GetComponents(_projectRoot, "Assets/Enemy.prefab", gameObjectFileId: 1);

        var health = components.Single(c => c.FileId == 4);
        Assert.Equal("aaaa1111aaaa1111aaaa1111aaaa1111", health.ScriptGuid);
    }

    [Fact]
    public void GetComponents_ANonMonoBehaviourComponentHasNoScriptGuid()
    {
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        var components = ReadThrough.GetComponents(_projectRoot, "Assets/Enemy.prefab", gameObjectFileId: 1);

        var renderer = components.Single(c => c.FileId == 3);
        Assert.Null(renderer.ScriptGuid);
    }

    [Fact]
    public void GetComponents_AcceptsTheTransformsFileIdTheSameWayPrefabGetContentsReportsIt()
    {
        // BuildHierarchy (behind prefab_get_contents / scene_get_hierarchy) keys a "GameObject"
        // node by its TRANSFORM's fileId, not the GameObject document's own - confirmed against
        // the real Hades-Unity-Client corpus, where every "GameObject"-kind node fileId IS its
        // Transform's. component_get_all exists specifically to be fed that reported fileId, so it
        // must accept it, not just the GameObject's own fileId (fileId 1 in this fixture; its
        // Transform is fileId 2 - see EnemyWithComponents).
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        var byGameObjectFileId = ReadThrough.GetComponents(_projectRoot, "Assets/Enemy.prefab", gameObjectFileId: 1);
        var byTransformFileId = ReadThrough.GetComponents(_projectRoot, "Assets/Enemy.prefab", gameObjectFileId: 2);

        Assert.Equal(byGameObjectFileId.Select(c => c.FileId), byTransformFileId.Select(c => c.FileId));
    }

    [Fact]
    public void GetComponents_AnUnknownGameObjectFileIdIsReportedClearlyNotAsAnEmptyList()
    {
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        var ex = Assert.Throws<ArgumentException>(
            () => ReadThrough.GetComponents(_projectRoot, "Assets/Enemy.prefab", gameObjectFileId: 999));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void GetComponents_AFileIdThatIsAComponentNotAGameObjectIsRejectedWithAClearReason()
    {
        // Passing a component's own fileId (or a nested prefab instance placeholder's) instead of
        // its owning GameObject's is the natural mistake this guards - the error must say why.
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        var ex = Assert.Throws<ArgumentException>(
            () => ReadThrough.GetComponents(_projectRoot, "Assets/Enemy.prefab", gameObjectFileId: 3));
        Assert.Contains("MeshRenderer", ex.Message);
        Assert.Contains("not a GameObject", ex.Message);
    }

    [Fact]
    public void GetComponents_PathEscapingTheProjectIsRefused()
    {
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        Assert.Throws<ArgumentException>(
            () => ReadThrough.GetComponents(_projectRoot, "Assets/../../../../etc/passwd", gameObjectFileId: 1));
    }

    // ---------------------------------------------------------------- GetComponentProperties

    [Fact]
    public void GetComponentProperties_ReturnsScalarFieldsAsRawStrings()
    {
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        var properties = ReadThrough.GetComponentProperties(_projectRoot, "Assets/Enemy.prefab", fileId: 4);

        Assert.Equal("100", properties["maxHealth"]);
    }

    [Fact]
    public void GetComponentProperties_ReturnsAReferenceValuedFieldAsANestedMap()
    {
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        var properties = ReadThrough.GetComponentProperties(_projectRoot, "Assets/Enemy.prefab", fileId: 4);

        var reference = Assert.IsType<Dictionary<string, object?>>(properties["damageConfig"]);
        Assert.Equal("bbbb2222bbbb2222bbbb2222bbbb2222", reference["guid"]);
    }

    [Fact]
    public void GetComponentProperties_ASequenceValuedFieldReturnsAsAList()
    {
        const string withArray =
            "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: Root\n"
          + "--- !u!114 &2\nMonoBehaviour:\n  m_GameObject: {fileID: 1}\n  m_Items:\n  - one\n  - two\n";
        Write("Assets/Array.prefab", Header + withArray);

        var properties = ReadThrough.GetComponentProperties(_projectRoot, "Assets/Array.prefab", fileId: 2);

        var items = Assert.IsType<List<object?>>(properties["m_Items"]);
        Assert.Equal(["one", "two"], items);
    }

    [Fact]
    public void GetComponentProperties_EveryNameItReturns_IsInTurnASuccessfulLookup()
    {
        // component_list_properties and component_get_property are built directly on this one
        // dictionary (see ComponentListProperties / ComponentGetProperty in InspectionTools), so
        // this is what actually guarantees their vocabularies match exactly.
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        var properties = ReadThrough.GetComponentProperties(_projectRoot, "Assets/Enemy.prefab", fileId: 4);

        Assert.NotEmpty(properties);
        foreach (var name in properties.Keys) Assert.True(properties.ContainsKey(name));
    }

    [Fact]
    public void GetComponentProperties_AnUnknownFileIdIsReportedClearlyNotAsAnEmptyMap()
    {
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        var ex = Assert.Throws<ArgumentException>(
            () => ReadThrough.GetComponentProperties(_projectRoot, "Assets/Enemy.prefab", fileId: 999));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void GetComponentProperties_PathEscapingTheProjectIsRefused()
    {
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        Assert.Throws<ArgumentException>(
            () => ReadThrough.GetComponentProperties(_projectRoot, "Assets/../../../../etc/passwd", fileId: 4));
    }

    [Fact]
    public void GetComponentProperties_AFileDeletedSinceIndexing_GivesAClearNoLongerOnDiskError()
    {
        Write("Assets/Gone.prefab", Header + EnemyWithComponents);
        File.Delete(Path.Combine(_projectRoot, "Assets", "Gone.prefab"));

        var ex = Assert.Throws<FileNotFoundException>(
            () => ReadThrough.GetComponentProperties(_projectRoot, "Assets/Gone.prefab", fileId: 4));
        Assert.Contains("no longer on disk", ex.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }
}
