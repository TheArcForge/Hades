using Hades.Core.Memory;
using Hades.Core.Migration;
using Hades.Core.Storage;

namespace Hades.Core.Tests.Migration;

public sealed class V12ImporterTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    const string ProductGuid = "aaaabbbbccccddddeeeeffff00001111";

    string ArcforgeDir => Path.Combine(_projectRoot, ".arcforge");
    string ArcforgeMemoryDir => Path.Combine(ArcforgeDir, "memory");

    V12Importer NewImporter() => new(new AppPaths(_appRoot));

    // ---- fixture helpers ----

    void WriteSourceMemoryFile(string relativePath, string content)
    {
        var path = Path.Combine(ArcforgeMemoryDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    void WriteSourceTraces(string content = "sqlite-traces-bytes")
    {
        Directory.CreateDirectory(ArcforgeDir);
        File.WriteAllText(Path.Combine(ArcforgeDir, "traces.db"), content);
    }

    void WriteSourceGraph(string content = "sqlite-graph-bytes")
    {
        Directory.CreateDirectory(ArcforgeDir);
        File.WriteAllText(Path.Combine(ArcforgeDir, "graph.db"), content);
    }

    static (byte[] Content, DateTime WriteTimeUtc) Snapshot(string path) =>
        (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));

    // ---- memory: this class does not re-implement import, it calls the existing one ----

    [Fact]
    public void ImportMemory_CopiesTopLevelDocuments_SameShapeAsMemoryStoreImportFromArcforge()
    {
        WriteSourceMemoryFile("conventions.md", "# Conventions\n");
        WriteSourceMemoryFile("decisions.md", "# Decisions\n");

        var result = NewImporter().ImportMemory(ProductGuid, _projectRoot);

        Assert.Equal(new[] { "conventions.md", "decisions.md" }, result.Imported.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(result.Skipped);

        var body = new MemoryStore(new AppPaths(_appRoot)).Read(ProductGuid, "conventions.md")!.RawText;
        Assert.Equal("# Conventions\n", body);
    }

    [Fact]
    public void ImportMemory_NoArcforgeDirectory_ImportsNothingWithoutError()
    {
        Directory.CreateDirectory(_projectRoot);

        var result = NewImporter().ImportMemory(ProductGuid, _projectRoot);

        Assert.Empty(result.Imported);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void ImportMemory_CalledAgainAfterProjectServiceAdoptAlreadyImported_IsSafeAndReportsAlreadyThere()
    {
        // ProjectService.Adopt already imports memory automatically, unconditionally, on first
        // sight of any project (v1.2 or not) - see MemoryStore.ImportFromArcforge's own doc
        // comment and ProjectService.Adopt. V12Importer.ImportMemory calls that exact same method,
        // so calling it again later (e.g. from a migration screen, after Adopt already ran) must
        // be safe: idempotent, and merely reports what is already there rather than duplicating
        // work or throwing.
        WriteSourceMemoryFile("conventions.md", "# Conventions\n");
        var appPaths = new AppPaths(_appRoot);
        var importer = new V12Importer(appPaths);

        var first = importer.ImportMemory(ProductGuid, _projectRoot);
        Assert.Equal(new[] { "conventions.md" }, first.Imported);

        var second = importer.ImportMemory(ProductGuid, _projectRoot);

        Assert.Empty(second.Imported);
        var skip = Assert.Single(second.Skipped);
        Assert.Equal("conventions.md", skip.Source);
    }

    // ---- the requirement that matters most: the source is provably untouched ----

    [Fact]
    public void ImportMemory_SourceTree_IsByteAndMtimeIdenticalAfterImport()
    {
        WriteSourceMemoryFile("conventions.md", "# Conventions\n");
        WriteSourceMemoryFile("proposals/idea.md", "An idea.\n");
        WriteSourceMemoryFile("inferred/thing.md", "Something.\n");

        var files = new[] { "conventions.md", "proposals/idea.md", "inferred/thing.md" }
            .Select(rel => Path.Combine(ArcforgeMemoryDir, rel)).ToList();
        var before = files.ToDictionary(f => f, Snapshot);

        NewImporter().ImportMemory(ProductGuid, _projectRoot);

        foreach (var file in files)
        {
            var after = Snapshot(file);
            Assert.True(before[file].Content.AsSpan().SequenceEqual(after.Content), $"'{file}' content changed after import");
            Assert.Equal(before[file].WriteTimeUtc, after.WriteTimeUtc);
        }
    }

    // ---- collisions: never overwrite; report and leave both sides intact ----

    [Fact]
    public void ImportMemory_ExistingAppSideDocument_IsNeverOverwritten_BothSidesIntact()
    {
        WriteSourceMemoryFile("conventions.md", "FROM ARCFORGE\n");
        var appPaths = new AppPaths(_appRoot);
        new MemoryStore(appPaths).Write(ProductGuid, "conventions.md", "APP-SIDE, EDITED BY A HUMAN\n");

        var result = NewImporter().ImportMemory(ProductGuid, _projectRoot);

        Assert.Empty(result.Imported);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal("conventions.md", skip.Source);
        Assert.False(string.IsNullOrWhiteSpace(skip.Reason));

        // Both sides survive unchanged - the collision is reported, not resolved, by the importer.
        Assert.Equal("APP-SIDE, EDITED BY A HUMAN\n", new MemoryStore(appPaths).Read(ProductGuid, "conventions.md")!.RawText);
        Assert.Equal("FROM ARCFORGE\n", File.ReadAllText(Path.Combine(ArcforgeMemoryDir, "conventions.md")));
    }

    // ---- the real-world shape: leading-hyphen names, a blank frontmatter field, multi-way collisions ----

    [Fact]
    public void ImportMemory_RealWorldShapedCorpus_MalformedFilesSurviveAndEveryCollisionResolvesTheSameWay()
    {
        // Mirrors the actual Hades-Unity-Client corpus shape, confirmed by read-only inspection
        // (never pointed at the real project itself - see synthetic fixtures below): two
        // leading-hyphen inferred/ filenames with a blank YAML scalar field, plus three basenames
        // that exist in BOTH proposals/ and inferred/ at once (not just one, as the corpus's
        // narrower unit tests exercise).
        WriteSourceMemoryFile("conventions.md", "# Conventions\n");
        WriteSourceMemoryFile("inferred/-render_pipeline.md",
            "---\nstatus: inferred\nanalyzer: \nconfidence: 0.95\n---\nINFERRED PATTERN (not confirmed by team)\n");
        WriteSourceMemoryFile("inferred/-prefab_variants.md",
            "---\nstatus: inferred\nanalyzer: \nconfidence: 1.00\n---\nINFERRED PATTERN (not confirmed by team)\n");
        WriteSourceMemoryFile("proposals/convention-naming.md", "FROM PROPOSALS: naming\n");
        WriteSourceMemoryFile("inferred/convention-naming.md", "FROM INFERRED: naming\n");
        WriteSourceMemoryFile("proposals/convention-prefab_variants.md", "FROM PROPOSALS: prefab_variants\n");
        WriteSourceMemoryFile("inferred/convention-prefab_variants.md", "FROM INFERRED: prefab_variants\n");
        WriteSourceMemoryFile("proposals/convention-render_pipeline.md", "FROM PROPOSALS: render_pipeline\n");
        WriteSourceMemoryFile("inferred/convention-render_pipeline.md", "FROM INFERRED: render_pipeline\n");
        WriteSourceMemoryFile("inferred/.conventions-state.json", "{}"); // bookkeeping, not a document

        var appPaths = new AppPaths(_appRoot);
        var result = NewImporter().ImportMemory(ProductGuid, _projectRoot);
        var memoryDir = appPaths.MemoryDir(ProductGuid);

        // All three collisions resolve the same way: proposals/ wins, inferred/'s copy is skipped.
        Assert.Equal("FROM PROPOSALS: naming\n", File.ReadAllText(Path.Combine(memoryDir, "proposals", "convention-naming.md")));
        Assert.Equal("FROM PROPOSALS: prefab_variants\n", File.ReadAllText(Path.Combine(memoryDir, "proposals", "convention-prefab_variants.md")));
        Assert.Equal("FROM PROPOSALS: render_pipeline\n", File.ReadAllText(Path.Combine(memoryDir, "proposals", "convention-render_pipeline.md")));

        var collisionSkips = result.Skipped.Where(s => s.Source.StartsWith("inferred/convention-", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, collisionSkips.Count);
        Assert.All(collisionSkips, s => Assert.Contains("already exists", s.Reason, StringComparison.OrdinalIgnoreCase));

        // The two malformed, leading-hyphen, blank-field documents are real data and survive the
        // trip: imported under their literal on-disk names, byte-for-byte, not "fixed".
        Assert.Contains("proposals/-render_pipeline.md", result.Imported);
        Assert.Contains("proposals/-prefab_variants.md", result.Imported);
        var renderPipelineBody = File.ReadAllText(Path.Combine(memoryDir, "proposals", "-render_pipeline.md"));
        Assert.Contains("analyzer: \n", renderPipelineBody);

        // The non-markdown bookkeeping file is neither imported nor reported as a skip.
        Assert.DoesNotContain(result.Imported, n => n.EndsWith(".json", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Skipped, s => s.Source.EndsWith(".json", StringComparison.Ordinal));
    }

    // ---- traces: optional, its own call, its own outcome ----

    [Fact]
    public void ImportTraces_CopiesTracesDbByteForByte()
    {
        WriteSourceTraces("sqlite-traces-bytes-blob");

        var result = NewImporter().ImportTraces(ProductGuid, _projectRoot);

        Assert.True(result.Imported);
        Assert.Null(result.SkippedReason);
        Assert.Equal("sqlite-traces-bytes-blob", File.ReadAllText(new AppPaths(_appRoot).TracesDb(ProductGuid)));
    }

    [Fact]
    public void ImportTraces_NoSourceTracesDb_ReportsNotImportedWithReasonAndWritesNothing()
    {
        Directory.CreateDirectory(_projectRoot);

        var result = NewImporter().ImportTraces(ProductGuid, _projectRoot);

        Assert.False(result.Imported);
        Assert.False(string.IsNullOrWhiteSpace(result.SkippedReason));
        Assert.False(File.Exists(new AppPaths(_appRoot).TracesDb(ProductGuid)));
    }

    [Fact]
    public void ImportTraces_ExistingAppSideTracesDb_IsNeverOverwritten_BothSidesIntact()
    {
        WriteSourceTraces("FROM ARCFORGE");
        var appPaths = new AppPaths(_appRoot);
        appPaths.EnsureProjectDir(ProductGuid);
        File.WriteAllText(appPaths.TracesDb(ProductGuid), "ALREADY IN APP STORAGE");

        var result = NewImporter().ImportTraces(ProductGuid, _projectRoot);

        Assert.False(result.Imported);
        Assert.Contains("already exists", result.SkippedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ALREADY IN APP STORAGE", File.ReadAllText(appPaths.TracesDb(ProductGuid)));
        Assert.Equal("FROM ARCFORGE", File.ReadAllText(Path.Combine(ArcforgeDir, "traces.db")));
    }

    [Fact]
    public void ImportTraces_SourceFile_IsByteAndMtimeIdenticalAfterImport()
    {
        WriteSourceTraces("sqlite-traces-bytes");
        var path = Path.Combine(ArcforgeDir, "traces.db");
        var before = Snapshot(path);

        NewImporter().ImportTraces(ProductGuid, _projectRoot);

        var after = Snapshot(path);
        Assert.True(before.Content.AsSpan().SequenceEqual(after.Content));
        Assert.Equal(before.WriteTimeUtc, after.WriteTimeUtc);
    }

    [Fact]
    public void ImportTraces_CopiesWalAndShmSidecarsWhenPresent()
    {
        // The real Hades-Unity-Client project has both sidecars present right now: traces.db is
        // WAL-mode SQLite, and a v1.2 install can still be running against a project during
        // migration (spec #4 §5: "v1.2 keeps working throughout"). A copy of only the main file
        // would silently miss whatever is sitting in an as-yet-unchecked-pointed WAL.
        Directory.CreateDirectory(ArcforgeDir);
        File.WriteAllText(Path.Combine(ArcforgeDir, "traces.db"), "main-db-bytes");
        File.WriteAllText(Path.Combine(ArcforgeDir, "traces.db-wal"), "wal-bytes");
        File.WriteAllText(Path.Combine(ArcforgeDir, "traces.db-shm"), "shm-bytes");

        var result = NewImporter().ImportTraces(ProductGuid, _projectRoot);

        Assert.True(result.Imported);
        var destination = new AppPaths(_appRoot).TracesDb(ProductGuid);
        Assert.Equal("main-db-bytes", File.ReadAllText(destination));
        Assert.Equal("wal-bytes", File.ReadAllText(destination + "-wal"));
        Assert.Equal("shm-bytes", File.ReadAllText(destination + "-shm"));
    }

    [Fact]
    public void ImportTraces_NoSidecarsPresent_StillCopiesMainFileFine()
    {
        WriteSourceTraces("main-only");

        var result = NewImporter().ImportTraces(ProductGuid, _projectRoot);

        Assert.True(result.Imported);
        var destination = new AppPaths(_appRoot).TracesDb(ProductGuid);
        Assert.False(File.Exists(destination + "-wal"));
        Assert.False(File.Exists(destination + "-shm"));
    }

    // ---- independence: memory and traces are separate calls, neither depends on the other ----

    [Fact]
    public void ImportMemory_WithoutEverCallingImportTraces_WritesNoTracesFile()
    {
        WriteSourceMemoryFile("conventions.md", "# Conventions\n");
        WriteSourceTraces();

        var result = NewImporter().ImportMemory(ProductGuid, _projectRoot);

        Assert.Equal(new[] { "conventions.md" }, result.Imported);
        Assert.False(File.Exists(new AppPaths(_appRoot).TracesDb(ProductGuid)));
    }

    [Fact]
    public void ImportTraces_WithoutEverCallingImportMemory_StillSucceeds()
    {
        WriteSourceTraces("standalone-traces");

        var result = NewImporter().ImportTraces(ProductGuid, _projectRoot);

        Assert.True(result.Imported);
    }

    // ---- the graph is never imported: not copied, and never even opened ----

    [Fact]
    public void ImportMemory_AndImportTraces_NeverCopyGraphDbIntoAppStorage()
    {
        WriteSourceMemoryFile("conventions.md", "# Conventions\n");
        WriteSourceTraces();
        WriteSourceGraph("sqlite-graph-bytes");

        var importer = NewImporter();
        importer.ImportMemory(ProductGuid, _projectRoot);
        importer.ImportTraces(ProductGuid, _projectRoot);

        var projectDir = new AppPaths(_appRoot).ProjectDir(ProductGuid);
        Assert.False(Directory.Exists(projectDir) && Directory.EnumerateFiles(projectDir, "graph.db*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void ImportMemory_AndImportTraces_NeverOpenGraphDbEvenWhenItIsUnreadable()
    {
        // The strongest available proof of "never opened": graph.db exists (File.Exists true) but
        // is unreadable even to its own owner. If either import path ever so much as opened it -
        // for a read, a copy, anything - this throws. Both calls completing normally, with their
        // ordinary successful results, is the proof that neither one touches it.
        if (OperatingSystem.IsWindows())
        {
            return; // File.SetUnixFileMode is a Unix chmod equivalent; Windows ACLs differ.
        }

        if (Environment.IsPrivilegedProcess)
        {
            return; // Root bypasses permission bits entirely; the chmod below would not bite.
        }

        WriteSourceMemoryFile("conventions.md", "# Conventions\n");
        WriteSourceTraces();
        WriteSourceGraph("sqlite-graph-bytes");
        var graphPath = Path.Combine(ArcforgeDir, "graph.db");
        var originalMode = File.GetUnixFileMode(graphPath);
        File.SetUnixFileMode(graphPath, UnixFileMode.None);

        try
        {
            var importer = NewImporter();
            var memoryResult = importer.ImportMemory(ProductGuid, _projectRoot);
            var tracesResult = importer.ImportTraces(ProductGuid, _projectRoot);

            Assert.Contains("conventions.md", memoryResult.Imported);
            Assert.True(tracesResult.Imported);
        }
        finally
        {
            File.SetUnixFileMode(graphPath, originalMode);
        }
    }

    [Fact]
    public void ImportMemory_AndImportTraces_GraphDbSourceIsByteAndMtimeIdenticalAfterward()
    {
        WriteSourceMemoryFile("conventions.md", "# Conventions\n");
        WriteSourceTraces();
        WriteSourceGraph("sqlite-graph-bytes");
        var graphPath = Path.Combine(ArcforgeDir, "graph.db");
        var before = Snapshot(graphPath);

        var importer = NewImporter();
        importer.ImportMemory(ProductGuid, _projectRoot);
        importer.ImportTraces(ProductGuid, _projectRoot);

        var after = Snapshot(graphPath);
        Assert.True(before.Content.AsSpan().SequenceEqual(after.Content));
        Assert.Equal(before.WriteTimeUtc, after.WriteTimeUtc);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot })
        {
            if (!Directory.Exists(dir)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }
}
