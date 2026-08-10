using Hades.Core.Projects;

namespace Hades.Core.Tests.Projects;

/// <summary>
/// Plan 15 Task 3: reconstructing the C# preprocessor define set Hades applies when indexing —
/// UNITY_EDITOR (always), the version ladder derived from ProjectVersion.txt, and
/// scriptingDefineSymbols read from ProjectSettings.asset. See <see cref="ProjectDefines"/>'s own
/// class doc comment for the shortcut this deliberately does NOT take (stripping #if/#else so both
/// branches land in the graph) and the per-assembly-union caveat this whole approach carries.
/// </summary>
public class ProjectDefinesTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    void WriteProjectVersion(string editorVersion)
    {
        var dir = Path.Combine(_root, "ProjectSettings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ProjectVersion.txt"),
            $"m_EditorVersion: {editorVersion}\nm_EditorVersionWithRevision: {editorVersion} (a9779f353c9b)\n");
    }

    void WriteProjectSettings(string contents)
    {
        var dir = Path.Combine(_root, "ProjectSettings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ProjectSettings.asset"), contents);
    }

    void WriteManifest(string json)
    {
        var dir = Path.Combine(_root, "Packages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), json);
    }

    void WriteLock(string json)
    {
        var dir = Path.Combine(_root, "Packages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "packages-lock.json"), json);
    }

    void WriteAsmdef(string relativePath, string json)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, json);
    }

    // --- VersionLadder: pure function, no filesystem ------------------------------------------

    [Fact]
    public void VersionLadder_BuildsExactSymbolsForAUnity6Version()
    {
        // Ground truth: both project_aurora and Hades-Unity-Client report exactly this version
        // (ProjectSettings/ProjectVersion.txt, checked 2026-08-07). Unity's own docs
        // (docs.unity3d.com/6000.3/Documentation/Manual/scripting-symbol-reference.html) give
        // 6000.0.33 as their worked example: UNITY_X ("release version"), UNITY_X_Y ("major
        // version"), UNITY_X_Y_Z ("minor version") - X stays the full "6000", never abbreviated
        // to "6". The OR_NEWER ladder is the mechanically-derivable part: every minor from 0 up
        // to the project's own, within the SAME major version.
        var symbols = ProjectDefines.VersionLadder("6000.3.2f1");

        Assert.Equal(
            new[]
            {
                "UNITY_6000",
                "UNITY_6000_3",
                "UNITY_6000_3_2",
                "UNITY_6000_0_OR_NEWER",
                "UNITY_6000_1_OR_NEWER",
                "UNITY_6000_2_OR_NEWER",
                "UNITY_6000_3_OR_NEWER",
            },
            symbols);
    }

    [Fact]
    public void VersionLadder_IsSchemeAgnosticForOlderYearBasedVersions()
    {
        // Same algorithm, no special-casing for the "6000" epoch - Unity's own general docs use
        // 2019.4.14 as their X.Y.Z example, so the year-based scheme must produce the same shape.
        var symbols = ProjectDefines.VersionLadder("2022.3.45f1");

        Assert.Equal(
            new[]
            {
                "UNITY_2022",
                "UNITY_2022_3",
                "UNITY_2022_3_45",
                "UNITY_2022_0_OR_NEWER",
                "UNITY_2022_1_OR_NEWER",
                "UNITY_2022_2_OR_NEWER",
                "UNITY_2022_3_OR_NEWER",
            },
            symbols);
    }

    [Fact]
    public void VersionLadder_HandlesMinorZeroWithoutANegativeRange()
    {
        var symbols = ProjectDefines.VersionLadder("6000.0.5f1");

        Assert.Equal(
            new[] { "UNITY_6000", "UNITY_6000_0", "UNITY_6000_0_5", "UNITY_6000_0_OR_NEWER" },
            symbols);
    }

    [Fact]
    public void VersionLadder_ReturnsEmptyForAnUnparseableVersionString()
    {
        // Never throws — a version string in some future or unrecognised shape degrades to "no
        // ladder", not a crash that would take down an entire index run.
        Assert.Empty(ProjectDefines.VersionLadder("not-a-version"));
        Assert.Empty(ProjectDefines.VersionLadder(""));
    }

    // --- Resolve: reads ProjectVersion.txt + ProjectSettings.asset off disk -------------------

    [Fact]
    public void Resolve_AppliesOnlyUnityEditorWhenNeitherFileExists()
    {
        // No ProjectSettings directory at all — several existing ScriptIndexer fixtures look
        // exactly like this. Must degrade gracefully, never throw.
        var result = ProjectDefines.Resolve(_root);

        Assert.Equal(["UNITY_EDITOR"], result.Symbols);
        Assert.Null(result.UnityVersion);
    }

    [Fact]
    public void Resolve_AddsTheVersionLadderWhenProjectVersionExists()
    {
        WriteProjectVersion("6000.3.2f1");

        var result = ProjectDefines.Resolve(_root);

        Assert.Contains("UNITY_EDITOR", result.Symbols);
        Assert.Contains("UNITY_6000_3_OR_NEWER", result.Symbols);
        Assert.Contains("UNITY_6000_3_2", result.Symbols);
        Assert.Equal("6000.3.2f1", result.UnityVersion);
    }

    [Fact]
    public void Resolve_AddsScriptingDefineSymbolsFromTheStandaloneTarget()
    {
        // Real shape from project_aurora's ProjectSettings.asset (2026-08-07), trimmed to the
        // one relevant key.
        WriteProjectSettings("""
              productGUID: aaaabbbbccccddddeeeeffff00001111
              scriptingDefineSymbols:
                Standalone: ODIN_INSPECTOR;ODIN_INSPECTOR_3
              additionalCompilerArguments: {}
            """);

        var result = ProjectDefines.Resolve(_root);

        Assert.Contains("ODIN_INSPECTOR", result.Symbols);
        Assert.Contains("ODIN_INSPECTOR_3", result.Symbols);
        Assert.Equal("Standalone", result.ScriptingDefineSymbolsTarget);
    }

    [Fact]
    public void Resolve_IgnoresOtherBuildTargetsScriptingDefineSymbols()
    {
        // Only the editor's own desktop platform is read (see ProjectDefines' own class doc
        // comment for why) — a mobile-only define must not leak into every project's graph.
        WriteProjectSettings("""
              productGUID: aaaabbbbccccddddeeeeffff00001111
              scriptingDefineSymbols:
                Android: MOBILE_ONLY_DEFINE
              additionalCompilerArguments: {}
            """);

        var result = ProjectDefines.Resolve(_root);

        Assert.DoesNotContain("MOBILE_ONLY_DEFINE", result.Symbols);
        Assert.Null(result.ScriptingDefineSymbolsTarget);
    }

    [Fact]
    public void Resolve_TreatsAnEmptyScriptingDefineSymbolsMapAsNoUserDefines()
    {
        // Real shape from Hades-Unity-Client's ProjectSettings.asset (2026-08-07) — the ordinary
        // case for a project that has never added a custom define.
        WriteProjectSettings("""
              productGUID: aaaabbbbccccddddeeeeffff00001111
              scriptingDefineSymbols: {}
              additionalCompilerArguments: {}
            """);

        var result = ProjectDefines.Resolve(_root);

        Assert.Equal(["UNITY_EDITOR"], result.Symbols);
        Assert.Null(result.ScriptingDefineSymbolsTarget);
    }

    [Fact]
    public void Resolve_CombinesEditorLadderAndUserDefinesSortedAndDeduplicated()
    {
        WriteProjectVersion("6000.3.2f1");
        WriteProjectSettings("""
              productGUID: aaaabbbbccccddddeeeeffff00001111
              scriptingDefineSymbols:
                Standalone: MY_CUSTOM_DEFINE;UNITY_EDITOR
              additionalCompilerArguments: {}
            """);

        var result = ProjectDefines.Resolve(_root);

        Assert.Equal(result.Symbols.Distinct(StringComparer.Ordinal).Count(), result.Symbols.Count);
        Assert.Equal(result.Symbols.OrderBy(s => s, StringComparer.Ordinal), result.Symbols);
        Assert.Contains("MY_CUSTOM_DEFINE", result.Symbols);
        Assert.Contains("UNITY_6000_3_OR_NEWER", result.Symbols);
    }

    // --- VersionDefineSatisfied: pure function, no filesystem ---------------------------------
    // Plan 15 Task 4: Unity's own versionDefines expression grammar
    // (docs.unity3d.com/6000.3/Documentation/Manual/assembly-definition-includes.html, checked
    // 2026-08-07) - interval notation with "[" inclusive / "(" exclusive bounds, a bracketed
    // single version meaning exact equality, a bare version meaning ">= that version", and an
    // empty expression meaning "any version". See ProjectDefines' own class doc comment for the
    // forms this deliberately does NOT support.

    [Fact]
    public void VersionDefineSatisfied_EmptyExpressionMatchesAnyVersion()
    {
        Assert.True(ProjectDefines.VersionDefineSatisfied("", "0.0.1"));
        Assert.True(ProjectDefines.VersionDefineSatisfied("   ", "9.9.9"));
    }

    [Fact]
    public void VersionDefineSatisfied_BareVersionIsAShortcutForAtLeast()
    {
        // Unity's own worked example: "2.1.0-preview.7" evaluates to "x >= 2.1.0-preview.7".
        // Real astar shape: AstarPathfindingProject.asmdef gates MODULE_BURST on bare "1.8.7".
        Assert.True(ProjectDefines.VersionDefineSatisfied("1.8.7", "1.8.7"));
        Assert.True(ProjectDefines.VersionDefineSatisfied("1.8.7", "1.8.26"));
        Assert.False(ProjectDefines.VersionDefineSatisfied("1.8.7", "1.8.6"));
    }

    [Fact]
    public void VersionDefineSatisfied_BracketedSingleVersionIsExactEquality()
    {
        // Unity's own worked example: "[2.4.5]" evaluates to "x = 2.4.5".
        Assert.True(ProjectDefines.VersionDefineSatisfied("[2.4.5]", "2.4.5"));
        Assert.False(ProjectDefines.VersionDefineSatisfied("[2.4.5]", "2.4.6"));
        Assert.False(ProjectDefines.VersionDefineSatisfied("[2.4.5]", "2.4.4"));
    }

    [Fact]
    public void VersionDefineSatisfied_InclusiveRangeIncludesBothEndpoints()
    {
        // Unity's own worked example: "[1.3,3.4.1]" evaluates to "1.3.0 <= x <= 3.4.1".
        Assert.True(ProjectDefines.VersionDefineSatisfied("[1.3,3.4.1]", "1.3.0"));
        Assert.True(ProjectDefines.VersionDefineSatisfied("[1.3,3.4.1]", "3.4.1"));
        Assert.True(ProjectDefines.VersionDefineSatisfied("[1.3,3.4.1]", "2.0.0"));
        Assert.False(ProjectDefines.VersionDefineSatisfied("[1.3,3.4.1]", "1.2.9"));
        Assert.False(ProjectDefines.VersionDefineSatisfied("[1.3,3.4.1]", "3.4.2"));
    }

    [Fact]
    public void VersionDefineSatisfied_ExclusiveRangeExcludesBothEndpoints()
    {
        // Unity's own worked example: "(1.3.0,3.4)" evaluates to "1.3.0 < x < 3.4.0".
        Assert.False(ProjectDefines.VersionDefineSatisfied("(1.3.0,3.4)", "1.3.0"));
        Assert.False(ProjectDefines.VersionDefineSatisfied("(1.3.0,3.4)", "3.4.0"));
        Assert.True(ProjectDefines.VersionDefineSatisfied("(1.3.0,3.4)", "1.3.1"));
        Assert.True(ProjectDefines.VersionDefineSatisfied("(1.3.0,3.4)", "3.3.9"));
    }

    [Fact]
    public void VersionDefineSatisfied_MixedInclusiveExclusiveBounds()
    {
        // Unity's own worked example: "[1.1,3.4)" evaluates to "1.1.0 <= x < 3.4.0".
        Assert.True(ProjectDefines.VersionDefineSatisfied("[1.1,3.4)", "1.1.0"));
        Assert.False(ProjectDefines.VersionDefineSatisfied("[1.1,3.4)", "3.4.0"));
        Assert.True(ProjectDefines.VersionDefineSatisfied("[1.1,3.4)", "3.3.9"));
    }

    [Fact]
    public void VersionDefineSatisfied_AReleaseOutranksAPreReleaseAtTheSameNumericTriple()
    {
        // Unity's own worked example: "(0.2.4,5.6.2-preview.2]" evaluates to
        // "0.2.4 < x <= 5.6.2-preview.2" - which only makes sense if the release 5.6.2 sorts
        // ABOVE 5.6.2-preview.2 and so EXCEEDS this inclusive upper bound (standard SemVer
        // precedence, semver.org #11.4: a release outranks any pre-release at the same triple).
        Assert.True(ProjectDefines.VersionDefineSatisfied("(0.2.4,5.6.2-preview.2]", "5.6.2-preview.2"));
        Assert.False(ProjectDefines.VersionDefineSatisfied("(0.2.4,5.6.2-preview.2]", "5.6.2"));
        Assert.False(ProjectDefines.VersionDefineSatisfied("(0.2.4,5.6.2-preview.2]", "0.2.4"));
    }

    [Fact]
    public void VersionDefineSatisfied_TwoDifferentlyLabelledVersionsCompareOrdinally_DocumentedLimitation()
    {
        // KNOWN, DOCUMENTED simplification (see ProjectDefines' own class doc comment): two
        // DIFFERENT pre-release labels at the same MAJOR.MINOR.PATCH compare as plain ordinal
        // strings, not numeric-aware SemVer precedence - "preview.9" sorts AFTER "preview.10"
        // character-by-character ('9' > '1'), the opposite of numeric truth (9 < 10). Unity's own
        // docs do not specify a comparison algorithm for this case, and no real fixture this
        // project validates against needs one. Pinned here so the limitation is visible, not
        // silently different from what a reader would assume "SemVer" means.
        Assert.True(ProjectDefines.VersionDefineSatisfied("1.0.0-preview.10", "1.0.0-preview.9"));
    }

    [Fact]
    public void VersionDefineSatisfied_NeverSatisfiedForUnparseableInputRatherThanGuessing()
    {
        // Fails closed, never a phantom: a git URL (not a SemVer) as the "installed version", a
        // malformed expression, or a range with more than one comma all degrade to "not
        // satisfied" rather than a guess.
        Assert.False(ProjectDefines.VersionDefineSatisfied("1.0.0", "https://github.com/example/pkg.git#v1.0.0"));
        Assert.False(ProjectDefines.VersionDefineSatisfied("not-a-valid-expression!!", "1.0.0"));
        Assert.False(ProjectDefines.VersionDefineSatisfied("[1.0,2.0,3.0]", "1.5.0"));
    }

    // --- Resolve: versionDefines, read off asmdef + manifest.json/packages-lock.json ----------

    [Fact]
    public void Resolve_AppliesAVersionDefineWhenTheManifestSatisfiesIt()
    {
        WriteAsmdef("Assets/Test.asmdef", """
            {"name":"Test","versionDefines":[{"name":"com.example.widgets","expression":"1.0.0","define":"MODULE_WIDGETS"}]}
            """);
        WriteManifest("""{"dependencies":{"com.example.widgets":"1.2.0"}}""");

        var result = ProjectDefines.Resolve(_root);

        Assert.Contains("MODULE_WIDGETS", result.Symbols);
    }

    [Fact]
    public void Resolve_DoesNotApplyAVersionDefineWhenTheInstalledVersionIsTooLow()
    {
        WriteAsmdef("Assets/Test.asmdef", """
            {"name":"Test","versionDefines":[{"name":"com.example.widgets","expression":"2.0.0","define":"MODULE_WIDGETS"}]}
            """);
        WriteManifest("""{"dependencies":{"com.example.widgets":"1.2.0"}}""");

        var result = ProjectDefines.Resolve(_root);

        Assert.DoesNotContain("MODULE_WIDGETS", result.Symbols);
    }

    [Fact]
    public void Resolve_DoesNotApplyAVersionDefineForAnUninstalledPackage()
    {
        WriteAsmdef("Assets/Test.asmdef", """
            {"name":"Test","versionDefines":[{"name":"com.example.widgets","expression":"","define":"MODULE_WIDGETS"}]}
            """);
        // No manifest.json / packages-lock.json at all.

        var result = ProjectDefines.Resolve(_root);

        Assert.DoesNotContain("MODULE_WIDGETS", result.Symbols);
    }

    [Fact]
    public void Resolve_NeverResolvesABuiltinModuleNamePlaceholderVersion()
    {
        // Unity's own manifest.json convention: a built-in module (com.unity.modules.*) always
        // carries a fixed, meaningless "1.0.0" placeholder - confirmed against project_aurora's
        // real manifest.json (2026-08-07). Unity's REAL resolution compares a built-in module's
        // version against the EDITOR's own version, not this placeholder - not implemented here
        // (see ProjectDefines' own class doc comment) - so this must never be treated as
        // "installed at 1.0.0", which could otherwise produce a define no real compile sets. The
        // expression below WOULD be satisfied by "1.0.0" if it were (wrongly) allowed through.
        WriteAsmdef("Assets/Test.asmdef", """
            {"name":"Test","versionDefines":[{"name":"com.unity.modules.physics","expression":"[0.5,2.0)","define":"WOULD_BE_WRONG"}]}
            """);
        WriteManifest("""{"dependencies":{"com.unity.modules.physics":"1.0.0"}}""");

        var result = ProjectDefines.Resolve(_root);

        Assert.DoesNotContain("WOULD_BE_WRONG", result.Symbols);
    }

    [Fact]
    public void Resolve_FallsBackToPackagesLockJsonForATransitiveDependency()
    {
        // See ProjectDefines' own class doc comment for why: project_aurora's own
        // com.unity.burst/mathematics/collections are pulled in transitively by
        // com.unity.entities and never appear as a direct Packages/manifest.json dependency -
        // only packages-lock.json (Unity's own record of what actually got resolved) has them.
        WriteAsmdef("Assets/Test.asmdef", """
            {"name":"Test","versionDefines":[{"name":"com.example.burst","expression":"1.8.7","define":"MODULE_BURST"}]}
            """);
        WriteLock("""{"dependencies":{"com.example.burst":{"version":"1.8.26","depth":1,"source":"registry"}}}""");

        var result = ProjectDefines.Resolve(_root);

        Assert.Contains("MODULE_BURST", result.Symbols);
    }

    [Fact]
    public void Resolve_PrefersPackagesLockJsonOverManifestWhenBothDisagree()
    {
        // packages-lock.json is Unity's own record of what actually got resolved, cached
        // alongside Library/PackageCache - the closer of the two to "what the compiler used" if
        // manifest.json was hand-edited since the last resolve. See ProjectDefines' own class
        // doc comment.
        WriteAsmdef("Assets/Test.asmdef", """
            {"name":"Test","versionDefines":[{"name":"com.example.widgets","expression":"2.0.0","define":"MODULE_WIDGETS"}]}
            """);
        WriteManifest("""{"dependencies":{"com.example.widgets":"3.0.0"}}""");
        WriteLock("""{"dependencies":{"com.example.widgets":{"version":"1.0.0","depth":0,"source":"registry"}}}""");

        var result = ProjectDefines.Resolve(_root);

        Assert.DoesNotContain("MODULE_WIDGETS", result.Symbols);   // 1.0.0 does not satisfy ">=2.0.0"
    }

    [Fact]
    public void Resolve_UnionsVersionDefinesAcrossMultipleAsmdefFiles()
    {
        WriteAsmdef("Assets/A.asmdef", """
            {"name":"A","versionDefines":[{"name":"com.example.widgets","expression":"1.0.0","define":"MODULE_WIDGETS"}]}
            """);
        WriteAsmdef("Assets/B.asmdef", """
            {"name":"B","versionDefines":[{"name":"com.example.gadgets","expression":"1.0.0","define":"MODULE_GADGETS"}]}
            """);
        WriteManifest("""{"dependencies":{"com.example.widgets":"1.5.0","com.example.gadgets":"2.0.0"}}""");

        var result = ProjectDefines.Resolve(_root);

        Assert.Contains("MODULE_WIDGETS", result.Symbols);
        Assert.Contains("MODULE_GADGETS", result.Symbols);
    }

    [Fact]
    public void Resolve_IgnoresAMalformedAsmdefWithoutThrowing()
    {
        WriteAsmdef("Assets/Broken.asmdef", "{ not valid json");
        WriteManifest("""{"dependencies":{"com.example.widgets":"1.5.0"}}""");

        var result = ProjectDefines.Resolve(_root);   // must not throw

        Assert.Equal(["UNITY_EDITOR"], result.Symbols);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
