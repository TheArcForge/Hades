using Hades.Core.Reading;

namespace Hades.Core.Tests.Reading;

/// <summary>
/// The read-through mechanism: parses one named scene or prefab file on demand, with no graph
/// involved. Four later plan tasks (component inspection, settings, typed asset readers,
/// reference/event queries) all sit on this, so its failure modes are covered deliberately rather
/// than just its happy path.
/// </summary>
public class ReadThroughTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

    public ReadThroughTests() => Directory.CreateDirectory(Path.Combine(_projectRoot, "Assets"));

    void Write(string relative, string body)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
    }

    // A root GameObject ("Root") with a MeshRenderer, and one child ("Child") - enough to prove
    // parent/child nesting, name resolution, and per-GameObject component listing all at once.
    const string RootAndChild =
        "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  - component: {fileID: 5}\n  m_Name: Root\n"
      + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n"
      + "--- !u!1 &3\nGameObject:\n  m_Component:\n  - component: {fileID: 4}\n  m_Name: Child\n"
      + "--- !u!4 &4\nTransform:\n  m_GameObject: {fileID: 3}\n  m_Father: {fileID: 2}\n"
      + "--- !u!23 &5\nMeshRenderer:\n  m_GameObject: {fileID: 1}\n";

    // ---------------------------------------------------------------- hierarchy shape

    [Fact]
    public void ParsingAPrefab_YieldsGameObjectHierarchyWithParentChildIntact()
    {
        Write("Assets/Hierarchy.prefab", Header + RootAndChild);

        var hierarchy = ReadThrough.GetHierarchy(_projectRoot, "Assets/Hierarchy.prefab");

        Assert.Equal("Assets/Hierarchy.prefab", hierarchy.Path);
        var root = Assert.Single(hierarchy.Roots);
        Assert.Equal("Root", root.Name);
        Assert.Equal("GameObject", root.Kind);
        Assert.Equal(["MeshRenderer"], root.Components);

        var child = Assert.Single(root.Children);
        Assert.Equal("Child", child.Name);
        Assert.Empty(child.Children);
    }

    [Fact]
    public void ParsingAScene_YieldsTheSameShapeAsAPrefab()
    {
        // Same renderer, same shape - scenes and prefabs are the same YAML format.
        Write("Assets/Hierarchy.unity", Header + RootAndChild);

        var hierarchy = ReadThrough.GetHierarchy(_projectRoot, "Assets/Hierarchy.unity");

        var root = Assert.Single(hierarchy.Roots);
        Assert.Equal("Root", root.Name);
        Assert.Equal("Child", Assert.Single(root.Children).Name);
    }

    [Fact]
    public void AChildsTransformListedBeforeItsParentInFileOrder_StillLinksCorrectly()
    {
        // Unity does not promise document order follows the hierarchy. The child's Transform (&4)
        // is written before the parent's (&2) here - a single-pass "link as we discover" build
        // would misplace Child as a root because Root's Transform has not been seen yet.
        const string reversedOrder =
            "--- !u!1 &3\nGameObject:\n  m_Component:\n  - component: {fileID: 4}\n  m_Name: Child\n"
          + "--- !u!4 &4\nTransform:\n  m_GameObject: {fileID: 3}\n  m_Father: {fileID: 2}\n"
          + "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: Root\n"
          + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n";
        Write("Assets/Reversed.prefab", Header + reversedOrder);

        var hierarchy = ReadThrough.GetHierarchy(_projectRoot, "Assets/Reversed.prefab");

        var root = Assert.Single(hierarchy.Roots);
        Assert.Equal("Root", root.Name);
        Assert.Equal("Child", Assert.Single(root.Children).Name);
    }

    [Fact]
    public void MultipleRootGameObjects_AllAppearAtTheTopLevel()
    {
        const string twoRoots =
            "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: First\n"
          + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n"
          + "--- !u!1 &3\nGameObject:\n  m_Component:\n  - component: {fileID: 4}\n  m_Name: Second\n"
          + "--- !u!4 &4\nTransform:\n  m_GameObject: {fileID: 3}\n  m_Father: {fileID: 0}\n";
        Write("Assets/TwoRoots.prefab", Header + twoRoots);

        var hierarchy = ReadThrough.GetHierarchy(_projectRoot, "Assets/TwoRoots.prefab");

        Assert.Equal(["First", "Second"], hierarchy.Roots.Select(r => r.Name));
    }

    [Fact]
    public void RectTransformUiHierarchies_LinkParentChildTheSameWayAsOrdinaryTransforms()
    {
        // UI content uses RectTransform (class 224), not Transform (class 4) - get_scene_summary's
        // own root-detection treats the two identically ("'Root' means... Transform (or
        // RectTransform, for UI)"), and BuildHierarchy's node/component filters branch on both
        // kinds the same way. Worth its own fixture: the real Hades-Unity-Client corpus has no UI
        // content at all, so nothing there exercises this path.
        const string canvasWithButton =
            "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: Canvas\n"
          + "--- !u!224 &2\nRectTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n"
          + "--- !u!1 &3\nGameObject:\n  m_Component:\n  - component: {fileID: 4}\n  m_Name: Button\n"
          + "--- !u!224 &4\nRectTransform:\n  m_GameObject: {fileID: 3}\n  m_Father: {fileID: 2}\n";
        Write("Assets/Canvas.prefab", Header + canvasWithButton);

        var hierarchy = ReadThrough.GetHierarchy(_projectRoot, "Assets/Canvas.prefab");

        var root = Assert.Single(hierarchy.Roots);
        Assert.Equal("Canvas", root.Name);
        Assert.Equal("GameObject", root.Kind);
        Assert.Equal("Button", Assert.Single(root.Children).Name);
    }

    [Fact]
    public async Task ACircularParentChainInAHandCorruptedFile_TerminatesRatherThanOverflowing()
    {
        // Unity itself never writes a cycle, but a hand-edited or corrupted file could (this is
        // exactly the kind of file the malformed-YAML handling above exists for too). Building
        // the tree top-down from roots means the whole cycle - disconnected from every root -
        // would never be visited at all without the safety net that surfaces it afterward instead
        // of silently dropping it. Bounded with a timeout so a regression fails fast rather than
        // hanging the suite, the same defensive shape as ScriptIndexerTests' symlink-cycle test.
        const string cyclic =
            "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: A\n"
          + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 4}\n"
          + "--- !u!1 &3\nGameObject:\n  m_Component:\n  - component: {fileID: 4}\n  m_Name: B\n"
          + "--- !u!4 &4\nTransform:\n  m_GameObject: {fileID: 3}\n  m_Father: {fileID: 2}\n";
        Write("Assets/Cyclic.prefab", Header + cyclic);

        var task = Task.Run(() => ReadThrough.GetHierarchy(_projectRoot, "Assets/Cyclic.prefab"));
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(completed, task), "GetHierarchy did not terminate - a parent cycle is unguarded");

        var names = Flatten((await task).Roots).Select(n => n.Name).ToList();
        Assert.Contains("A", names);
        Assert.Contains("B", names);

        static IEnumerable<HierarchyNode> Flatten(IEnumerable<HierarchyNode> nodes) =>
            nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
    }

    // ---------------------------------------------------------------- prefab variants / nested instances

    [Fact]
    public void APureVariantRootWithNoLocalTransforms_YieldsAPlaceholderRootNodeNotAnEmptyHierarchy()
    {
        // Mirrors the real Hades-Unity-Client corpus exactly: SmokeTestCube_Variant.prefab and
        // Enemy_Fast/Tank/Boss.prefab are each a SINGLE PrefabInstance document with
        // m_TransformParent: {fileID: 0} and zero local Transform/GameObject documents - the base
        // prefab supplies the whole tree. Read-through must not chase into the base prefab (that
        // is cross-file, graph-served work), but this is a non-empty file and must not silently
        // report an empty hierarchy either.
        const string body = """
            --- !u!1001 &259710713600510621
            PrefabInstance:
              serializedVersion: 2
              m_Modification:
                m_TransformParent: {fileID: 0}
                m_Modifications: []
                m_RemovedComponents: []
              m_SourcePrefab: {fileID: 100100000, guid: beb43c66c1c72416290db5dae24d452f, type: 3}
            """;
        Write("Assets/Variant.prefab", Header + body);

        var hierarchy = ReadThrough.GetHierarchy(_projectRoot, "Assets/Variant.prefab");

        var root = Assert.Single(hierarchy.Roots);
        Assert.Equal("PrefabInstance", root.Kind);
        Assert.Equal("beb43c66c1c72416290db5dae24d452f", root.SourcePrefabGuid);
        Assert.Null(root.Name);
        Assert.Empty(root.Components);
    }

    [Fact]
    public void ANestedInstanceWithAStrippedPlaceholder_IsPositionedUnderItsAttachPointNotAsARoot()
    {
        // Mirrors Assets/Prefabs/NestTest/Wrapper.prefab: a host GameObject "Wrapper" with a
        // nested PrefabInstance attached under it, represented locally only by a stripped
        // Transform. Neither the stripped Transform nor the PrefabInstance carries a usable
        // m_Father - the PrefabInstance's own m_TransformParent is the only local signal for
        // where this instance attaches, and m_PrefabInstance is what links the stripped
        // placeholder back to it.
        const string body = """
            --- !u!1 &927721006231195886
            GameObject:
              m_Component:
              - component: {fileID: 4854749283863383569}
              m_Name: Wrapper
            --- !u!4 &4854749283863383569
            Transform:
              m_GameObject: {fileID: 927721006231195886}
              m_Children:
              - {fileID: 1823683217792359763}
              m_Father: {fileID: 0}
            --- !u!1001 &867084610188306506
            PrefabInstance:
              serializedVersion: 2
              m_Modification:
                m_TransformParent: {fileID: 4854749283863383569}
                m_Modifications: []
                m_RemovedComponents: []
              m_SourcePrefab: {fileID: 100100000, guid: f148ae4dd5d604a8684245a740d8569d, type: 3}
            --- !u!4 &1823683217792359763 stripped
            Transform:
              m_CorrespondingSourceObject: {fileID: 1533342505765025049, guid: f148ae4dd5d604a8684245a740d8569d, type: 3}
              m_PrefabInstance: {fileID: 867084610188306506}
              m_PrefabAsset: {fileID: 0}
            """;
        Write("Assets/Wrapper.prefab", Header + body);

        var hierarchy = ReadThrough.GetHierarchy(_projectRoot, "Assets/Wrapper.prefab");

        var root = Assert.Single(hierarchy.Roots);
        Assert.Equal("Wrapper", root.Name);
        var nested = Assert.Single(root.Children);
        Assert.Equal("PrefabInstance", nested.Kind);
        Assert.Equal("f148ae4dd5d604a8684245a740d8569d", nested.SourcePrefabGuid);
        Assert.Null(nested.Name);
        Assert.Empty(nested.Children);
    }

    // ---------------------------------------------------------------- containment guard

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("/etc/passwd")]
    [InlineData("Assets/../../../../etc/passwd")]
    [InlineData("../../etc/passwd")]
    [InlineData("Nonexistent/Foo.prefab")]
    [InlineData("Assets")]
    public void GetHierarchy_RejectsAnyPathThatCouldEscapeTheProjectsScanRoots(string? path)
    {
        Write("Assets/Hierarchy.prefab", Header + RootAndChild);

        Assert.Throws<ArgumentException>(() => ReadThrough.GetHierarchy(_projectRoot, path!));
    }

    [Fact]
    public void AnAbsolutePathEvenWhenLexicallyInsideTheProject_IsStillRefused()
    {
        // Path.Combine silently IGNORES its first argument when the second is rooted, so
        // resolving a caller-supplied absolute path against a scan root the ordinary way would
        // hand back the caller's path completely unchecked. The contract is project-relative;
        // an absolute path is refused outright rather than quietly "just working".
        Write("Assets/Hierarchy.prefab", Header + RootAndChild);
        var absolute = Path.Combine(_projectRoot, "Assets", "Hierarchy.prefab");

        Assert.Throws<ArgumentException>(() => ReadThrough.GetHierarchy(_projectRoot, absolute));
    }

    [Fact]
    public void ASymlinkInsideAScanRootEscapingToOutsideTheProject_IsRefused()
    {
        // Path.GetFullPath is purely lexical and never touches the filesystem, so a symlink
        // planted inside a scan root (Assets/Escape -> somewhere else) would pass a plain
        // string-prefix containment check while physically reading from outside the project.
        var outside = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Secret.prefab"), Header + RootAndChild);
        Directory.CreateSymbolicLink(Path.Combine(_projectRoot, "Assets", "Escape"), outside);

        try
        {
            Assert.Throws<ArgumentException>(
                () => ReadThrough.GetHierarchy(_projectRoot, "Assets/Escape/Secret.prefab"));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    // ---------------------------------------------------------------- disk state / parse errors

    [Fact]
    public void AFileDeletedSinceIndexing_GivesAClearNoLongerOnDiskErrorNotAnUnhandledException()
    {
        Write("Assets/Gone.prefab", Header + RootAndChild);
        File.Delete(Path.Combine(_projectRoot, "Assets", "Gone.prefab"));

        var ex = Assert.Throws<FileNotFoundException>(
            () => ReadThrough.GetHierarchy(_projectRoot, "Assets/Gone.prefab"));
        Assert.Contains("no longer on disk", ex.Message);
        Assert.Contains("Gone.prefab", ex.Message);
    }

    [Fact]
    public void MalformedOrTruncatedYaml_ReturnsAParseErrorNamingTheFileNeverAPartialHierarchy()
    {
        // Cut off mid-flow-mapping, as a file caught mid-write would be.
        Write("Assets/Truncated.prefab",
            Header + "--- !u!1 &1\nGameObject:\n  m_Name: Test\n--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1");

        var ex = Assert.Throws<InvalidDataException>(
            () => ReadThrough.GetHierarchy(_projectRoot, "Assets/Truncated.prefab"));
        Assert.Contains("Truncated.prefab", ex.Message);
    }

    [Fact]
    public void ABinaryOrUnrecognisedAsset_IsReportedAsUnreadableNotAsAnEmptyHierarchy()
    {
        Write("Assets/Binary.prefab", "\0\x01\x02 not Unity YAML at all");

        var ex = Assert.Throws<InvalidDataException>(
            () => ReadThrough.GetHierarchy(_projectRoot, "Assets/Binary.prefab"));
        Assert.Contains("Binary.prefab", ex.Message);
    }

    [Fact]
    public void LegacyPreUnity2018PrefabFormat_IsReportedAsUnsupportedByNameNotSilentlyEmpty()
    {
        // The "Prefab:" format PrefabInstanceReader already recognises and skips (pre-2018.3,
        // marks a prefab ASSET rather than an instance). Real, if rare: 15 in the measured corpus.
        Write("Assets/Legacy.prefab", Header + """
            --- !u!1001 &100100000
            Prefab:
              m_ObjectHideFlags: 1
              serializedVersion: 2
              m_Modification:
                m_TransformParent: {fileID: 0}
                m_Modifications: []
                m_RemovedComponents: []
              m_ParentPrefab: {fileID: 0}
              m_RootGameObject: {fileID: 100000}
              m_IsPrefabParent: 1
            """);

        var ex = Assert.Throws<NotSupportedException>(
            () => ReadThrough.GetHierarchy(_projectRoot, "Assets/Legacy.prefab"));
        Assert.Contains("Legacy.prefab", ex.Message);
    }

    // ---------------------------------------------------------------- GetAssetInfo

    [Fact]
    public void GetAssetInfo_AMissingFileNamesToolsThatActuallyExistOnTheCurrentSurface()
    {
        var ex = Assert.Throws<FileNotFoundException>(
            () => ReadThrough.GetAssetInfo(_projectRoot, "Assets/Missing.png"));

        Assert.Contains("Missing.png", ex.Message);

        // Regression test for the dead-tool-name cleanup: this used to point at the now-deleted
        // asset_find. search_by_name alone is not an honest replacement here - it now only
        // searches C# types - so the general-asset case has to name graph_query's fileType filter
        // instead (asset_find's real replacement).
        Assert.Contains("search_by_name (for a script) or graph_query's 'fileType' filter (for any asset)", ex.Message);
        Assert.DoesNotContain("asset_find", ex.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }
}
