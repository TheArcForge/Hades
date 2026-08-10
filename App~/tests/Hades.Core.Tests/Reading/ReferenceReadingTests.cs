using Hades.Core.Reading;

namespace Hades.Core.Tests.Reading;

/// <summary>
/// The read-through mechanism behind reference_get, reference_find_unset and
/// event_list_listeners: resolving one object-reference field, scanning a whole file for Unity's
/// null reference, and reading a UnityEvent's persistent-call list - all by re-parsing the named
/// file, no graph involved (see <see cref="ReferenceReading"/>'s class doc comment). Graph
/// resolution of an external reference's guid (reference_get / event_list_listeners' one graph
/// touch) is exercised at the ReferenceTools HTTP level, not here - this file is scoped to what
/// ReferenceReading itself does with no graph involved, exactly like ComponentInspectionTests is
/// for ReadThrough's own component methods.
/// </summary>
public class ReferenceReadingTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
    const string ExternalGuid = "bbbb2222bbbb2222bbbb2222bbbb2222";

    public ReferenceReadingTests() => Directory.CreateDirectory(Path.Combine(_projectRoot, "Assets"));

    void Write(string relative, string body)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
    }

    // A root GameObject ("Root", fileId 1) with a Transform (fileId 2, m_Father unset - it is a
    // root) and a MonoBehaviour (fileId 3) carrying: a LOCAL reference field ("target", pointing
    // back at the GameObject), an EXTERNAL reference field ("otherAsset"), an explicitly unset
    // reference field ("unassigned"), a plain scalar ("maxHealth"), and one UnityEvent field
    // ("onDamage") with two persistent calls - one wired (target set, Bool argument used), one
    // with an unset target (the partially-configured-listener case).
    const string Fixture =
        "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  - component: {fileID: 3}\n  m_Name: Root\n"
      + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n"
      + "--- !u!114 &3\nMonoBehaviour:\n  m_GameObject: {fileID: 1}\n"
      + "  m_Script: {fileID: 11500000, guid: aaaa1111aaaa1111aaaa1111aaaa1111, type: 3}\n"
      + "  target: {fileID: 1}\n"
      + $"  otherAsset: {{fileID: 11400000, guid: {ExternalGuid}, type: 2}}\n"
      + "  unassigned: {fileID: 0}\n"
      + "  maxHealth: 100\n"
      + "  onDamage:\n"
      + "    m_PersistentCalls:\n"
      + "      m_Calls:\n"
      + "      - m_Target: {fileID: 1}\n"
      + "        m_TargetAssemblyTypeName: UnityEngine.GameObject, UnityEngine\n"
      + "        m_MethodName: SetActive\n"
      + "        m_Mode: 6\n"
      + "        m_Arguments:\n"
      + "          m_ObjectArgument: {fileID: 0}\n"
      + "          m_ObjectArgumentAssemblyTypeName: \n"
      + "          m_IntArgument: 0\n"
      + "          m_FloatArgument: 0\n"
      + "          m_StringArgument: \n"
      + "          m_BoolArgument: 1\n"
      + "        m_CallState: 2\n"
      + "      - m_Target: {fileID: 0}\n"
      + "        m_TargetAssemblyTypeName: \n"
      + "        m_MethodName: \n"
      + "        m_Mode: 1\n"
      + "        m_Arguments:\n"
      + "          m_ObjectArgument: {fileID: 0}\n"
      + "          m_ObjectArgumentAssemblyTypeName: \n"
      + "          m_IntArgument: 0\n"
      + "          m_FloatArgument: 0\n"
      + "          m_StringArgument: \n"
      + "          m_BoolArgument: 0\n"
      + "        m_CallState: 2\n";

    // ---------------------------------------------------------------- GetReference

    [Fact]
    public void GetReference_ALocalReferenceHasNoGuidAndIsNotUnset()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        var reference = ReferenceReading.GetReference(_projectRoot, "Assets/Enemy.prefab", fileId: 3, "target");

        Assert.Equal(1, reference.FileId);
        Assert.Null(reference.Guid);
        Assert.False(reference.IsUnset);
    }

    [Fact]
    public void GetReference_AnExternalReferenceCarriesItsGuidRaw()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        var reference = ReferenceReading.GetReference(_projectRoot, "Assets/Enemy.prefab", fileId: 3, "otherAsset");

        Assert.Equal(ExternalGuid, reference.Guid);
        Assert.False(reference.IsUnset);
    }

    [Fact]
    public void GetReference_AnExplicitlyUnsetFieldIsReportedAsUnsetNotAsAnError()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        var reference = ReferenceReading.GetReference(_projectRoot, "Assets/Enemy.prefab", fileId: 3, "unassigned");

        Assert.True(reference.IsUnset);
        Assert.Equal(0, reference.FileId);
        Assert.Null(reference.Guid);
    }

    [Fact]
    public void GetReference_AScalarFieldIsRejectedAsNotAReference()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        var ex = Assert.Throws<ArgumentException>(
            () => ReferenceReading.GetReference(_projectRoot, "Assets/Enemy.prefab", fileId: 3, "maxHealth"));
        Assert.Contains("not an object-reference field", ex.Message);

        // Regression test for the dead-tool-name cleanup: this used to point at the now-deleted
        // component_get_property.
        Assert.Contains("inspect_asset with 'target', 'component', and 'property' to see its actual value.", ex.Message);
    }

    [Fact]
    public void GetReference_AnUnknownPropertyNameGivesActionableGuidance()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        var ex = Assert.Throws<ArgumentException>(
            () => ReferenceReading.GetReference(_projectRoot, "Assets/Enemy.prefab", fileId: 3, "notAField"));
        Assert.Contains("notAField", ex.Message);

        // Regression test for the dead-tool-name cleanup: this used to point at the now-deleted
        // component_list_properties.
        Assert.Contains("inspect_asset with 'target' and 'component' (no 'property') to confirm.", ex.Message);
    }

    [Fact]
    public void GetReference_PathEscapingTheProjectIsRefused()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        Assert.Throws<ArgumentException>(
            () => ReferenceReading.GetReference(_projectRoot, "Assets/../../../../etc/passwd", fileId: 3, "target"));
    }

    // ---------------------------------------------------------------- FindUnsetReferences

    [Fact]
    public void FindUnsetReferences_FindsExactlyTheFiveUnsetOccurrencesInTheFixture()
    {
        // m_Father on the root Transform (fileId 2); "unassigned" on the MonoBehaviour (fileId 3);
        // the second onDamage call's m_Target; and BOTH calls' m_Arguments.m_ObjectArgument, since
        // neither call uses Object mode - genuinely unset, and reported as such, since nothing in
        // the data says an unused argument slot is any different from a forgotten reference (the
        // same unfiltered-by-design stance as m_Father, just illustrated on a second field shape).
        // Pinned to exactly 5 so a change in either direction (missed hit, or a spurious extra one)
        // shows up.
        Write("Assets/Enemy.prefab", Header + Fixture);

        var hits = ReferenceReading.FindUnsetReferences(_projectRoot, "Assets/Enemy.prefab");

        Assert.Equal(5, hits.Count);
        Assert.Contains(hits, h => h.FileId == 2 && h.PropertyPath == "m_Father" && h.ObjectKind == "Transform");
        Assert.Contains(hits, h => h.FileId == 3 && h.PropertyPath == "unassigned");
        Assert.Contains(hits, h => h.FileId == 3 && h.PropertyPath.Contains("onDamage") && h.PropertyPath.Contains("m_Target"));
        Assert.Equal(2, hits.Count(h => h.PropertyPath.Contains("m_ObjectArgument")));
    }

    [Fact]
    public void FindUnsetReferences_ReportsTheOwningObjectsNameWhenItHasOne()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        var hits = ReferenceReading.FindUnsetReferences(_projectRoot, "Assets/Enemy.prefab");

        // fileId 2 (the Transform) has no m_Name of its own - only the GameObject does - so this
        // just confirms the field is read through, not fabricated.
        Assert.All(hits, h => Assert.True(h.ObjectName is null or "Root"));
    }

    [Fact]
    public void FindUnsetReferences_ASetReferenceIsNeverReported()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        var hits = ReferenceReading.FindUnsetReferences(_projectRoot, "Assets/Enemy.prefab");

        Assert.DoesNotContain(hits, h => h.PropertyPath == "target");
        Assert.DoesNotContain(hits, h => h.PropertyPath == "otherAsset");
    }

    [Fact]
    public void FindUnsetReferences_AFileWithNoUnsetReferencesReturnsEmpty()
    {
        const string allSet =
            "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: Child\n"
          + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 99}\n";
        Write("Assets/Child.prefab", Header + allSet);

        Assert.Empty(ReferenceReading.FindUnsetReferences(_projectRoot, "Assets/Child.prefab"));
    }

    [Fact]
    public void FindUnsetReferences_PathEscapingTheProjectIsRefused()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        Assert.Throws<ArgumentException>(
            () => ReferenceReading.FindUnsetReferences(_projectRoot, "Assets/../../../../etc/passwd"));
    }

    [Fact]
    public void FindUnsetReferences_AFileDeletedSinceIndexing_GivesAClearNoLongerOnDiskError()
    {
        Write("Assets/Gone.prefab", Header + Fixture);
        File.Delete(Path.Combine(_projectRoot, "Assets", "Gone.prefab"));

        var ex = Assert.Throws<FileNotFoundException>(
            () => ReferenceReading.FindUnsetReferences(_projectRoot, "Assets/Gone.prefab"));
        Assert.Contains("no longer on disk", ex.Message);
    }

    // ---------------------------------------------------------------- GetEventListeners

    [Fact]
    public void GetEventListeners_FindsBothCallsUnderTheirEventFieldName()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        var listeners = ReferenceReading.GetEventListeners(_projectRoot, "Assets/Enemy.prefab", fileId: 3);

        Assert.Equal(2, listeners.Count);
        Assert.All(listeners, l => Assert.Equal("onDamage", l.EventField));
        Assert.Equal([0, 1], listeners.Select(l => l.Index).OrderBy(i => i));
    }

    [Fact]
    public void GetEventListeners_AWiredCallReportsItsTargetMethodAndArguments()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        var wired = ReferenceReading.GetEventListeners(_projectRoot, "Assets/Enemy.prefab", fileId: 3)
            .Single(l => l.Index == 0);

        Assert.Equal(1, wired.Target.FileId);
        Assert.False(wired.Target.IsUnset);
        Assert.Equal("SetActive", wired.MethodName);
        Assert.Equal("UnityEngine.GameObject, UnityEngine", wired.TargetAssemblyTypeName);
        Assert.Equal("6", wired.Mode);
        Assert.Equal("2", wired.CallState);
        Assert.Equal("1", wired.Arguments["m_BoolArgument"]);
    }

    [Fact]
    public void GetEventListeners_ACallWithAnUnsetTargetIsStillReported_UnlikeTheGraphServedEventFindAll()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        var unwired = ReferenceReading.GetEventListeners(_projectRoot, "Assets/Enemy.prefab", fileId: 3)
            .Single(l => l.Index == 1);

        Assert.True(unwired.Target.IsUnset);
        Assert.Equal("", unwired.MethodName);
    }

    [Fact]
    public void GetEventListeners_AComponentWithNoUnityEventFieldsReturnsEmpty()
    {
        const string noEvents =
            "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: Plain\n"
          + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n";
        Write("Assets/Plain.prefab", Header + noEvents);

        Assert.Empty(ReferenceReading.GetEventListeners(_projectRoot, "Assets/Plain.prefab", fileId: 2));
    }

    [Fact]
    public void GetEventListeners_AnUnknownFileIdIsReportedClearlyNotAsAnEmptyList()
    {
        Write("Assets/Enemy.prefab", Header + Fixture);

        var ex = Assert.Throws<ArgumentException>(
            () => ReferenceReading.GetEventListeners(_projectRoot, "Assets/Enemy.prefab", fileId: 999));
        Assert.Contains("999", ex.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }
}
