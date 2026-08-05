using Hades.Core.Unity;

namespace Hades.Core.Tests.Unity;

public class UnityYamlReaderModificationTests
{
    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

    static IReadOnlyList<UnityObject> Read(string body) =>
        UnityYamlReader.Read(Header + body, "Assets/Test.unity");

    const string Instance = """
        --- !u!1001 &100
        PrefabInstance:
          m_ObjectHideFlags: 0
          serializedVersion: 2
          m_Modification:
            m_TransformParent: {fileID: 200}
            m_Modifications:
            - target: {fileID: 300, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}
              propertyPath: m_Name
              value: Renamed
              objectReference: {fileID: 0}
            - target: {fileID: 301, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}
              propertyPath: m_Sprite
              value: 
              objectReference: {fileID: 21300000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb, type: 3}
            m_RemovedComponents: []
          m_SourcePrefab: {fileID: 100100000, guid: cccccccccccccccccccccccccccccccc, type: 3}
        """;

    [Fact]
    public void KeepsEachModificationAsAStructuredEntry()
    {
        var instance = Assert.Single(Read(Instance));

        Assert.Equal(2, instance.Modifications.Count);
        Assert.Equal("m_Name", instance.Modifications[0].PropertyPath);
        Assert.Equal("Renamed", instance.Modifications[0].Value);
    }

    [Fact]
    public void DistinguishesAReferenceOverrideFromAValueOverride()
    {
        // Only 792 of 44,576 real overrides carry an objectReference. That distinction is the
        // whole basis for what plan 3 stores and what it discards.
        var modifications = Assert.Single(Read(Instance)).Modifications;

        Assert.False(modifications[0].IsReferenceOverride);
        Assert.True(modifications[1].IsReferenceOverride);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", modifications[1].ObjectReference!.Guid);
    }

    [Fact]
    public void KeepsTheModificationTarget()
    {
        var modification = Assert.Single(Read(Instance).Single().Modifications, m => m.IsReferenceOverride);

        Assert.Equal(301L, modification.Target.FileId);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", modification.Target.Guid);
    }

    [Fact]
    public void AnEmptyValueIsPreservedAsEmptyNotNull()
    {
        // "value: " with nothing after it is how Unity writes a reference override's value.
        Assert.Equal(string.Empty, Read(Instance).Single().Modifications[1].Value);
    }

    [Fact]
    public void StillExtractsTheSourcePrefabAsAReference()
    {
        var references = Assert.Single(Read(Instance)).References;

        Assert.Contains(references, r => r.PropertyPath == "m_SourcePrefab"
                                      && r.Guid == "cccccccccccccccccccccccccccccccc");
    }

    [Fact]
    public void ModificationTargetsAreNoLongerAlsoLooseReferences()
    {
        // Deliberate: the structured entry supersedes the loose form. Emitting both would double
        // every override edge, and the structured one is strictly more informative.
        var references = Assert.Single(Read(Instance)).References;

        Assert.DoesNotContain(references, r => r.PropertyPath.EndsWith(".target"));
        Assert.DoesNotContain(references, r => r.PropertyPath.EndsWith(".objectReference"));
    }

    [Fact]
    public void OrdinaryObjectsHaveNoModifications()
    {
        Assert.Empty(Assert.Single(Read("--- !u!1 &1\nGameObject:\n  m_Name: Plain\n")).Modifications);
    }

    [Fact]
    public void CapturesCorrespondingSourceObjectOnStrippedObjects()
    {
        var stripped = Assert.Single(Read(
            "--- !u!1 &6346727972004658377 stripped\nGameObject:\n"
            + "  m_CorrespondingSourceObject: {fileID: 3952813215589545985, guid: dddddddddddddddddddddddddddddddd,\n    type: 3}\n"));

        Assert.True(stripped.IsStripped);
        Assert.Equal("dddddddddddddddddddddddddddddddd", stripped.CorrespondingSourceObject!.Guid);
    }
}
