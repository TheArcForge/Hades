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

        // The tool this points to must actually exist on the current 32-tool MCP surface - a
        // regression test for the dead-tool-name cleanup (prefab_get_contents/scene_get_hierarchy
        // no longer exist).
        Assert.Contains("call inspect_asset with just 'path' again to get current fileIds.", ex.Message);
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

        // Regression test for the dead-tool-name cleanup: this used to point at
        // component_get_all/prefab_get_contents/scene_get_hierarchy, none of which exist anymore.
        Assert.Contains(
            "inspect_asset's 'target' needs a GameObject's fileId, exactly as the whole-file structure result reports it.",
            ex.Message);
    }

    [Fact]
    public void GetComponents_PathEscapingTheProjectIsRefused()
    {
        Write("Assets/Enemy.prefab", Header + EnemyWithComponents);

        Assert.Throws<ArgumentException>(
            () => ReadThrough.GetComponents(_projectRoot, "Assets/../../../../etc/passwd", gameObjectFileId: 1));
    }

    // ---------------------------------------------------------------- GetComponents: nested/variant instance overrides
    //
    // Plan 15 Task 2. Ground truth for this fixture's shape is a REAL file, not a guess:
    // project_aurora's Assets/_ResourcesStatic/Buildings/BedroomWithChestAndTable.prefab, read
    // directly (raw YAML) and cross-checked against the real Hades MCP server's live output before
    // any fix landed. Two independent local override anchors exist in that one file, and -
    // critically, discovered only by reading the source prefab it instantiates
    // (Assets/_ResourcesStatic/BasePrefab.prefab) - they are placeholders for TWO DIFFERENT,
    // unrelated source objects, not two views of the same one:
    //   - a stripped TRANSFORM (source fileID 2256036153830176909, the source prefab's "Graphics"
    //     CHILD's own Transform) - present only because a locally-added sibling GameObject needed
    //     an m_Father anchor. It owns no components at all.
    //   - a stripped GAMEOBJECT (source fileID 869704862101631421, the source prefab's own ROOT
    //     GameObject) - present because 3 components were added directly to the root. It carries
    //     NO m_Component list of its own (confirmed on the real file), so its real components are
    //     only discoverable by scanning for objects whose OWN m_GameObject reference names it.
    // This fixture mirrors that exact shape with small, sequential fileIds instead of the real
    // 19-digit ones, per this file's own established convention (see EnemyWithComponents above).

    const string SourceGuid = "cccccccccccccccccccccccccccccccc";

    // fileId 100: PrefabInstance, root-level in this file (m_TransformParent: {fileID: 0}).
    // fileId 101: stripped Transform - mirrors "Graphics": owns nothing, anchors a child GameObject.
    // fileId 102: stripped GameObject - mirrors the source root: owns the 3 added MonoBehaviours.
    // fileId 103-105: the 3 added MonoBehaviours, each pointing m_GameObject at 102.
    // fileId 106/107: a locally-added child GameObject/Transform, parented under the stripped
    //                 Transform (101) - mirrors the real file's added "Sprite" child.
    const string NestedInstanceWithOverriddenRoot =
        "--- !u!1001 &100\nPrefabInstance:\n  serializedVersion: 2\n  m_Modification:\n"
      + "    m_TransformParent: {fileID: 0}\n    m_Modifications: []\n    m_RemovedComponents: []\n"
      + $"  m_SourcePrefab: {{fileID: 100100000, guid: {SourceGuid}, type: 3}}\n"
      + "--- !u!1 &102 stripped\nGameObject:\n"
      + $"  m_CorrespondingSourceObject: {{fileID: 6000, guid: {SourceGuid}, type: 3}}\n"
      + "  m_PrefabInstance: {fileID: 100}\n"
      + "--- !u!114 &103\nMonoBehaviour:\n  m_GameObject: {fileID: 102}\n"
      + "  m_Script: {fileID: 11500000, guid: aaaa1111aaaa1111aaaa1111aaaa1111, type: 3}\n"
      + "--- !u!114 &104\nMonoBehaviour:\n  m_GameObject: {fileID: 102}\n"
      + "  m_Script: {fileID: 11500000, guid: bbbb2222bbbb2222bbbb2222bbbb2222, type: 3}\n"
      + "--- !u!114 &105\nMonoBehaviour:\n  m_GameObject: {fileID: 102}\n"
      + "  m_Script: {fileID: 11500000, guid: dddd4444dddd4444dddd4444dddd4444, type: 3}\n"
      + "--- !u!4 &101 stripped\nTransform:\n"
      + $"  m_CorrespondingSourceObject: {{fileID: 5000, guid: {SourceGuid}, type: 3}}\n"
      + "  m_PrefabInstance: {fileID: 100}\n"
      + "--- !u!1 &106\nGameObject:\n  m_Component:\n  - component: {fileID: 107}\n  m_Name: Sprite\n"
      + "--- !u!4 &107\nTransform:\n  m_GameObject: {fileID: 106}\n  m_Father: {fileID: 101}\n";

    [Fact]
    public void GetHierarchy_ANestedInstanceWithAnOverriddenRoot_SurfacesBothLocalOverrideAnchorsAsIndependentNodes()
    {
        Write("Assets/Nested.prefab", Header + NestedInstanceWithOverriddenRoot);

        var hierarchy = ReadThrough.GetHierarchy(_projectRoot, "Assets/Nested.prefab");

        var placeholders = hierarchy.Roots.Where(r => r.Kind == "PrefabInstance").ToList();
        Assert.Equal(2, placeholders.Count);
        Assert.All(placeholders, p => Assert.Equal(SourceGuid, p.SourcePrefabGuid));
        Assert.All(placeholders, p => Assert.Empty(p.Components)); // unchanged structure-view contract

        var graphicsLike = Assert.Single(placeholders, p => p.FileId == 101);
        var child = Assert.Single(graphicsLike.Children);
        Assert.Equal("Sprite", child.Name);

        var rootLike = Assert.Single(placeholders, p => p.FileId == 102);
        Assert.Empty(rootLike.Children);
    }

    [Fact]
    public void GetComponents_TheOverriddenRootsOwnNode_ResolvesAllThreeLocallyAddedComponents()
    {
        // This is the literal repro: BedroomWithChestAndTable.prefab hides 3 real MonoBehaviour
        // overrides behind "components": []. Confirmed failing against the real, unmodified server
        // before this fix: target=<the real stripped GameObject's fileId> returned
        // {"components": []} - not an exception, but not the 3 real components either.
        Write("Assets/Nested.prefab", Header + NestedInstanceWithOverriddenRoot);

        var components = ReadThrough.GetComponents(_projectRoot, "Assets/Nested.prefab", gameObjectFileId: 102);

        Assert.Equal(3, components.Count);
        Assert.All(components, c => Assert.Equal("MonoBehaviour", c.Kind));
        Assert.Equal(["aaaa1111aaaa1111aaaa1111aaaa1111", "bbbb2222bbbb2222bbbb2222bbbb2222", "dddd4444dddd4444dddd4444dddd4444"],
            components.Select(c => c.ScriptGuid));
    }

    [Fact]
    public void GetComponents_TheStrippedPlaceholderWithNoLocalOverrides_NeverBlamesCorruption()
    {
        // This is the OTHER half of the repro: feeding the tool's OWN documented fileId (the one
        // "structure" actually reports for this exact placeholder) back as 'target' used to throw
        // "the file may be corrupted or hand-edited". Confirmed against the real, unmodified
        // server: BedroomWithChestAndTable.prefab is a completely healthy file. This placeholder
        // (mirroring "Graphics") genuinely owns zero components - reporting that plainly, without
        // the false corruption diagnosis, is the fix.
        Write("Assets/Nested.prefab", Header + NestedInstanceWithOverriddenRoot);

        var ex = Assert.Throws<ArgumentException>(
            () => ReadThrough.GetComponents(_projectRoot, "Assets/Nested.prefab", gameObjectFileId: 101));

        // The fix's own message explicitly DENIES corruption ("...is not corrupted or
        // hand-edited...") - a naive substring check for "corrupted" would false-fail on that
        // very denial, so this targets the specific claim that must never appear instead.
        Assert.DoesNotContain("may be corrupted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetComponents_AGenuinelyBrokenNonStrippedTransform_StillReportsPossibleCorruption()
    {
        // Guards against over-widening the step-2 fix: a Transform that is NOT a nested-instance
        // placeholder yet still has no m_GameObject is exactly the anomalous, possibly-hand-edited
        // case the original message was written for - that diagnosis must survive.
        const string broken = "--- !u!4 &1\nTransform:\n  m_Father: {fileID: 0}\n";
        Write("Assets/Broken.prefab", Header + broken);

        var ex = Assert.Throws<ArgumentException>(
            () => ReadThrough.GetComponents(_projectRoot, "Assets/Broken.prefab", gameObjectFileId: 1));

        Assert.Contains("may be corrupted or hand-edited", ex.Message);
    }

    [Fact]
    public void DocumentedRoundTrip_EveryPrefabInstanceNodeTheStructureViewReports_IsUsableAsTargetWithoutThrowingCorruption()
    {
        // inspect_asset's own documented workflow: call with 'path' to see the structure, then
        // feed a reported node's fileId back as 'target'. Every "PrefabInstance"-kind node the
        // structure view reports must survive that round trip - either with real data, or with an
        // honest, non-alarming explanation - never the false "may be corrupted" diagnosis.
        Write("Assets/Nested.prefab", Header + NestedInstanceWithOverriddenRoot);

        var hierarchy = ReadThrough.GetHierarchy(_projectRoot, "Assets/Nested.prefab");
        var placeholders = hierarchy.Roots.Where(r => r.Kind == "PrefabInstance").ToList();
        Assert.Equal(2, placeholders.Count);

        foreach (var placeholder in placeholders)
        {
            try
            {
                var components = ReadThrough.GetComponents(_projectRoot, "Assets/Nested.prefab", placeholder.FileId);
                Assert.Equal(3, components.Count); // the root-like placeholder (102)
            }
            catch (ArgumentException ex)
            {
                Assert.DoesNotContain("may be corrupted", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ---------------------------------------------------------------- GetComponents: prefab VARIANTS behave the same way

    const string VariantSourceGuid = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    // A PURE variant (the whole file is one PrefabInstance document, m_TransformParent: {fileID:
    // 0}, no local stripped Transform at all - see BuildHierarchy's own doc comment) that ALSO has
    // one component added directly to its root. No real project_aurora example of this exact
    // shape was found (its only 3 pure variants carry zero overrides), so this fixture is
    // synthetic - but built on the same, separately-confirmed Unity serialisation mechanics
    // (a stripped GameObject anchoring an added component) as the nested-instance fixture above,
    // which IS real. Answers this task's own "do variants behave differently" question: they go
    // through the identical code path, so the answer is no.
    const string PureVariantWithOverriddenRoot =
        "--- !u!1001 &200\nPrefabInstance:\n  serializedVersion: 2\n  m_Modification:\n"
      + "    m_TransformParent: {fileID: 0}\n    m_Modifications: []\n    m_RemovedComponents: []\n"
      + $"  m_SourcePrefab: {{fileID: 100100000, guid: {VariantSourceGuid}, type: 3}}\n"
      + "--- !u!1 &201 stripped\nGameObject:\n"
      + $"  m_CorrespondingSourceObject: {{fileID: 7000, guid: {VariantSourceGuid}, type: 3}}\n"
      + "  m_PrefabInstance: {fileID: 200}\n"
      + "--- !u!114 &202\nMonoBehaviour:\n  m_GameObject: {fileID: 201}\n"
      + "  m_Script: {fileID: 11500000, guid: ffff5555ffff5555ffff5555ffff5555, type: 3}\n";

    [Fact]
    public void GetHierarchy_APureVariantWithAnOverriddenRoot_ReportsOnlyTheOverrideAnchorNotADuplicate()
    {
        Write("Assets/Variant.prefab", Header + PureVariantWithOverriddenRoot);

        var hierarchy = ReadThrough.GetHierarchy(_projectRoot, "Assets/Variant.prefab");

        // Exactly one node - the orphan stripped GameObject (201), not also a second, redundant
        // "leftover PrefabInstance" node for the PrefabInstance document (200) itself.
        var root = Assert.Single(hierarchy.Roots);
        Assert.Equal("PrefabInstance", root.Kind);
        Assert.Equal(201, root.FileId);
        Assert.Equal(VariantSourceGuid, root.SourcePrefabGuid);
    }

    [Fact]
    public void GetComponents_APureVariantsOverriddenRoot_ResolvesItsLocallyAddedComponent()
    {
        Write("Assets/Variant.prefab", Header + PureVariantWithOverriddenRoot);

        var components = ReadThrough.GetComponents(_projectRoot, "Assets/Variant.prefab", gameObjectFileId: 201);

        var only = Assert.Single(components);
        Assert.Equal("MonoBehaviour", only.Kind);
        Assert.Equal("ffff5555ffff5555ffff5555ffff5555", only.ScriptGuid);
    }

    [Fact]
    public void GetHierarchy_APureVariantWithNoOverridesAtAll_StillReportsTheOriginalPlaceholderNode()
    {
        // Regression guard: the ordinary, overwhelmingly common case (a variant with zero local
        // overrides - e.g. the real MMRipple.prefab / AchievementDisplay.prefab in project_aurora)
        // must keep getting the SAME single placeholder node it always did.
        const string plainVariant =
            "--- !u!1001 &300\nPrefabInstance:\n  serializedVersion: 2\n  m_Modification:\n"
          + "    m_TransformParent: {fileID: 0}\n    m_Modifications: []\n    m_RemovedComponents: []\n"
          + $"  m_SourcePrefab: {{fileID: 100100000, guid: {VariantSourceGuid}, type: 3}}\n";
        Write("Assets/PlainVariant.prefab", Header + plainVariant);

        var hierarchy = ReadThrough.GetHierarchy(_projectRoot, "Assets/PlainVariant.prefab");

        var root = Assert.Single(hierarchy.Roots);
        Assert.Equal(300, root.FileId);
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

        // Regression test for the dead-tool-name cleanup: this used to point at the now-deleted
        // component_get_all.
        Assert.Contains("call inspect_asset with 'target' again to get current fileIds.", ex.Message);
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
