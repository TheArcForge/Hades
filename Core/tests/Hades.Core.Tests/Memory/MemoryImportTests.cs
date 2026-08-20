using Hades.Core;
using Hades.Core.Memory;
using Hades.Core.Storage;

namespace Hades.Core.Tests.Memory;

public class MemoryImportTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    const string ProductGuid = "aaaabbbbccccddddeeeeffff00001111";

    MemoryStore NewStore() => new(new AppPaths(_appRoot));

    string ArcforgeMemoryDir => Path.Combine(_projectRoot, ".arcforge", "memory");

    void WriteSourceFile(string relativePath, string content)
    {
        var path = Path.Combine(ArcforgeMemoryDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Import_NoArcforgeDirectoryImportsNothingAndDoesNotError()
    {
        Directory.CreateDirectory(_projectRoot);

        var result = NewStore().ImportFromArcforge(ProductGuid, _projectRoot);

        Assert.Empty(result.Imported);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Import_CopiesTopLevelDocumentsIntoMemoryDir()
    {
        WriteSourceFile("conventions.md", "---\nlast_reviewed: 2026-05-12\n---\n# Conventions\n");
        WriteSourceFile("decisions.md", "# Decisions\n");

        var store = NewStore();
        var result = store.ImportFromArcforge(ProductGuid, _projectRoot);

        Assert.Equal(new[] { "conventions.md", "decisions.md" }, result.Imported.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(result.Skipped);

        var imported = store.Read(ProductGuid, "conventions.md");
        Assert.NotNull(imported);
        Assert.Equal("---\nlast_reviewed: 2026-05-12\n---\n# Conventions\n", imported!.RawText);
    }

    [Fact]
    public void Import_PreservesContentByteForByte()
    {
        // Raw bytes, including a BOM-less UTF8 unicode body - proves the import path never
        // decodes/re-encodes text (it copies bytes), unlike a naive read-then-Write round trip.
        const string content = "---\nstatus: ok\n---\nUnicode: café, 日本語 — em dash, tab\ttab.\r\nCRLF line.\n";
        WriteSourceFile("glossary.md", content);

        var store = NewStore();
        store.ImportFromArcforge(ProductGuid, _projectRoot);

        var destination = Path.Combine(new AppPaths(_appRoot).MemoryDir(ProductGuid), "glossary.md");
        Assert.Equal(File.ReadAllBytes(Path.Combine(ArcforgeMemoryDir, "glossary.md")), File.ReadAllBytes(destination));
    }

    [Fact]
    public void Import_NeverModifiesOrDeletesTheSourceFiles()
    {
        const string content = "# Patterns\n";
        WriteSourceFile("patterns.md", content);
        WriteSourceFile("proposals/idea.md", "---\ntarget_file: patterns\n---\nAn idea.\n");
        WriteSourceFile("inferred/thing.md", "---\nstatus: inferred\n---\nSomething inferred.\n");

        NewStore().ImportFromArcforge(ProductGuid, _projectRoot);

        Assert.Equal(content, File.ReadAllText(Path.Combine(ArcforgeMemoryDir, "patterns.md")));
        Assert.True(File.Exists(Path.Combine(ArcforgeMemoryDir, "proposals", "idea.md")));
        Assert.True(File.Exists(Path.Combine(ArcforgeMemoryDir, "inferred", "thing.md")));
    }

    [Fact]
    public void Import_CopiesProposalsAndInferredIntoTheProposalsSubdirectory()
    {
        WriteSourceFile("proposals/convention-render_pipeline.md", "---\ntarget_file: conventions\n---\nTargets URP.\n");
        WriteSourceFile("inferred/time_of_day-abc123.md", "---\nstatus: inferred\n---\nSomething.\n");

        var store = NewStore();
        var result = store.ImportFromArcforge(ProductGuid, _projectRoot);

        Assert.Equal(
            new[] { "proposals/convention-render_pipeline.md", "proposals/time_of_day-abc123.md" },
            result.Imported.OrderBy(n => n, StringComparer.Ordinal));

        var memoryDir = new AppPaths(_appRoot).MemoryDir(ProductGuid);
        Assert.True(File.Exists(Path.Combine(memoryDir, "proposals", "convention-render_pipeline.md")));
        Assert.True(File.Exists(Path.Combine(memoryDir, "proposals", "time_of_day-abc123.md")));
    }

    [Fact]
    public void Import_OnCollisionBetweenProposalsAndInferredKeepsProposalsAndSkipsInferred()
    {
        // The real Hades-Unity-Client corpus has exactly this: the old auto-inferrer wrote both
        // proposals/convention-prefab_variants.md (the promoted proposal) AND left behind
        // inferred/convention-prefab_variants.md (its own source) under the identical basename.
        // Both would land at memory/proposals/convention-prefab_variants.md - the second one in
        // must be skipped rather than silently overwrite the first.
        WriteSourceFile("proposals/convention-prefab_variants.md", "FROM PROPOSALS\n");
        WriteSourceFile("inferred/convention-prefab_variants.md", "FROM INFERRED\n");

        var store = NewStore();
        var result = store.ImportFromArcforge(ProductGuid, _projectRoot);

        Assert.Equal(new[] { "proposals/convention-prefab_variants.md" }, result.Imported);

        var skip = Assert.Single(result.Skipped);
        Assert.Equal("inferred/convention-prefab_variants.md", skip.Source);
        Assert.False(string.IsNullOrWhiteSpace(skip.Reason));

        var written = File.ReadAllText(Path.Combine(
            new AppPaths(_appRoot).MemoryDir(ProductGuid), "proposals", "convention-prefab_variants.md"));
        Assert.Equal("FROM PROPOSALS\n", written);
    }

    [Fact]
    public void Import_SkipsNonMarkdownFilesLikeInferrerStateWithoutReportingThem()
    {
        // The real corpus has inferred/.conventions-state.json - bookkeeping for the old
        // automatic inferrer, not a memory document. It is simply outside the *.md import
        // surface, not a validation failure, so it must not appear in Skipped either.
        WriteSourceFile("inferred/.conventions-state.json", "{}");
        WriteSourceFile("inferred/real-note.md", "---\nstatus: inferred\n---\nBody.\n");

        var result = NewStore().ImportFromArcforge(ProductGuid, _projectRoot);

        Assert.Equal(new[] { "proposals/real-note.md" }, result.Imported);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Import_IsOneTimeAndDoesNotOverwriteAppSideEditsOnReAdoption()
    {
        WriteSourceFile("conventions.md", "FROM ARCFORGE\n");
        var store = NewStore();

        var first = store.ImportFromArcforge(ProductGuid, _projectRoot);
        Assert.Equal(new[] { "conventions.md" }, first.Imported);

        // A human (or a tool) edits the app-side copy after the first import.
        store.Write(ProductGuid, "conventions.md", "EDITED APP-SIDE\n");

        var second = store.ImportFromArcforge(ProductGuid, _projectRoot);

        Assert.Empty(second.Imported);
        var skip = Assert.Single(second.Skipped);
        Assert.Equal("conventions.md", skip.Source);

        Assert.Equal("EDITED APP-SIDE\n", store.Read(ProductGuid, "conventions.md")!.Body);
    }

    [Fact]
    public void Import_ReAdoptionWithNoChangesReportsEveryFileAsSkipped()
    {
        WriteSourceFile("conventions.md", "# Conventions\n");
        WriteSourceFile("proposals/idea.md", "An idea.\n");
        var store = NewStore();

        store.ImportFromArcforge(ProductGuid, _projectRoot);
        var second = store.ImportFromArcforge(ProductGuid, _projectRoot);

        Assert.Empty(second.Imported);
        Assert.Equal(2, second.Skipped.Count);
    }

    void MakeUnityProject()
    {
        var dir = Path.Combine(_projectRoot, "ProjectSettings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ProjectSettings.asset"), $"  productGUID: {ProductGuid}\n");
    }

    [Fact]
    public void ProjectService_Adopt_ImportsArcforgeMemory()
    {
        MakeUnityProject();
        WriteSourceFile("conventions.md", "---\nlast_reviewed: 2026-05-12\n---\n# Conventions\n");

        var appPaths = new AppPaths(_appRoot);
        var service = new ProjectService(appPaths);

        var project = service.Adopt(_projectRoot);

        Assert.NotNull(project);
        var imported = new MemoryStore(appPaths).Read(ProductGuid, "conventions.md");
        Assert.NotNull(imported);
        Assert.Equal("# Conventions\n", imported!.Body);
    }

    [Fact]
    public void ProjectService_AdoptAndIndex_AlsoImportsArcforgeMemory()
    {
        MakeUnityProject();
        WriteSourceFile("patterns.md", "# Patterns\n");

        var appPaths = new AppPaths(_appRoot);
        var service = new ProjectService(appPaths);

        var project = service.AdoptAndIndex(_projectRoot);

        Assert.NotNull(project);
        Assert.NotNull(new MemoryStore(appPaths).Read(ProductGuid, "patterns.md"));
    }

    [Fact]
    public void ProjectService_Adopt_WithNoArcforgeDirectoryDoesNotErrorOrCreateOne()
    {
        MakeUnityProject();

        var service = new ProjectService(new AppPaths(_appRoot));
        var project = service.Adopt(_projectRoot);

        Assert.NotNull(project);
        Assert.False(Directory.Exists(Path.Combine(_projectRoot, ".arcforge")));
    }

    [Fact]
    public void ProjectService_Adopt_OnlyScansArcforgeOnceProcessPerProject()
    {
        // Adopt runs on every routed tool call (see RootsRouter), so re-scanning .arcforge on
        // every call would repeat filesystem work that only ever needs to happen once. A file
        // added to .arcforge/memory AFTER the first Adopt call in this process is therefore not
        // picked up by a second Adopt call in the SAME process - restarting the server (a fresh
        // ProjectService/process) is what re-attempts the scan.
        MakeUnityProject();
        WriteSourceFile("conventions.md", "# Conventions\n");

        var appPaths = new AppPaths(_appRoot);
        var service = new ProjectService(appPaths);
        service.Adopt(_projectRoot);

        WriteSourceFile("decisions.md", "# Decisions\n");
        service.Adopt(_projectRoot);

        var store = new MemoryStore(appPaths);
        Assert.NotNull(store.Read(ProductGuid, "conventions.md"));
        Assert.Null(store.Read(ProductGuid, "decisions.md"));
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
