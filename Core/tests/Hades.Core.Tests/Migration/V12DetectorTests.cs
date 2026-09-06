using System.Text.Json;
using Hades.Core.Migration;

namespace Hades.Core.Tests.Migration;

public sealed class V12DetectorTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public V12DetectorTests() => Directory.CreateDirectory(_projectRoot);

    // ---- fixture helpers ----

    void WriteManifest(string dependencyValue)
    {
        var dir = Path.Combine(_projectRoot, "Packages");
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new
        {
            dependencies = new Dictionary<string, string> { [V12Detector.PackageId] = dependencyValue },
        });
        File.WriteAllText(Path.Combine(dir, "manifest.json"), json);
    }

    void WriteManifestWithoutHadesEntry()
    {
        var dir = Path.Combine(_projectRoot, "Packages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            """{ "dependencies": { "com.unity.textmeshpro": "3.0.6" } }""");
    }

    void WriteMemory(string relativePath, string content = "# doc\n")
    {
        var path = Path.Combine(_projectRoot, ".arcforge", "memory", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    void WriteClaudeMd(string content) => File.WriteAllText(Path.Combine(_projectRoot, "CLAUDE.md"), content);

    void WriteUnityPlugin()
    {
        var dir = Path.Combine(_projectRoot, "Assets", "Hades");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Hades.asmdef"), "{ \"name\": \"Hades\" }");
    }

    static (Dictionary<string, (byte[] Content, DateTime WriteTimeUtc)> Files, HashSet<string> Directories) Snapshot(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'),
            f => (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f)));

        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(root, d).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet();

        return (files, directories);
    }

    // ---- every item absent ----

    [Fact]
    public void Detect_EmptyProject_EveryItemAbsent()
    {
        var result = V12Detector.Detect(_projectRoot);

        Assert.True(result.ProjectRootExists); // the root itself genuinely exists (just empty)
        Assert.False(result.ManifestEntry.Present);
        Assert.Null(result.ManifestEntry.Value);
        Assert.Null(result.ManifestEntry.ResolvedPath);
        Assert.False(result.IsV12Project);
        Assert.False(result.HasMemory);
        Assert.Equal(0, result.MemoryDocumentCount);
        Assert.False(result.HasTraces);
        Assert.False(result.HasGraph);
        Assert.False(result.HasGeneratedMcpConfig);
        Assert.Equal(ClaudeMdShape.Absent, result.ClaudeMd.Shape);
        Assert.Null(result.ClaudeMd.MarkedBlock);
        Assert.False(result.HasUnityPlugin);
    }

    // ---- M6: a missing project root must be distinguishable from "looked, found nothing" ----

    [Fact]
    public void Detect_NonexistentProjectRoot_ReportsProjectRootExistsFalse_WithoutThrowing()
    {
        // Before this fix, a nonexistent path reported the exact same "every item absent" shape as
        // a genuine, freshly-scanned, v1.2-free project - a caller (a project whose folder moved,
        // was deleted, or sits on an unmounted volume, despite still being a KNOWN, previously
        // adopted project) could not tell "confirmed nothing here" apart from "could not even
        // look". ProjectRootExists is that distinction, and every other field must still resolve to
        // its ordinary absent value rather than throwing.
        var missing = Path.Combine(_projectRoot, "does-not-exist");

        var result = V12Detector.Detect(missing);

        Assert.False(result.ProjectRootExists);
        Assert.False(result.IsV12Project);
        Assert.False(result.ManifestEntry.Present);
        Assert.False(result.HasMemory);
        Assert.Equal(0, result.MemoryDocumentCount);
        Assert.False(result.HasTraces);
        Assert.False(result.HasGraph);
        Assert.False(result.HasGeneratedMcpConfig);
        Assert.Equal(ClaudeMdShape.Absent, result.ClaudeMd.Shape);
        Assert.False(result.HasUnityPlugin);
    }

    [Fact]
    public void Detect_ProjectRootIsAFileNotADirectory_ReportsProjectRootExistsFalse()
    {
        // A path that exists on disk but is not a directory at all (e.g. a stray file where a
        // project root used to be) is exactly as "could not look" as a path with nothing there -
        // Directory.Exists is correctly false for a plain file, and this must read the same way.
        var filePath = Path.Combine(_projectRoot, "not-a-directory.txt");
        File.WriteAllText(filePath, "oops");

        var result = V12Detector.Detect(filePath);

        Assert.False(result.ProjectRootExists);
    }

    // ---- every item present ----

    [Fact]
    public void Detect_FullV12Project_EveryItemPresent()
    {
        WriteManifest("file:/Users/mike/Projects/Hades");
        WriteMemory("conventions.md");
        WriteMemory("decisions.md");
        WriteMemory("proposals/idea.md");
        WriteMemory("inferred/thing.md");
        Directory.CreateDirectory(Path.Combine(_projectRoot, ".arcforge"));
        File.WriteAllText(Path.Combine(_projectRoot, ".arcforge", "traces.db"), "sqlite-traces");
        File.WriteAllText(Path.Combine(_projectRoot, ".arcforge", "graph.db"), "sqlite-graph");
        File.WriteAllText(Path.Combine(_projectRoot, ".mcp.json"), "{}");
        WriteClaudeMd($"before\n{V12Detector.StartMarker}\nblock\n{V12Detector.EndMarker}\nafter\n");
        WriteUnityPlugin();

        var result = V12Detector.Detect(_projectRoot);

        Assert.True(result.ProjectRootExists);
        Assert.True(result.IsV12Project);
        Assert.True(result.ManifestEntry.Present);
        Assert.True(result.HasMemory);
        Assert.Equal(4, result.MemoryDocumentCount);
        Assert.True(result.HasTraces);
        Assert.True(result.HasGraph);
        Assert.True(result.HasGeneratedMcpConfig);
        Assert.Equal(ClaudeMdShape.Marked, result.ClaudeMd.Shape);
        Assert.NotNull(result.ClaudeMd.MarkedBlock);
        Assert.True(result.HasUnityPlugin);
    }

    // ---- the manifest entry: both forms, and its absence ----

    [Fact]
    public void Detect_ManifestEntry_FileForm_ReportsValueAndResolvedAbsolutePath()
    {
        // Absolute in the shape the running platform actually uses. This used to hardcode
        // "/Users/mike/Projects/Hades", which IS rooted on Windows — but rooted without a drive, so
        // Path.GetFullPath correctly resolves it against the current one and returns
        // "D:\Users\mike\Projects\Hades". The assertion was pinning the literal the test wrote
        // rather than the contract, which is "an already-absolute file: path resolves to itself".
        var absolute = OperatingSystem.IsWindows()
            ? @"C:\Projects\Hades"
            : "/Users/mike/Projects/Hades";

        WriteManifest($"file:{absolute}");

        var result = V12Detector.Detect(_projectRoot);

        Assert.True(result.IsV12Project);
        Assert.True(result.ManifestEntry.Present);
        Assert.Equal($"file:{absolute}", result.ManifestEntry.Value);
        Assert.Equal(absolute, result.ManifestEntry.ResolvedPath);
    }

    [Fact]
    public void Detect_ManifestEntry_RegistryVersionForm_ReportsValueWithNoResolvedPath()
    {
        WriteManifest("1.2.3");

        var result = V12Detector.Detect(_projectRoot);

        Assert.True(result.IsV12Project);
        Assert.True(result.ManifestEntry.Present);
        Assert.Equal("1.2.3", result.ManifestEntry.Value);
        Assert.Null(result.ManifestEntry.ResolvedPath);
    }

    [Fact]
    public void Detect_ManifestEntry_RelativeFilePath_ResolvesAgainstPackagesDirectory()
    {
        // Unity resolves a relative "file:" dependency against Packages/ itself - the same rule
        // Indexing.ProjectWalker.ReadLocalPackages already applies when scanning local packages.
        WriteManifest("file:LocalHades");

        var result = V12Detector.Detect(_projectRoot);

        Assert.True(result.ManifestEntry.Present);
        Assert.Equal("file:LocalHades", result.ManifestEntry.Value);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_projectRoot, "Packages", "LocalHades")),
            result.ManifestEntry.ResolvedPath);
    }

    [Fact]
    public void Detect_ManifestEntry_EmptyFilePathSuffix_DoesNotThrowAndLeavesResolvedPathNull()
    {
        WriteManifest("file:");

        var result = V12Detector.Detect(_projectRoot);

        Assert.True(result.ManifestEntry.Present);
        Assert.Equal("file:", result.ManifestEntry.Value);
        Assert.Null(result.ManifestEntry.ResolvedPath);
    }

    [Fact]
    public void Detect_ManifestWithoutTheHadesEntry_IsNotAV12Project()
    {
        WriteManifestWithoutHadesEntry();

        var result = V12Detector.Detect(_projectRoot);

        Assert.False(result.IsV12Project);
        Assert.False(result.ManifestEntry.Present);
        Assert.Null(result.ManifestEntry.Value);
    }

    [Fact]
    public void Detect_NoManifestFile_IsNotAV12ProjectAndDoesNotThrow()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "Packages"));

        var result = V12Detector.Detect(_projectRoot);

        Assert.False(result.IsV12Project);
        Assert.False(result.ManifestEntry.Present);
    }

    [Fact]
    public void Detect_MalformedManifestJson_DoesNotThrowAndReportsNotAV12Project()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "Packages"));
        File.WriteAllText(Path.Combine(_projectRoot, "Packages", "manifest.json"), "{ not valid json ");

        var result = V12Detector.Detect(_projectRoot);

        Assert.False(result.IsV12Project);
        Assert.False(result.ManifestEntry.Present);
    }

    // ---- CLAUDE.md: three shapes, honestly told apart (or not) ----

    [Fact]
    public void Detect_ClaudeMd_Absent_ReportsAbsentShape()
    {
        var result = V12Detector.Detect(_projectRoot);

        Assert.Equal(ClaudeMdShape.Absent, result.ClaudeMd.Shape);
        Assert.Null(result.ClaudeMd.MarkedBlock);
    }

    [Fact]
    public void Detect_ClaudeMd_Marked_ReportsShapeAndExactBlockExtent()
    {
        const string before = "# My notes\n\n";
        const string block = "guidance from hades\nmore guidance\n";
        const string after = "\n# My own section after\n";
        var content = before + V12Detector.StartMarker + "\n" + block + V12Detector.EndMarker + after;
        WriteClaudeMd(content);

        var result = V12Detector.Detect(_projectRoot);

        Assert.Equal(ClaudeMdShape.Marked, result.ClaudeMd.Shape);
        Assert.NotNull(result.ClaudeMd.MarkedBlock);
        var start = result.ClaudeMd.MarkedBlock!.Start;
        var end = result.ClaudeMd.MarkedBlock!.End;

        // The extent covers exactly the marker pair (and what's between them) - nothing outside
        // it, which is the entire contract task 4 needs: cut [start, end) and every other byte
        // must survive untouched.
        Assert.Equal(before, content[..start]);
        Assert.Equal(after, content[end..]);
        Assert.StartsWith(V12Detector.StartMarker, content[start..end], StringComparison.Ordinal);
        Assert.EndsWith(V12Detector.EndMarker, content[start..end], StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_ClaudeMd_UnmarkedHadesAuthoredWholesale_ReportsUnmarkedShape()
    {
        // A real shape-2 file: what v1.2 wrote before HADES:START/END markers existed at all -
        // the Hades-Unity-Client reference project's own CLAUDE.md carries exactly this text,
        // unmarked, sitting ahead of a later-added marked block.
        WriteClaudeMd(
            "# Hades — Agent Guidelines\n\n" +
            "This is a Unity project with Hades installed. You have 89 MCP tools that give you " +
            "deep structural understanding of the project.\n");

        var result = V12Detector.Detect(_projectRoot);

        Assert.Equal(ClaudeMdShape.Unmarked, result.ClaudeMd.Shape);
        Assert.Null(result.ClaudeMd.MarkedBlock);
    }

    [Fact]
    public void Detect_ClaudeMd_HandWritten_AlsoReportsUnmarkedShape()
    {
        // Shape 3: nothing to do with Hades at all. Deliberately asserts the SAME outcome as
        // the shape-2 test above - see V12Detector's remarks on why the detector does not (and
        // cannot reliably) tell these two apart.
        WriteClaudeMd(
            "# Team conventions\n\n" +
            "We use tabs, not spaces. PRs need two approvals. Ping #eng-platform for infra.\n");

        var result = V12Detector.Detect(_projectRoot);

        Assert.Equal(ClaudeMdShape.Unmarked, result.ClaudeMd.Shape);
        Assert.Null(result.ClaudeMd.MarkedBlock);
    }

    [Theory]
    [InlineData("some text <!-- HADES:START -->\nblock with no end\n")]
    [InlineData("<!-- HADES:END -->\nend before start\n<!-- HADES:START -->\n")]
    public void Detect_ClaudeMd_MalformedMarkerPair_FallsBackToUnmarkedRatherThanGuessing(string content)
    {
        WriteClaudeMd(content);

        var result = V12Detector.Detect(_projectRoot);

        Assert.Equal(ClaudeMdShape.Unmarked, result.ClaudeMd.Shape);
        Assert.Null(result.ClaudeMd.MarkedBlock);
    }

    [Theory]
    [InlineData("Notes\n\n<!-- HADES:START -->\nInner A\n<!-- HADES:START -->\nInner B\n<!-- HADES:END -->\nInner C\n<!-- HADES:END -->\nFinal\n")]
    [InlineData("<!-- HADES:START -->\nfirst block\n<!-- HADES:END -->\n\n<!-- HADES:START -->\nsecond block\n<!-- HADES:END -->\n")]
    public void Detect_ClaudeMd_NestedOrDuplicateMarkers_FallsBackToUnmarkedRatherThanGuessing(string content)
    {
        // A second START or END anywhere in the file - nested inside the first pair (first
        // InlineData) or a second, separate well-formed pair later in the file (second InlineData)
        // - makes which pair is "the" block genuinely ambiguous. V12Detector.ReadClaudeMd used to
        // pair the FIRST start with the FIRST end and call that Shape.Marked regardless, which is
        // exactly the shape V12Cleanup's own multiplicity guard (CountOccurrences(...) != 1)
        // already refuses to act on - see V12CleanupTests' own coverage of this file's first
        // InlineData. The detector must not report Marked for a file no consumer should act on.
        WriteClaudeMd(content);

        var result = V12Detector.Detect(_projectRoot);

        Assert.Equal(ClaudeMdShape.Unmarked, result.ClaudeMd.Shape);
        Assert.Null(result.ClaudeMd.MarkedBlock);
    }

    // ---- memory document count ----

    [Fact]
    public void Detect_MemoryDocumentCount_CountsTopLevelProposalsAndInferredMarkdownFiles()
    {
        WriteMemory("conventions.md");
        WriteMemory("decisions.md");
        WriteMemory("glossary.md");
        WriteMemory("proposals/idea-one.md");
        WriteMemory("proposals/idea-two.md");
        WriteMemory("inferred/pattern-one.md");

        var result = V12Detector.Detect(_projectRoot);

        Assert.True(result.HasMemory);
        Assert.Equal(6, result.MemoryDocumentCount);
    }

    [Fact]
    public void Detect_MemoryDocumentCount_ExcludesNonMarkdownBookkeepingFiles()
    {
        // The real corpus has exactly this: inferred/.conventions-state.json, bookkeeping for
        // the old automatic inferrer, not a memory document.
        WriteMemory("conventions.md");
        WriteMemory("inferred/.conventions-state.json", "{}");

        var result = V12Detector.Detect(_projectRoot);

        Assert.Equal(1, result.MemoryDocumentCount);
    }

    [Fact]
    public void Detect_MemoryDirectoryExistsButEmpty_HasMemoryTrueWithZeroDocuments()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, ".arcforge", "memory"));

        var result = V12Detector.Detect(_projectRoot);

        Assert.True(result.HasMemory);
        Assert.Equal(0, result.MemoryDocumentCount);
    }

    // ---- independence: absence of one item is ordinary, not a reason to hide the rest ----

    [Fact]
    public void Detect_MemoryPresentButTracesAbsent_EachItemReportedIndependently()
    {
        // "A project may have memory but no traces" - the brief's own example.
        WriteMemory("conventions.md");

        var result = V12Detector.Detect(_projectRoot);

        Assert.True(result.HasMemory);
        Assert.Equal(1, result.MemoryDocumentCount);
        Assert.False(result.HasTraces);
        Assert.False(result.HasGraph);
        Assert.False(result.HasGeneratedMcpConfig);
        Assert.False(result.HasUnityPlugin);
        Assert.Equal(ClaudeMdShape.Absent, result.ClaudeMd.Shape);
        Assert.False(result.IsV12Project);
    }

    [Fact]
    public void Detect_TracesAndGraphPresentButNoMemory_EachItemReportedIndependently()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, ".arcforge"));
        File.WriteAllText(Path.Combine(_projectRoot, ".arcforge", "traces.db"), "sqlite-traces");
        File.WriteAllText(Path.Combine(_projectRoot, ".arcforge", "graph.db"), "sqlite-graph");

        var result = V12Detector.Detect(_projectRoot);

        Assert.False(result.HasMemory);
        Assert.Equal(0, result.MemoryDocumentCount);
        Assert.True(result.HasTraces);
        Assert.True(result.HasGraph);
    }

    [Fact]
    public void Detect_AssetsHadesFolderWithoutAsmdef_IsNotReportedAsThePlugin()
    {
        // "Hades" is also a well-known game title - a bare Assets/Hades/ folder is not on its
        // own good evidence of the Hades Unity plugin. Only the specific file the installer
        // actually writes (see Editors.PluginInstaller) counts.
        Directory.CreateDirectory(Path.Combine(_projectRoot, "Assets", "Hades"));
        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "Hades", "SomeGameScript.cs"),
            "public class SomeGameScript {}");

        var result = V12Detector.Detect(_projectRoot);

        Assert.False(result.HasUnityPlugin);
    }

    // ---- the test that matters most: detection never writes ----

    [Fact]
    public void Detect_NeverWritesMovesOrDeletesAnythingInTheProject()
    {
        // A full fixture, deliberately duplicated here rather than shared with
        // Detect_FullV12Project_EveryItemPresent: this test's whole point is a byte-for-byte
        // and mtime-for-mtime before/after comparison, so it must own a fixture that is not
        // touched by (or coupled to) any other test.
        WriteManifest("file:/Users/mike/Projects/Hades");
        WriteMemory("conventions.md");
        WriteMemory("proposals/idea.md");
        WriteMemory("inferred/thing.md");
        Directory.CreateDirectory(Path.Combine(_projectRoot, ".arcforge"));
        File.WriteAllText(Path.Combine(_projectRoot, ".arcforge", "traces.db"), "sqlite-traces");
        File.WriteAllText(Path.Combine(_projectRoot, ".arcforge", "graph.db"), "sqlite-graph");
        File.WriteAllText(Path.Combine(_projectRoot, ".mcp.json"), "{}");
        WriteClaudeMd($"before\n{V12Detector.StartMarker}\nblock\n{V12Detector.EndMarker}\nafter\n");
        WriteUnityPlugin();

        var (filesBefore, dirsBefore) = Snapshot(_projectRoot);
        Assert.NotEmpty(filesBefore); // sanity: the fixture really did seed files

        var result = V12Detector.Detect(_projectRoot);

        // Touch every property so a hypothetically lazy accessor can't hide a side effect that
        // a "call Detect and ignore the result" pass would miss.
        _ = result.ProjectRootExists;
        _ = result.IsV12Project;
        _ = result.ManifestEntry;
        _ = result.HasMemory;
        _ = result.MemoryDocumentCount;
        _ = result.HasTraces;
        _ = result.HasGraph;
        _ = result.HasGeneratedMcpConfig;
        _ = result.ClaudeMd;
        _ = result.HasUnityPlugin;

        // Detection must be safe to run repeatedly (e.g. onboarding re-checking after the user
        // switches projects and back), not just safe once.
        V12Detector.Detect(_projectRoot);

        var (filesAfter, dirsAfter) = Snapshot(_projectRoot);

        Assert.Equal(dirsBefore, dirsAfter);
        Assert.Equal(
            filesBefore.Keys.OrderBy(k => k, StringComparer.Ordinal),
            filesAfter.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (path, before) in filesBefore)
        {
            var after = filesAfter[path];
            Assert.True(before.Content.AsSpan().SequenceEqual(after.Content), $"'{path}' content changed after detection");
            Assert.Equal(before.WriteTimeUtc, after.WriteTimeUtc);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }
}
