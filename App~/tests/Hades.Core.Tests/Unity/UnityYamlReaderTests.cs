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
}
