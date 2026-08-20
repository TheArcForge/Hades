using Hades.Core.Graph;
using Hades.Core.Indexing;

namespace Hades.Core.Tests.Indexing;

public class ScriptIndexerTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    void WriteScript(string relativePath, string source)
    {
        var full = Path.Combine(_projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, source);
    }

    GraphDatabase OpenGraph() => GraphDatabase.Open(Path.Combine(_projectRoot, "graph.db"));

    [Fact]
    public void IndexesEveryScriptUnderAssets()
    {
        WriteScript("Assets/Scripts/Player.cs", "public class Player { }");
        WriteScript("Assets/Scripts/Enemy.cs", "public class Enemy { }");
        using var db = OpenGraph();

        var result = ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Equal(2, result.FilesScanned);
        Assert.Equal(2, result.TypesFound);
        Assert.Single(db.SearchByName("Player"));
    }

    [Fact]
    public void SkipsLibraryTempAndBuildFolders()
    {
        // Unity's Library churns constantly and contains generated code; indexing it
        // would both waste time and pollute the graph.
        WriteScript("Assets/Real.cs", "public class Real { }");
        WriteScript("Library/PackageCache/Fake.cs", "public class Fake { }");
        WriteScript("Temp/Fake2.cs", "public class Fake2 { }");
        WriteScript("obj/Fake3.cs", "public class Fake3 { }");
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Single(db.SearchByName("Real"));
        Assert.Empty(db.SearchByName("Fake"));
    }

    [Fact]
    public void IndexesPackagesFolderAsWell()
    {
        WriteScript("Packages/com.example.thing/Runtime/Thing.cs", "public class Thing { }");
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Single(db.SearchByName("Thing"));
    }

    [Fact]
    public void StoresProjectRelativePathsWithForwardSlashes()
    {
        WriteScript("Assets/Scripts/Player.cs", "public class Player { }");
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Equal("Assets/Scripts/Player.cs", db.SearchByName("Player")[0].Path);
    }

    [Fact]
    public void ReindexingRemovesTypesDeletedFromAFile()
    {
        WriteScript("Assets/Scripts/Player.cs", "public class Player { }\npublic class Sidekick { }");
        using var db = OpenGraph();
        ScriptIndexer.IndexProject(_projectRoot, db);
        Assert.Single(db.SearchByName("Sidekick"));

        WriteScript("Assets/Scripts/Player.cs", "public class Player { }");
        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Empty(db.SearchByName("Sidekick"));
        Assert.Single(db.SearchByName("Player"));
    }

    [Fact]
    public void TypesFoundReflectsNodesActuallyRecorded_NotTypesParsed()
    {
        // A collision (two declarations sharing one node identity) correctly merges onto a
        // single node — TypesFound must never disagree with what the graph actually holds, or
        // a future collision could hide behind an inflated, never-verified count.
        WriteScript("Assets/Scripts/Dup.cs",
            "namespace A { public class Config { } }\nnamespace A { public class Config { } }");
        using var db = OpenGraph();

        var result = ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Equal(1, result.TypesFound);
        Assert.Equal(1, db.TotalNodes());
    }

    [Fact]
    public void ReindexingRemovesNodesForFilesDeletedFromDisk()
    {
        // Delete-then-insert only touches files actually encountered during a run, so a file
        // removed from disk was never revisited and its nodes lived forever. The whole promise
        // of the graph is that Search returns real locations, and deleting a file is the most
        // routine action a developer takes.
        WriteScript("Assets/Player.cs", "public class Player { }");
        WriteScript("Assets/Enemy.cs", "public class Enemy { }");
        using var db = OpenGraph();
        ScriptIndexer.IndexProject(_projectRoot, db);
        Assert.Equal(2, db.TotalNodes());

        File.Delete(Path.Combine(_projectRoot, "Assets", "Enemy.cs"));
        var result = ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(1, db.TotalNodes());
        Assert.Empty(db.SearchByName("Enemy"));
        Assert.Single(db.SearchByName("Player"));
    }

    [Fact]
    public void ReindexingRemovesNodesForFilesDeletedFromAnEmbeddedPackage()
    {
        // A package declared with a "file:" dependency pointing INSIDE the project (the key
        // matches a directory already under Packages/) is deliberately not its own scan root —
        // the generic "Packages" walk covers it directly, and is the ONLY thing that ever will.
        // Reserving its prefix from that walk's sweep (to protect packages that failed to
        // resolve or are ancestors) would mean nothing ever sweeps it — reintroducing the
        // round-2 defect for embedded packages specifically.
        WriteScript("Packages/com.example.inner/Runtime/Inner.cs", "public class Inner { }");
        WriteScript("Packages/com.example.inner/Runtime/InnerGone.cs", "public class InnerGone { }");
        WriteManifest("\"com.example.inner\":\"file:com.example.inner\"");
        using var db = OpenGraph();
        ScriptIndexer.IndexProject(_projectRoot, db);
        Assert.Single(db.SearchByName("InnerGone"));

        File.Delete(Path.Combine(_projectRoot, "Packages", "com.example.inner", "Runtime", "InnerGone.cs"));
        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Empty(db.SearchByName("InnerGone"));
        Assert.Single(db.SearchByName("Inner"));
    }

    [Fact]
    public void DoesNotSweepNodesFromAPackageThatFailedToResolveThisRun()
    {
        // A file: package that is temporarily unavailable (e.g. an unmounted drive) only warns
        // and continues — a global "delete anything not visited this run" sweep would then
        // read as "every one of its files was deleted". The sweep is scoped per-root, so a
        // root that never resolved (and so was never even attempted) is never swept.
        var external = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(external, "Editor"));
        File.WriteAllText(Path.Combine(external, "Editor", "Tool.cs"), "public class Tool { }");
        WriteScript("Assets/Local.cs", "public class Local { }");
        WriteManifest($"\"com.example.tools\":\"file:{external}\"");
        using var db = OpenGraph();
        ScriptIndexer.IndexProject(_projectRoot, db);
        Assert.Single(db.SearchByName("Tool"));

        Directory.Delete(external, recursive: true);   // simulate the package becoming unavailable
        var result = ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Contains(result.Warnings, w => w.Contains("com.example.tools"));
        Assert.Single(db.SearchByName("Tool"));    // not swept — its root never resolved this run
        Assert.Single(db.SearchByName("Local"));   // Assets/ still indexes, and still sweeps, normally
    }

    [Fact]
    public void ContinuesAfterAnUnreadableFile()
    {
        WriteScript("Assets/Good.cs", "public class Good { }");
        WriteScript("Assets/Bad.cs", "public class Bad {");
        using var db = OpenGraph();

        var result = ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Equal(2, result.FilesScanned);
        Assert.Single(db.SearchByName("Good"));
    }

    [Fact]
    public void ContinuesWhenADirectoryIsUnreadable()
    {
        // UnauthorizedAccessException derives from SystemException, not IOException.
        // DirectoryInfo.ResolveLinkTarget throws it for a directory it cannot access, so a
        // guard that only widened its own catch to IOException would still let it propagate
        // out of CanonicalIdentity and abort the whole scan — exactly what
        // EnumerateSourceFiles's own doc comment says must not happen: "an unreadable
        // subdirectory must skip rather than abort the whole scan."
        if (OperatingSystem.IsWindows())
        {
            return; // File.SetUnixFileMode is a Unix chmod equivalent; Windows ACLs differ.
        }

        if (Environment.IsPrivilegedProcess)
        {
            return; // Root bypasses permission bits entirely; the chmod below would not bite.
        }

        WriteScript("Assets/Good/Keep.cs", "public class Keep { }");
        var blocked = Path.Combine(_projectRoot, "Assets", "Blocked");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, "Hidden.cs"), "public class Hidden { }");
        using var db = OpenGraph();

        File.SetUnixFileMode(blocked, UnixFileMode.UserRead);   // r--: readable, not searchable

        try
        {
            ScriptIndexer.IndexProject(_projectRoot, db);

            Assert.Single(db.SearchByName("Keep"));
            Assert.Empty(db.SearchByName("Hidden"));
        }
        finally
        {
            // Restore permissions so Dispose() can delete the directory.
            File.SetUnixFileMode(blocked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // --- Local "file:" packages -------------------------------------------------------
    // These live OUTSIDE the project directory but are first-class project code. Measured
    // on the real Hades-Unity-Client project: Assets/ holds 16 .cs files, while the single
    // file:-referenced package holds 172. Scanning only Assets/ indexed 16 of 188 files.

    void WriteManifest(string dependenciesJson) =>
        WriteScript("Packages/manifest.json", $"{{\"dependencies\":{{{dependenciesJson}}}}}");

    [Fact]
    public void IndexesLocalPackagesDeclaredWithAFileDependency()
    {
        var external = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(external, "Editor"));
        File.WriteAllText(Path.Combine(external, "Editor", "Tool.cs"), "public class Tool { }");
        WriteScript("Assets/Local.cs", "public class Local { }");
        WriteManifest($"\"com.example.tools\":\"file:{external}\"");
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Single(db.SearchByName("Tool"));
        Assert.Single(db.SearchByName("Local"));
        Directory.Delete(external, recursive: true);
    }

    [Fact]
    public void RecordsLocalPackageFilesUnderTheUnityPackagesConvention()
    {
        var external = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(external, "Editor"));
        File.WriteAllText(Path.Combine(external, "Editor", "Tool.cs"), "public class Tool { }");
        WriteManifest($"\"com.example.tools\":\"file:{external}\"");
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        // Not "../../tmp/xyz/Editor/Tool.cs" — Unity's own display convention for local packages.
        Assert.Equal("Packages/com.example.tools/Editor/Tool.cs", db.SearchByName("Tool")[0].Path);
        Directory.Delete(external, recursive: true);
    }

    [Fact]
    public void WarnsWhenADeclaredLocalPackageIsMissing()
    {
        WriteScript("Assets/Local.cs", "public class Local { }");
        WriteManifest("\"com.example.gone\":\"file:/nonexistent/path/xyz\"");
        using var db = OpenGraph();

        var result = ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Contains(result.Warnings, w => w.Contains("com.example.gone"));
        Assert.Single(db.SearchByName("Local"));   // the rest of the project still indexes
    }

    [Fact]
    public void DoesNotDoubleScanALocalPackageThatLivesInsideTheProject()
    {
        WriteScript("Packages/com.example.inner/Runtime/Inner.cs", "public class Inner { }");
        WriteManifest("\"com.example.inner\":\"file:com.example.inner\"");
        using var db = OpenGraph();

        var result = ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Equal(1, result.FilesScanned);
        Assert.Single(db.SearchByName("Inner"));
    }

    [Fact]
    public void PrunesDirectoriesUnityItselfIgnores()
    {
        // Unity ignores directories starting with "." or ending with "~", and build output is
        // never project code. Without pruning, an external file: package root descends into
        // things Unity cannot see — on the real project that meant indexing Hades's own .NET
        // solution under Core/, including generated AssemblyInfo.cs and GlobalUsings.g.cs.
        var external = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        foreach (var sub in new[] { "Editor", "Tooling~", ".hidden", "obj/Debug", "bin", "node_modules/pkg" })
        {
            Directory.CreateDirectory(Path.Combine(external, sub));
            File.WriteAllText(Path.Combine(external, sub, "F.cs"),
                $"public class F{sub.Replace('/', '_').Replace('~', '_').Replace('.', '_')} {{ }}");
        }
        WriteManifest($"\"com.example.tools\":\"file:{external}\"");
        using var db = OpenGraph();

        var result = ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Equal(1, result.FilesScanned);              // Editor/F.cs only
        Assert.Single(db.SearchByName("FEditor"));
        Assert.Empty(db.SearchByName("FTooling"));
        Assert.Empty(db.SearchByName("F_hidden"));
        Assert.Empty(db.SearchByName("Fobj"));
        Assert.Empty(db.SearchByName("Fbin"));
        Assert.Empty(db.SearchByName("Fnode"));

        Directory.Delete(external, recursive: true);
    }

    [Fact]
    public void DoesNotDoubleIndexWhenALocalPackagePointsExactlyAtTheProjectRoot()
    {
        // The containment check was false when candidate == root (only true for a STRICT
        // child), so "file:.." — which resolves to exactly the project root, no trailing
        // separator — used to be treated as "outside" and rescanned as a whole extra root.
        WriteScript("Assets/Scripts/Player.cs", "public class Player { }");
        WriteManifest("\"com.example.self\":\"file:..\"");
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Single(db.SearchByName("Player"));
    }

    [Fact]
    public void SkipsALocalPackageThatIsAnAncestorOfTheProjectRoot()
    {
        // Scanning a package that CONTAINS the project root would both rescan the project
        // itself and pull in unrelated sibling directories. Uses its own container directory
        // (never the shared OS temp root) so it cannot sweep up other fixtures' files.
        var container = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var projectRoot = Path.Combine(container, "Project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: aaaabbbbccccddddeeeeffff00001111\n");
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets", "Scripts"));
        File.WriteAllText(Path.Combine(projectRoot, "Assets", "Scripts", "Player.cs"), "public class Player { }");
        Directory.CreateDirectory(Path.Combine(projectRoot, "Packages"));
        File.WriteAllText(Path.Combine(projectRoot, "Packages", "manifest.json"),
            "{\"dependencies\":{\"com.example.parent\":\"file:" + container + "\"}}");

        var siblingDir = Path.Combine(container, "Sibling");
        Directory.CreateDirectory(siblingDir);
        File.WriteAllText(Path.Combine(siblingDir, "Junk.cs"), "public class SiblingJunk { }");

        using var db = GraphDatabase.Open(Path.Combine(container, "graph.db"));

        var result = ScriptIndexer.IndexProject(projectRoot, db);

        Assert.Contains(result.Warnings, w => w.Contains("com.example.parent"));
        Assert.Single(db.SearchByName("Player"));
        Assert.Empty(db.SearchByName("SiblingJunk"));

        Directory.Delete(container, recursive: true);
    }

    [Fact]
    public async Task TerminatesWhenASymlinkedDirectoryCreatesACycle()
    {
        // A directory symlink can point back at an ancestor, and Directory.EnumerateDirectories
        // follows symlinks — without a visited-set the walk revisits the same subtree forever,
        // exponential in branching rather than depth. Bounded with an explicit timeout instead
        // of relying on OS-level ELOOP behaviour (which is platform-dependent, and worse on
        // Linux) so this fails fast rather than hanging the suite if the guard regresses.
        WriteScript("Assets/Real.cs", "public class Real { }");
        var deep = Path.Combine(_projectRoot, "Assets", "Deep");
        Directory.CreateDirectory(deep);
        Directory.CreateSymbolicLink(Path.Combine(deep, "loop"), Path.Combine(_projectRoot, "Assets"));
        using var db = OpenGraph();

        var indexing = Task.Run(() => ScriptIndexer.IndexProject(_projectRoot, db));
        var completed = await Task.WhenAny(indexing, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(ReferenceEquals(completed, indexing), "Indexing did not terminate — symlink cycle is unguarded");

        var result = await indexing;
        Assert.Equal(1, result.FilesScanned);
        Assert.Single(db.SearchByName("Real"));
    }

    [Fact]
    public void IgnoresRegistryPackagesWhichHaveNoFilePrefix()
    {
        WriteScript("Assets/Local.cs", "public class Local { }");
        WriteManifest("\"com.unity.ugui\":\"2.0.0\",\"com.unity.timeline\":\"1.8.9\"");
        using var db = OpenGraph();

        var result = ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Empty(result.Warnings);
        Assert.Equal(1, result.FilesScanned);
    }

    // --- Plan 15 Task 3: conditional compilation, end to end -------------------------------
    // RoslynScriptScannerTests proves the scanner mechanism respects #if/#else correctly given
    // a define set; these prove ScriptIndexer actually RESOLVES and PASSES one, against real
    // ProjectVersion.txt / ProjectSettings.asset files on disk — the wiring the unit-level tests
    // cannot see.

    void WriteProjectVersion(string editorVersion) =>
        WriteScript("ProjectSettings/ProjectVersion.txt",
            $"m_EditorVersion: {editorVersion}\nm_EditorVersionWithRevision: {editorVersion} (a9779f353c9b)\n");

    [Fact]
    public void IndexesCodeGuardedByUnityEditorEvenWithNoProjectVersionFile()
    {
        // UNITY_EDITOR is unconditional (ProjectDefines.UnityEditorSymbol) — must apply even when
        // ProjectVersion.txt does not exist, the shape most of this test class's own fixtures use.
        WriteScript("Assets/Scripts/EditorDrawer.cs", """
            #if UNITY_EDITOR
            public class EditorOnlyDrawer { }
            #endif
            """);
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Single(db.SearchByName("EditorOnlyDrawer"));
    }

    [Fact]
    public void StillExcludesCodeGuardedByASymbolNotInTheAppliedSet()
    {
        // The regression this whole task must not introduce: proving false #if branches are
        // still excluded end to end, not just in the scanner unit tests — the shortcut Plan 15
        // Task 3 rejects (stripping #if/#else entirely) would make this test fail.
        WriteScript("Assets/Scripts/AndroidOnly.cs", """
            #if UNITY_ANDROID
            public class NeverInThisIndex { }
            #endif
            """);
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Empty(db.SearchByName("NeverInThisIndex"));
    }

    [Fact]
    public void IndexesCodeGuardedByTheVersionLadderFromProjectVersionTxt()
    {
        WriteProjectVersion("6000.3.2f1");
        WriteScript("Assets/Scripts/NewApi.cs", """
            #if UNITY_6000_3_OR_NEWER
            public class UsesNewApi { }
            #endif
            """);
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Single(db.SearchByName("UsesNewApi"));
    }

    [Fact]
    public void IndexesCodeGuardedByAUserScriptingDefineSymbol()
    {
        WriteScript("ProjectSettings/ProjectSettings.asset", """
              productGUID: aaaabbbbccccddddeeeeffff00001111
              scriptingDefineSymbols:
                Standalone: MY_CUSTOM_DEFINE
              additionalCompilerArguments: {}
            """);
        WriteScript("Assets/Scripts/CustomGated.cs", """
            #if MY_CUSTOM_DEFINE
            public class GatedByCustomDefine { }
            #endif
            """);
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Single(db.SearchByName("GatedByCustomDefine"));
    }

    // --- Plan 15 Task 4: versionDefines, end to end -----------------------------------------
    // The insidious half of Plan 15's conditional-compilation defect: code gated on a genuinely
    // INSTALLED package version (an asmdef's own "versionDefines", resolved against Packages/
    // manifest.json and packages-lock.json), not on UNITY_EDITOR or a user scriptingDefineSymbol.
    // Real repro: project_aurora's Packages/com.arongranberg.astar/AstarPathfindingProject.asmdef
    // carries {"name":"com.unity.entities","expression":"1.0.0-pre.47","define":"MODULE_ENTITIES"},
    // and that project's Packages/manifest.json declares "com.unity.entities":"1.4.2" - so
    // MODULE_ENTITIES is genuinely defined in real compiles, and Core/ECS/Components/
    // AutoRepathPolicy.cs (entirely "#if MODULE_ENTITIES") was 100% invisible before this.

    void WriteAsmdef(string relativePath, string versionDefinesEntryJson) =>
        WriteScript(relativePath, "{\"name\":\"Test\",\"versionDefines\":[" + versionDefinesEntryJson + "]}");

    [Fact]
    public void IndexesCodeGuardedByAVersionDefineWhenTheManifestSatisfiesIt()
    {
        WriteAsmdef("Packages/com.example.astar/Test.asmdef",
            """{"name":"com.example.entities","expression":"1.0.0-pre.1","define":"MODULE_ENTITIES"}""");
        WriteManifest("\"com.example.entities\":\"1.4.2\"");
        WriteScript("Packages/com.example.astar/AutoRepathPolicy.cs", """
            #if MODULE_ENTITIES
            public class AutoRepathPolicy { }
            #endif
            """);
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Single(db.SearchByName("AutoRepathPolicy"));
    }

    [Fact]
    public void StillExcludesCodeGuardedByAVersionDefineWhenTheInstalledVersionIsTooLow()
    {
        WriteAsmdef("Packages/com.example.astar/Test.asmdef",
            """{"name":"com.example.entities","expression":"2.0.0","define":"MODULE_ENTITIES"}""");
        WriteManifest("\"com.example.entities\":\"1.4.2\"");
        WriteScript("Packages/com.example.astar/AutoRepathPolicy.cs", """
            #if MODULE_ENTITIES
            public class NeverInThisIndex { }
            #endif
            """);
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Empty(db.SearchByName("NeverInThisIndex"));
    }

    [Fact]
    public void TakesExactlyOneBranchWhenBaseTypeIsGatedByAVersionDefine()
    {
        // Mirrors the real shape of astar's own VersionedMonoBehaviour.cs: "#if MODULE_ENTITIES
        // ... #else ... #endif" choosing between two base types. The rejected shortcut (stripping
        // #if/#else so both branches land) would make both classes appear; Roslyn's own
        // preprocessor guarantees exactly one does when given a concrete, resolved define set.
        WriteAsmdef("Packages/com.example.astar/Test.asmdef",
            """{"name":"com.example.entities","expression":"1.0.0","define":"MODULE_ENTITIES"}""");
        WriteManifest("\"com.example.entities\":\"1.4.2\"");
        WriteScript("Packages/com.example.astar/Versioned.cs", """
            #if MODULE_ENTITIES
            public class UsesEntities { }
            #else
            public class FallsBackWithoutEntities { }
            #endif
            """);
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Single(db.SearchByName("UsesEntities"));
        Assert.Empty(db.SearchByName("FallsBackWithoutEntities"));
    }

    void WriteLock(string dependencyEntriesJson) =>
        WriteScript("Packages/packages-lock.json", "{\"dependencies\":{" + dependencyEntriesJson + "}}");

    [Fact]
    public void ResolvesAVersionDefineForATransitiveDependencyOnlyVisibleInPackagesLockJson()
    {
        // Real project_aurora shape: com.unity.burst/mathematics/collections are pulled in
        // TRANSITIVELY by com.unity.entities and never appear as a direct Packages/manifest.json
        // dependency at all - only packages-lock.json (Unity's own record of what actually got
        // resolved) lists them, at "depth":1. See ProjectDefines' own class doc comment for why
        // this project deliberately reads packages-lock.json too, not manifest.json alone.
        WriteAsmdef("Packages/com.example.astar/Test.asmdef",
            """{"name":"com.example.burst","expression":"1.8.7","define":"MODULE_BURST"}""");
        WriteLock("\"com.example.burst\":{\"version\":\"1.8.26\",\"depth\":1,\"source\":\"registry\"}");
        WriteScript("Packages/com.example.astar/BurstOnly.cs", """
            #if MODULE_BURST
            public class UsesBurst { }
            #endif
            """);
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);

        Assert.Single(db.SearchByName("UsesBurst"));
    }

    // I10: a directory a full rebuild's own walk cannot read is not evidence anything under it
    // was deleted — SweepStaleNodes (called once per root, right after this walk) must not treat
    // "never visited because unreadable" the same as "confirmed gone", or a permissions hiccup
    // silently wipes an entire subtree's nodes.
#pragma warning disable CA1416 // POSIX-only test, see IncrementalIndexTests' identical suppression
    [Fact]
    public void AnUnreadableDirectory_PreservesItsNodes_OnFullReindex()
    {
        WriteScript("Assets/Locked/Hidden.cs", "public class Hidden { }");
        using var db = OpenGraph();
        ScriptIndexer.IndexProject(_projectRoot, db);
        Assert.Single(db.SearchByName("Hidden"));

        var lockedDir = Path.Combine(_projectRoot, "Assets", "Locked");
        File.SetUnixFileMode(lockedDir, UnixFileMode.None);
        try
        {
            var result = ScriptIndexer.IndexProject(_projectRoot, db);

            Assert.Single(db.SearchByName("Hidden"));
            Assert.Contains(result.Warnings, w => w.Contains("Assets/Locked"));
        }
        finally
        {
            File.SetUnixFileMode(lockedDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
#pragma warning restore CA1416

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }
}
