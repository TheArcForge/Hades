using System.Collections.Concurrent;
using System.Reflection;
using Hades.Core;
using Hades.Core.Projects;
using Hades.Core.Storage;

namespace Hades.Core.Tests;

public class ProjectServiceTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    ProjectService NewService() => new(new AppPaths(_appRoot));

    void MakeUnityProject()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: aaaabbbbccccddddeeeeffff00001111\n");

        var scripts = Path.Combine(_projectRoot, "Assets", "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "PlayerController.cs"),
            "using UnityEngine;\npublic class PlayerController : MonoBehaviour { }");
    }

    [Fact]
    public void AdoptAndIndex_MakesTheProjectQueryable()
    {
        MakeUnityProject();
        var service = NewService();

        var project = service.AdoptAndIndex(_projectRoot);

        Assert.NotNull(project);
        var results = service.Search(project!.ProductGuid, "player");
        Assert.Single(results);
        Assert.Equal("PlayerController", results[0].Name);
    }

    [Fact]
    public void AdoptAndIndex_ReturnsNullForNonUnityDirectory()
    {
        Directory.CreateDirectory(_projectRoot);

        Assert.Null(NewService().AdoptAndIndex(_projectRoot));
    }

    [Fact]
    public void Adopt_WhenTheArcforgeMemoryImportFails_StillAdoptsAndAnnounces_AndALaterAdoptRetriesTheImport()
    {
        // Unix permissions are the failure mechanism under test; there is no Windows equivalent
        // of an unreadable-but-present mode-000 directory.
        if (OperatingSystem.IsWindows()) return;

        MakeUnityProject();
        var memorySource = Path.Combine(_projectRoot, ".arcforge", "memory");
        Directory.CreateDirectory(memorySource);
        File.WriteAllText(Path.Combine(memorySource, "conventions.md"), "# Conventions\n");
        File.SetUnixFileMode(memorySource, UnixFileMode.None);

        try
        {
            var service = NewService();
            var announced = false;
            service.ProjectAdopted += _ => announced = true;

            var adopted = service.Adopt(_projectRoot);

            // The project genuinely registered (project.json written) before the import ran, so
            // Adopt must report it and announce it - a throw here would leave a half-adopted
            // project no observer ever hears about.
            Assert.NotNull(adopted);
            Assert.True(announced);

            // And the failed attempt must not consume the once-per-process import: with the
            // directory readable again, the next Adopt retries and the authored document arrives.
            File.SetUnixFileMode(memorySource,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            service.Adopt(_projectRoot);

            var summary = service.GetMemorySummary(adopted!.ProductGuid);
            Assert.NotNull(summary);
            Assert.Contains(summary!.Documents, d => d.Name == "conventions.md");
        }
        finally
        {
            if (Directory.Exists(memorySource))
                File.SetUnixFileMode(memorySource,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void GraphLivesInAppStorage_NotInTheProject()
    {
        MakeUnityProject();

        var project = NewService().AdoptAndIndex(_projectRoot)!;

        Assert.True(File.Exists(new AppPaths(_appRoot).GraphDb(project.ProductGuid)));
        Assert.False(Directory.Exists(Path.Combine(_projectRoot, ".hades")));
        Assert.False(Directory.Exists(Path.Combine(_projectRoot, ".arcforge")));
    }

    [Fact]
    public void Summary_ReportsCountsAndIndexState()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        var summary = service.Summary(project.ProductGuid);

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.TotalNodes);
        Assert.Equal(1, summary.NodesByKind["Class"]);
        Assert.NotNull(summary.LastIndexedUtc);
        Assert.Contains("UNITY_EDITOR", summary.AppliedDefines);
    }

    [Fact]
    public void Summary_AppliedDefinesReflectsProjectVersionAndScriptingDefineSymbols()
    {
        // Plan 15 Task 3 Step 4: get_project_summary must STATE which symbols were applied, so
        // code guarded by anything else is a stated limitation rather than a silent hole.
        MakeUnityProject();
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 6000.3.2f1\nm_EditorVersionWithRevision: 6000.3.2f1 (a9779f353c9b)\n");
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: aaaabbbbccccddddeeeeffff00001111\n  scriptingDefineSymbols:\n    Standalone: MY_CUSTOM_DEFINE\n");
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        var summary = service.Summary(project.ProductGuid)!;

        Assert.Contains("UNITY_EDITOR", summary.AppliedDefines);
        Assert.Contains("UNITY_6000_3_OR_NEWER", summary.AppliedDefines);
        Assert.Contains("MY_CUSTOM_DEFINE", summary.AppliedDefines);
    }

    [Fact]
    public void Search_ReturnsEmptyForUnknownProject()
    {
        Assert.Empty(NewService().Search("ffffffffffffffffffffffffffffffff", "anything"));
    }

    // ---------------------------------------------------------------- RecordEditorAttached (F9)
    //
    // project.json kept UnityVersion: null and LastSeen == FirstSeen forever after the initial
    // Adopt, even once a real Editor had attached and reported both - project.json is otherwise
    // only ever written at Adopt time and never revisited. EditorListener.Register calls this once
    // a Hello completes (see that method's own doc comment) - this proves the persistence itself;
    // EditorListenerTests proves the attach-time wiring end to end over a real socket.

    [Fact]
    public void RecordEditorAttached_PersistsTheReportedUnityVersionAndBumpsLastSeen()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;
        Assert.Null(project.UnityVersion);
        var lastSeenAtAdopt = project.LastSeen;

        Thread.Sleep(20); // guarantee a measurable clock difference for LastSeen

        service.RecordEditorAttached(project.ProductGuid, "6000.3.2f1");

        var updated = service.Get(project.ProductGuid);
        Assert.NotNull(updated);
        Assert.Equal("6000.3.2f1", updated!.UnityVersion);
        Assert.Equal(project.FirstSeen, updated.FirstSeen); // FirstSeen never moves
        Assert.True(updated.LastSeen > lastSeenAtAdopt, "LastSeen was never bumped on attach");
    }

    [Fact]
    public void RecordEditorAttached_BlankUnityVersion_StillBumpsLastSeen_ButNeverClobbersAnAlreadyKnownVersion()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;
        service.RecordEditorAttached(project.ProductGuid, "6000.3.2f1");
        var afterFirstAttach = service.Get(project.ProductGuid)!.LastSeen;

        Thread.Sleep(20);

        service.RecordEditorAttached(project.ProductGuid, "");

        var updated = service.Get(project.ProductGuid);
        Assert.Equal("6000.3.2f1", updated!.UnityVersion);
        Assert.True(updated.LastSeen > afterFirstAttach, "LastSeen was never bumped on the second attach");
    }

    [Fact]
    public void RecordEditorAttached_UnknownProject_IsANoOpAndNeverThrows()
    {
        var service = NewService();

        var exception = Record.Exception(
            () => service.RecordEditorAttached("ffffffffffffffffffffffffffffffff", "6000.3.2f1"));

        Assert.Null(exception);
    }

    // ---------------------------------------------------------------- ExistsOnDisk (F6-honesty)
    //
    // Backs find_references_to's "exists on disk but is an asset type Hades does not index"
    // branch (HadesTools.FindReferencesTo) — the honest distinction from a genuinely absent path,
    // for an asset the graph never resolved (textures, models, audio, fonts, shaders, animation
    // clips: never indexed as nodes, by design).

    [Fact]
    public void ExistsOnDisk_TrueForAFileOnDisk()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;
        var texturePath = Path.Combine(_projectRoot, "Assets", "Wood.png");
        File.WriteAllBytes(texturePath, [0]);

        Assert.True(service.ExistsOnDisk(project.ProductGuid, "Assets/Wood.png"));
    }

    [Fact]
    public void ExistsOnDisk_TrueWhenOnlyTheMetaFileExists()
    {
        // The literal case F6-honesty names: "the path (or its .meta) exists on disk" — a folder
        // asset (or an asset whose content was deleted but Unity has not re-imported yet) has a
        // real .meta with no sibling content file for File.Exists to find directly.
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;
        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "Wood.png.meta"),
            "fileFormatVersion: 2\nguid: aabbccddeeff00112233445566778899\n");

        Assert.True(service.ExistsOnDisk(project.ProductGuid, "Assets/Wood.png"));
    }

    [Fact]
    public void ExistsOnDisk_FalseForAPathThatTrulyDoesNotExist()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        Assert.False(service.ExistsOnDisk(project.ProductGuid, "Assets/DoesNotExist.png"));
    }

    [Fact]
    public void ExistsOnDisk_FalseForAnUnknownProject()
    {
        Assert.False(NewService().ExistsOnDisk("ffffffffffffffffffffffffffffffff", "Assets/Wood.png"));
    }

    [Fact]
    public void ExistsOnDisk_FalseRatherThanThrowingForAPathThatEscapesTheProject()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        Assert.False(service.ExistsOnDisk(project.ProductGuid, "../../../../etc/passwd"));
    }

    readonly List<string> _adoptedProjectRoots = [];

    [Fact]
    public async Task EnsureIndexed_IsSafeUnderHighConcurrencyAcrossManyProjects()
    {
        // Dictionary<TKey,TValue> (the type _lastIndexed used before this fix) is not safe for
        // concurrent writers, even across different keys — internal bucket/resize state races.
        // Reindex() writes _lastIndexed on every call, and nothing serialises EnsureIndexed for
        // DIFFERENT projects, which is exactly what a server fielding concurrent requests across
        // multiple roots would do. Measured pre-fix: InvalidOperationException and/or silently
        // lost writes under enough concurrent projects and threads.
        var service = NewService();
        const int projectCount = 200;
        var guids = new string[projectCount];

        for (var i = 0; i < projectCount; i++)
        {
            var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            var guid = $"{i:x8}bbbbccccddddeeeeffff0000";
            File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectSettings.asset"),
                $"  productGUID: {guid}\n");
            guids[i] = guid;
            _adoptedProjectRoots.Add(root);
            Assert.NotNull(service.Adopt(root));
        }

        await Parallel.ForAsync(0, projectCount * 4, new ParallelOptions { MaxDegreeOfParallelism = 32 },
            (i, _) => { service.EnsureIndexed(guids[i % projectCount]); return ValueTask.CompletedTask; });

        foreach (var guid in guids)
            Assert.NotNull(service.Summary(guid)?.LastIndexedUtc);
    }

    // ---------------------------------------------------------------- RebuildGraph gating (F18-class)
    //
    // RebuildGraph used to call Reindex directly, bypassing the SAME per-project _indexGates
    // semaphore EnsureIndexed uses — see that field's own doc comment: "nothing otherwise
    // serialises EnsureIndexed for different projects" was equally true of RebuildGraph against
    // its OWN project. A concurrent rebuild could therefore run its own Reindex at the same time
    // as a routine EnsureIndexed (or the ObservationService sweep) reindexing the SAME project,
    // through two independent database connections with no app-level exclusion between them.
    //
    // Proven deterministically rather than by racing real indexing (inherently flaky, and a false
    // negative would prove nothing): grab the SAME private per-project SemaphoreSlim EnsureIndexed
    // itself acquires via _indexGates — there is no other observable seam for "did this acquire
    // THIS lock", and the real synchronization primitive is what's under test, not a stand-in for
    // it — hold it exactly as EnsureIndexed would while mid-Reindex, and confirm RebuildGraph
    // blocks on it rather than running straight through.

    static SemaphoreSlim AcquireIndexGateForTest(ProjectService service, string productGuid)
    {
        var gatesField = typeof(ProjectService).GetField("_indexGates", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ProjectService._indexGates not found — has it been renamed?");

        var gates = (ConcurrentDictionary<string, SemaphoreSlim>)gatesField.GetValue(service)!;
        return gates.GetOrAdd(productGuid, static _ => new SemaphoreSlim(1, 1));
    }

    [Fact]
    public async Task RebuildGraph_BlocksWhileEnsureIndexedsGateIsHeld_ForTheSameProject()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        var gate = AcquireIndexGateForTest(service, project.ProductGuid);
        await gate.WaitAsync(); // simulates EnsureIndexed already mid-Reindex for this project

        var rebuildTask = Task.Run(() => service.RebuildGraph(project.ProductGuid));

        var wonRace = await Task.WhenAny(rebuildTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(rebuildTask, wonRace); // RebuildGraph must NOT have run while the gate was held

        gate.Release();

        var result = await rebuildTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task RebuildGraph_ReleasesItsGate_SoASubsequentEnsureIndexedCallIsNotBlockedForever()
    {
        // The other half of the same proof: acquiring the gate must not leak it. If RebuildGraph
        // acquired but never released, every future EnsureIndexed for this project would hang.
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        var result = service.RebuildGraph(project.ProductGuid);
        Assert.NotNull(result);

        var gate = AcquireIndexGateForTest(service, project.ProductGuid);
        var acquired = await gate.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(acquired, "the index gate was still held after RebuildGraph returned — it leaked the lock");
        gate.Release();
    }

    // ---------------------------------------------------------------- I2: SyncChanges gating
    //
    // SyncChanges used to acquire no gate at all — neither the per-project _indexGates
    // RebuildGraph/EnsureIndexed share, nor anything else — so a rebuild and an incremental sync
    // (what the ObservationService periodic sweep and live watcher both drive) could run
    // concurrently against the same project through two independent database connections, racing
    // to interleave writes and permanently losing updates. Same deterministic proof technique as
    // RebuildGraph's own gating tests above: grab the real per-project SemaphoreSlim directly.

    [Fact]
    public async Task SyncChanges_BlocksWhileRebuildGraphsGateIsHeld_ForTheSameProject()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        var gate = AcquireIndexGateForTest(service, project.ProductGuid);
        await gate.WaitAsync(); // simulates RebuildGraph (or EnsureIndexed) already mid-Reindex

        var syncTask = Task.Run(() => service.SyncChanges(project.ProductGuid));

        var wonRace = await Task.WhenAny(syncTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(syncTask, wonRace); // SyncChanges must NOT have run while the gate was held

        gate.Release();

        var result = await syncTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task SyncChanges_ReleasesItsGate_SoASubsequentRebuildIsNotBlockedForever()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        var result = service.SyncChanges(project.ProductGuid);
        Assert.NotNull(result);

        var gate = AcquireIndexGateForTest(service, project.ProductGuid);
        var acquired = await gate.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(acquired, "the index gate was still held after SyncChanges returned — it leaked the lock");
        gate.Release();
    }

    // ---------------------------------------------------------------- I3: valid -> unparseable
    //
    // A file that WAS a valid, indexed asset and has since become unparseable must lose its old
    // nodes exactly as a deleted file would — proven end to end here through BOTH the incremental
    // (SyncChanges) and full (RebuildGraph) entry points, mirroring AssetIndexerTests' own
    // lower-level proof of the same fix in IndexAsset itself.

    const string PoisonPrefabHeader = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

    void WritePrefab(string relativePath, string body, string guid)
    {
        var full = Path.Combine(_projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, PoisonPrefabHeader + body);
        File.WriteAllText(full + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    [Fact]
    public void CorruptingAValidPrefab_RemovesItsNodes_OnSync()
    {
        MakeUnityProject();
        WritePrefab("Assets/Player.prefab", "--- !u!1 &111\nGameObject:\n  m_Name: Player\n",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;
        // kind-filtered: MakeUnityProject's own fixture script is named "PlayerController",
        // which substring-matches "Player" too and would otherwise make this setup assertion
        // (and the post-corruption one below) ambiguous about which node actually disappeared.
        Assert.Single(service.Search(project.ProductGuid, "Player", kind: "GameObject"));

        File.WriteAllText(Path.Combine(_projectRoot, "Assets/Player.prefab"),
            PoisonPrefabHeader + "--- !u!4294967296 &111\nGameObject:\n  m_Name: Player\n");
        var sweep = service.SyncChanges(project.ProductGuid);

        Assert.NotNull(sweep);
        Assert.Empty(service.Search(project.ProductGuid, "Player", kind: "GameObject"));
    }

    [Fact]
    public void CorruptingAValidPrefab_RemovesItsNodes_OnRebuild()
    {
        MakeUnityProject();
        WritePrefab("Assets/Player.prefab", "--- !u!1 &111\nGameObject:\n  m_Name: Player\n",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;
        Assert.Single(service.Search(project.ProductGuid, "Player", kind: "GameObject"));

        File.WriteAllText(Path.Combine(_projectRoot, "Assets/Player.prefab"),
            PoisonPrefabHeader + "--- !u!4294967296 &111\nGameObject:\n  m_Name: Player\n");
        var result = service.RebuildGraph(project.ProductGuid);

        Assert.NotNull(result);
        Assert.Empty(service.Search(project.ProductGuid, "Player", kind: "GameObject"));
    }

    // ---------------------------------------------------------------- I8: file_state ghosts
    //
    // A full rebuild's SweepStaleNodes call (inside ScriptIndexer/AssetIndexer.IndexProject)
    // always removed a deleted file's graph nodes, but never its file_state row — so the file
    // kept a phantom "last indexed" record forever. RecentlyChanged reads file_state directly
    // (see its own doc comment), so it is the existing public surface that can see a ghost.

    [Fact]
    public void RebuildGraph_RemovesFileStateForFilesDeletedSinceLastIndex()
    {
        MakeUnityProject(); // Assets/Scripts/PlayerController.cs
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;
        Assert.Contains(service.RecentlyChanged(project.ProductGuid), f => f.Path == "Assets/Scripts/PlayerController.cs");

        File.Delete(Path.Combine(_projectRoot, "Assets", "Scripts", "PlayerController.cs"));
        var result = service.RebuildGraph(project.ProductGuid);

        Assert.NotNull(result);
        Assert.DoesNotContain(service.RecentlyChanged(project.ProductGuid), f => f.Path == "Assets/Scripts/PlayerController.cs");
    }

    // ---------------------------------------------------------------- I5: warnings surfaced
    //
    // ScriptIndexer/AssetIndexer already build a per-file IndexResult.Warnings list — Reindex and
    // SyncChanges both discarded it entirely, so a poison file's own I1 diagnostic never reached
    // any caller. Now carried on the existing RebuildResult / SweepResult surfaces.

    [Fact]
    public void RebuildGraph_SurfacesPerFileWarningsFromIndexing()
    {
        MakeUnityProject();
        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "Poison.prefab"),
            PoisonPrefabHeader + "--- !u!4294967296 &1\nGameObject:\n  m_Name: Poison\n");
        var service = NewService();
        var project = service.Adopt(_projectRoot)!;

        var result = service.RebuildGraph(project.ProductGuid);

        Assert.NotNull(result);
        Assert.Contains(result!.Warnings, w => w.Contains("Assets/Poison.prefab"));
    }

    [Fact]
    public void SyncChanges_SurfacesPerFileWarningsFromIndexing()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "Poison.prefab"),
            PoisonPrefabHeader + "--- !u!4294967296 &1\nGameObject:\n  m_Name: Poison\n");
        var sweep = service.SyncChanges(project.ProductGuid);

        Assert.NotNull(sweep);
        Assert.Contains(sweep!.Warnings, w => w.Contains("Assets/Poison.prefab"));
    }

    // ---------------------------------------------------------------- F22: a move surviving a rebuild
    //
    // External-tester finding: create a prefab that a scene genuinely references, run
    // hades_rebuild_graph (a full Reindex), then move the prefab on disk (same GUID — Unity's own
    // contract for a rename/move) and let the incremental path (SyncChanges) pick it up. Two
    // symptoms were reported against find_references_to: (A) the OLD path keeps answering after
    // the move, even though nothing on disk owns it any more, and (B) — in a control run that
    // never called RebuildGraph at all — the incremental path was seen to drop the moved asset's
    // inbound reference entirely (a confident zero, the same class of defect F6/F14 were about).
    // Both are proven or disproven here against a REAL referencing edge (a PrefabInstance's own
    // instance_of link — see PrefabInstanceIndexingTests' identical WriteSceneWithInstance idiom),
    // never a bare node count, because a node count alone cannot distinguish "the reference
    // survived the move" from "the reference never existed".

    const string MovablePrefabGuid = "11112222333344445555666677778888";
    const string ReferencingSceneGuid = "99998888777766665555444433332222";

    /// <summary>A prefab at Assets/Before.prefab plus a scene that genuinely references it (one
    /// PrefabInstance -&gt; instance_of edge) — the fixture every test below moves and re-syncs.
    /// Takes an explicit <paramref name="projectRoot"/> rather than closing over the instance
    /// field so <see cref="IncrementalAndRebuildBasedMoves_AgreeOnTheSurvivingReferenceCount"/>
    /// can build two independent copies side by side.</summary>
    static void WriteMovableReferencedPrefab(string projectRoot)
    {
        void WriteAsset(string relativePath, string body, string guid)
        {
            var full = Path.Combine(projectRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, PoisonPrefabHeader + body);
            File.WriteAllText(full + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
        }

        WriteAsset("Assets/Before.prefab", "--- !u!1 &111\nGameObject:\n  m_Name: Piece\n", MovablePrefabGuid);
        WriteAsset("Assets/Main.unity", $$"""
            --- !u!1001 &100
            PrefabInstance:
              serializedVersion: 2
              m_Modification:
                m_TransformParent: {fileID: 0}
                m_Modifications: []
                m_RemovedComponents: []
              m_SourcePrefab: {fileID: 100100000, guid: {{MovablePrefabGuid}}, type: 3}
            """, ReferencingSceneGuid);
    }

    /// <summary>Renames both halves of an asset — content and .meta — exactly as Unity's own
    /// move/rename does: the GUID inside the .meta is untouched, only the file names change.</summary>
    static void MoveAsset(string projectRoot, string fromRelativePath, string toRelativePath)
    {
        File.Move(Path.Combine(projectRoot, fromRelativePath), Path.Combine(projectRoot, toRelativePath));
        File.Move(Path.Combine(projectRoot, fromRelativePath + ".meta"), Path.Combine(projectRoot, toRelativePath + ".meta"));
    }

    [Theory]
    [InlineData(true)]  // the tester's headline repro: hades_rebuild_graph runs between creation and the move
    [InlineData(false)] // the tester's own control: no intervening rebuild, incremental only throughout
    public void MovingAReferencedPrefab_RetiresTheOldPathAndPreservesItsInboundReference(bool intermediateRebuild)
    {
        MakeUnityProject();
        WriteMovableReferencedPrefab(_projectRoot);
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        var before = service.FindReferencesTo(project.ProductGuid, "Assets/Before.prefab");
        Assert.NotNull(before);
        Assert.True(before!.TotalReferences > 0,
            "fixture sanity: the scene must genuinely reference the prefab before anything moves");

        if (intermediateRebuild) Assert.NotNull(service.RebuildGraph(project.ProductGuid));

        MoveAsset(_projectRoot, "Assets/Before.prefab", "Assets/After.prefab");
        Assert.NotNull(service.SyncChanges(project.ProductGuid));

        // (A) nothing on disk owns "Before.prefab" any more — it must stop resolving.
        Assert.Null(service.FindReferencesTo(project.ProductGuid, "Assets/Before.prefab"));

        // (B) the reference the scene held must survive onto the NEW path, unchanged in count.
        var after = service.FindReferencesTo(project.ProductGuid, "Assets/After.prefab");
        Assert.NotNull(after);
        Assert.Equal(before.TotalReferences, after!.TotalReferences);
        Assert.Equal("Assets/Main.unity", Assert.Single(after.Files).Path);
    }

    [Fact]
    public void MovingAReferencedPrefab_PureIncrementalHistory_NeverRebuilt_PreservesTheInboundReference()
    {
        // The most literal reading of the tester's "WITHOUT the rebuild" control case: Reindex —
        // whether via AdoptAndIndex or RebuildGraph — never runs even ONCE. Every bit of graph
        // state, from the prefab and scene's own creation through the move, arrives only via
        // SyncChanges, exactly as a project that only ever has a live watcher/periodic sweep would
        // build it. (B) — "the incremental path drops the referencing edge on a move, reporting a
        // confident zero" — does not depend on any prior Reindex to test, so this is the strictest
        // version of that claim available in this harness.
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: aaaabbbbccccddddeeeeffff00001111\n");
        var service = NewService();
        var project = service.Adopt(_projectRoot)!; // registers only — Reindex never runs

        WriteMovableReferencedPrefab(_projectRoot);
        Assert.NotNull(service.SyncChanges(project.ProductGuid)); // first-ever index, incremental

        var before = service.FindReferencesTo(project.ProductGuid, "Assets/Before.prefab");
        Assert.NotNull(before);
        Assert.True(before!.TotalReferences > 0,
            "fixture sanity: the scene must genuinely reference the prefab before anything moves");

        MoveAsset(_projectRoot, "Assets/Before.prefab", "Assets/After.prefab");
        Assert.NotNull(service.SyncChanges(project.ProductGuid));

        Assert.Null(service.FindReferencesTo(project.ProductGuid, "Assets/Before.prefab"));

        var after = service.FindReferencesTo(project.ProductGuid, "Assets/After.prefab");
        Assert.NotNull(after);
        Assert.Equal(before.TotalReferences, after!.TotalReferences);
    }

    [Fact]
    public void IncrementalAndRebuildBasedMoves_AgreeOnTheSurvivingReferenceCount()
    {
        // The tester's own framing of the bug: "the reference counts differ between the two
        // routes (0 vs 2)" is itself the defect, independent of which absolute number is
        // "correct". Two fully independent copies of the same fixture — one routed through an
        // intermediate RebuildGraph, one not — must still agree once both have synced past the
        // move.
        static int? RunRoute(bool intermediateRebuild)
        {
            var appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(Path.Combine(projectRoot, "ProjectSettings"));
                File.WriteAllText(Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset"),
                    "  productGUID: aaaabbbbccccddddeeeeffff00001111\n");
                WriteMovableReferencedPrefab(projectRoot);

                var service = new ProjectService(new AppPaths(appRoot));
                var project = service.AdoptAndIndex(projectRoot)!;

                if (intermediateRebuild) service.RebuildGraph(project.ProductGuid);

                MoveAsset(projectRoot, "Assets/Before.prefab", "Assets/After.prefab");
                service.SyncChanges(project.ProductGuid);

                return service.FindReferencesTo(project.ProductGuid, "Assets/After.prefab")?.TotalReferences;
            }
            finally
            {
                if (Directory.Exists(appRoot)) Directory.Delete(appRoot, recursive: true);
                if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, recursive: true);
            }
        }

        var withRebuild = RunRoute(intermediateRebuild: true);
        var withoutRebuild = RunRoute(intermediateRebuild: false);

        Assert.NotNull(withRebuild);
        Assert.NotNull(withoutRebuild);
        Assert.Equal(withoutRebuild, withRebuild);
    }
    /// <summary>
    /// <summary>
    /// A sync that finds NO CHANGES still records the index time.
    ///
    /// This is the root of the stuck-"Indexing…" bug: SyncChanges returned early on the no-change
    /// path, so the five-minute periodic sweep verified the graph was current over and over and
    /// recorded none of it. A project nobody edits therefore never acquired a timestamp at all,
    /// and the control API rendered that missing timestamp as "indexing", forever.
    /// </summary>
    [Fact]
    public void SyncChanges_WithNothingChanged_StillRecordsTheIndexTime()
    {
        MakeUnityProject();

        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        // Clear the durable record the way an upgrade from a build that never wrote one leaves it.
        var store = new ProjectStore(new AppPaths(_appRoot));
        store.Save(store.Get(project.ProductGuid)! with { LastIndexedUtc = null });

        var reopened = NewService();
        Assert.Null(reopened.Summary(project.ProductGuid)!.LastIndexedUtc);

        var sweep = reopened.SyncChanges(project.ProductGuid);

        Assert.NotNull(sweep);
        Assert.False(sweep!.AnythingChanged, "the fixture was just indexed, so this sweep must find nothing - which is the case under test");
        Assert.NotNull(reopened.Summary(project.ProductGuid)!.LastIndexedUtc);
    }

    /// The last-indexed timestamp must SURVIVE A RESTART. "Has this project ever been indexed" is a
    /// durable fact about the graph sitting on disk, not a fact about the current process.
    ///
    /// It used to live only in a ConcurrentDictionary, so every fresh core answered "never" for
    /// every project - and because the control API derived "is an index running?" from that same
    /// null, the app then claimed to be indexing forever. Observed live on 2026-09-01: a 42 MB
    /// graph, 28,838 nodes, a blue "Indexing project_aurora…" that never ended, and zero disk I/O.
    /// </summary>
    [Fact]
    public void LastIndexedUtc_SurvivesARestart()
    {
        MakeUnityProject();

        var before = NewService();
        var project = before.AdoptAndIndex(_projectRoot)!;

        var indexedAt = before.Summary(project.ProductGuid)!.LastIndexedUtc;
        Assert.NotNull(indexedAt);

        // A second service over the same app root IS what a core restart is - same storage, new
        // process state. Nothing is re-indexed here, which is the point: the answer has to come off
        // disk, not from having just done the work.
        var after = NewService();

        Assert.Equal(indexedAt, after.Summary(project.ProductGuid)!.LastIndexedUtc);
    }

    /// <summary>
    /// A project adopted but never indexed still reports null after a restart - the timestamp is
    /// recorded on COMPLETION, so an interrupted or never-run index must not look finished.
    /// </summary>
    [Fact]
    public void LastIndexedUtc_IsNullForAProjectThatWasNeverIndexed()
    {
        MakeUnityProject();

        var before = NewService();
        var project = before.Adopt(_projectRoot)!;

        Assert.Null(before.Summary(project.ProductGuid)!.LastIndexedUtc);
        Assert.Null(NewService().Summary(project.ProductGuid)!.LastIndexedUtc);
    }


    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot }.Concat(_adoptedProjectRoots))
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
