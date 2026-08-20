using Hades.Core.Unity;

namespace Hades.Core.Tests.Unity;

public class UnityYamlReaderTests
{
    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

    static IReadOnlyList<UnityObject> Read(string body) =>
        UnityYamlReader.Read(Header + body, "Assets/Test.prefab");

    [Fact]
    public void ReadsAGameObjectWithItsNameAndFileId()
    {
        var objects = Read("--- !u!1 &869704862101631421\nGameObject:\n  m_Name: BasePrefab\n  m_IsActive: 1\n");

        var go = Assert.Single(objects);
        Assert.Equal(1, go.ClassId);
        Assert.Equal("GameObject", go.TypeName);
        Assert.Equal(869704862101631421L, go.FileId);
        Assert.Equal("BasePrefab", go.Name);
    }

    [Fact]
    public void ReadsMultipleDocuments()
    {
        var objects = Read("--- !u!1 &111\nGameObject:\n  m_Name: Root\n--- !u!4 &222\nTransform:\n  m_GameObject: {fileID: 111}\n");

        Assert.Equal(2, objects.Count);
        Assert.Equal("GameObject", objects[0].TypeName);
        Assert.Equal("Transform", objects[1].TypeName);
    }

    [Fact]
    public void ExtractsALocalReference()
    {
        var objects = Read("--- !u!4 &222\nTransform:\n  m_GameObject: {fileID: 111}\n");

        var reference = Assert.Single(Assert.Single(objects).References);
        Assert.Equal(111L, reference.FileId);
        Assert.Null(reference.Guid);
        Assert.Equal("m_GameObject", reference.PropertyPath);
        Assert.False(reference.IsExternal);
    }

    [Fact]
    public void ExtractsAnExternalReferenceWithItsGuid()
    {
        var objects = Read("--- !u!114 &333\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: 9848f30f74d55944087b9a0aafbe0e75, type: 3}\n");

        var reference = Assert.Single(Assert.Single(objects).References);
        Assert.Equal("9848f30f74d55944087b9a0aafbe0e75", reference.Guid);
        Assert.Equal(11500000L, reference.FileId);
        Assert.Equal(3, reference.Type);
        Assert.Equal("m_Script", reference.PropertyPath);
        Assert.True(reference.IsExternal);
    }

    [Fact]
    public void ExtractsReferencesThatWrapAcrossLines()
    {
        // 76% of references in the real corpus wrap (38,284 wrapped vs 11,883 single-line).
        // A line-based regex would silently miss three quarters of the reference graph — this is
        // the single most important reason the reader parses rather than pattern-matches.
        var objects = Read("--- !u!1 &444\nGameObject:\n  m_CorrespondingSourceObject: {fileID: 3952813215589545985, guid: 9848f30f74d55944087b9a0aafbe0e75,\n    type: 3}\n");

        var reference = Assert.Single(Assert.Single(objects).References);
        Assert.Equal(3952813215589545985L, reference.FileId);
        Assert.Equal("9848f30f74d55944087b9a0aafbe0e75", reference.Guid);
    }

    [Fact]
    public void PropertyPathExcludesTheTypeNameAndTracksNesting()
    {
        // Paths must be "m_Component.component", not "GameObject.m_Component.component": the
        // outer key names the document, not a property. Verified against the real corpus.
        var objects = Read("--- !u!1 &555\nGameObject:\n  m_Component:\n  - component: {fileID: 111}\n  m_Layer: 0\n");

        Assert.Equal("m_Component.component", Assert.Single(Assert.Single(objects).References).PropertyPath);
    }

    [Fact]
    public void SiblingKeysAfterANestedContainerKeepTheRightPath()
    {
        // Guards the push/pop imbalance: MappingEnd must only pop when MappingStart pushed.
        var objects = Read("--- !u!1 &556\nGameObject:\n  m_Component:\n  - component: {fileID: 111}\n  m_Icon: {fileID: 222}\n");

        var paths = Assert.Single(objects).References.Select(r => r.PropertyPath).ToList();
        Assert.Contains("m_Component.component", paths);
        Assert.Contains("m_Icon", paths);
    }

    [Fact]
    public void CapturesABareFlowMappingDirectlyUnderABlockSequence()
    {
        // Unity writes m_Materials (and any Object[] field) as a block sequence of bare flow
        // mappings - "m_Materials:\n  - {fileID, guid}" - with no per-item key. SequenceStart
        // already consumed "m_Materials" onto the path before this element is seen, so its
        // propertyPath is the array's own key, not a synthesized "m_Materials[0]". Verified
        // against the real corpus: Hades-Unity-Client's Enemy.prefab MeshRenderer, line 93.
        var objects = Read("--- !u!23 &1\nMeshRenderer:\n  m_Materials:\n"
            + "  - {fileID: 2100000, guid: 46ed38f068ed44823b27133f2ce8e23c, type: 2}\n");

        var reference = Assert.Single(Assert.Single(objects).References);
        Assert.Equal(2100000L, reference.FileId);
        Assert.Equal("46ed38f068ed44823b27133f2ce8e23c", reference.Guid);
        Assert.Equal("m_Materials", reference.PropertyPath);
        Assert.True(reference.IsExternal);
    }

    [Fact]
    public void CapturesEveryElementOfAFlowStyleSequence()
    {
        // The bare-flow-mapping-under-a-sequence shape is the same whether the sequence itself
        // is written in block style ("- {...}") or flow style ("[{...}, {...}]") - Unity emits
        // both, and SequenceStart's own handling never branched on that style already. Two
        // elements must yield two references, not one merged read.
        var objects = Read("--- !u!114 &1\nMonoBehaviour:\n"
            + "  m_List: [{fileID: 10, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa}, "
            + "{fileID: 20, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb}]\n");

        var references = Assert.Single(objects).References;
        Assert.Equal(2, references.Count);
        Assert.All(references, r => Assert.Equal("m_List", r.PropertyPath));
        Assert.Contains(references, r => r.FileId == 10 && r.Guid == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        Assert.Contains(references, r => r.FileId == 20 && r.Guid == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    }

    [Fact]
    public void ABareSequenceElementWithNoGuidIsNotCapturedAsAReference()
    {
        // m_Children is the paradigm case: each entry is a bare {fileID: N} with no guid, the
        // mirror image of the SAME Transform's own m_Father (a keyed field, captured via the
        // ordinary path below). Recording both directions would double every parent/child edge
        // in the graph for a fact m_Father already states - see UnityYamlReader's own class doc
        // comment and ReadThrough.BuildHierarchy's, which documents relying on exactly this
        // staying true. Only the local (guid-less) case is dropped; an external array element
        // with the same shape (previous test) is still captured.
        var objects = Read("--- !u!4 &1\nTransform:\n  m_Children:\n  - {fileID: 100}\n  - {fileID: 200}\n  m_Father: {fileID: 0}\n");

        Assert.Empty(Assert.Single(objects).References);
    }

    [Fact]
    public void IgnoresTheNullReference()
    {
        // {fileID: 0} is Unity's null. Recording it would bury the graph in meaningless edges.
        Assert.Empty(Assert.Single(Read("--- !u!1 &557\nGameObject:\n  m_Icon: {fileID: 0}\n")).References);
    }

    [Fact]
    public void HandlesTheStrippedMarker()
    {
        var go = Assert.Single(Read("--- !u!1 &6346727972004658377 stripped\nGameObject:\n  m_Name: Stub\n"));

        Assert.Equal(6346727972004658377L, go.FileId);
        Assert.True(go.IsStripped);
    }

    [Fact]
    public void HandlesNegativeFileIds()
    {
        Assert.Equal(-1766573354249734030L,
            Assert.Single(Read("--- !u!114 &-1766573354249734030\nMonoBehaviour:\n  m_Enabled: 1\n")).FileId);
    }

    [Fact]
    public void HandlesCrlfLineEndings()
    {
        var objects = UnityYamlReader.Read(
            "%YAML 1.1\r\n%TAG !u! tag:unity3d.com,2011:\r\n--- !u!1 &123 stripped\r\nGameObject:\r\n  m_Name: X\r\n",
            "Assets/T.prefab");

        Assert.True(Assert.Single(objects).IsStripped);
    }

    [Fact]
    public void HandlesDuplicateKeys()
    {
        // Animator controllers emit duplicate keys; YamlStream rejects them outright with
        // "Duplicate key data". The event parser must not.
        Assert.Single(Read("--- !u!91 &666\nAnimatorController:\n  data: first\n  data: second\n"));
    }

    [Fact]
    public void UnknownClassIdsStillProduceAnObject()
    {
        Assert.Equal("UnityType_99999", Assert.Single(Read("--- !u!99999 &777\nSomeNewThing:\n  m_Value: 1\n")).TypeName);
    }

    [Fact]
    public void ReturnsNothingForBinaryContent()
    {
        Assert.Empty(UnityYamlReader.Read("\0\0\0binary", "Assets/LightingData.asset"));
    }

    [Fact]
    public void ReturnsNothingForNonUnityContent()
    {
        Assert.Empty(UnityYamlReader.Read("just some text", "Assets/readme.txt"));
    }

    [Fact]
    public void DoesNotThrowOnTruncatedYaml()
    {
        // A file being written while indexed must degrade, never crash the indexer.
        Assert.NotNull(UnityYamlReader.Read(Header + "--- !u!1 &778\nGameObject:\n  m_Name: \"unterminated", "Assets/T.prefab"));
    }

    [Fact]
    public void PoisonClassIdHeaderIsReportedAsUnparseable_NotAnUnhandledOverflow()
    {
        // I1: a class id this large (2^32) overflows Int32. The header pre-scan that parses it
        // used to run OUTSIDE any try/catch, so it threw straight out of Read as a bare
        // OverflowException naming no file at all — aborting a full rebuild entirely and, via
        // ObservationService's blanket catch, vanishing from an incremental sync with zero log
        // output. It must now surface as a typed, catchable, per-file diagnostic instead.
        var content = Header + "--- !u!4294967296 &111\nGameObject:\n  m_Name: Poison\n";

        var ex = Assert.Throws<UnityYamlParseException>(() => UnityYamlReader.Read(content, "Assets/Poison.prefab"));
        Assert.Contains("Assets/Poison.prefab", ex.Message);
    }
}
