using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Hades.Core.Editors;
using Hades.Core.Graph;
using Hades.Core.Indexing;
using Hades.Core.Observation;
using Hades.Core.Projects;
using Hades.Core.Reading;
using Hades.Core.Storage;
using Hades.Core.Unity;

namespace Hades.Core;

public sealed record ReferenceQueryResult
{
    public required string AssetPath { get; init; }
    public required string Guid { get; init; }

    /// <summary>Individual references, across every file. Often far larger than the file count.</summary>
    public required int TotalReferences { get; init; }

    /// <summary>Distinct files — the number that answers "how widely is this used".</summary>
    public required int ReferencingFileCount { get; init; }

    public required IReadOnlyList<Graph.ReferencingFile> Files { get; init; }

    /// <summary>True when more FILES exist than were returned. Because results are grouped, this
    /// trips far less often than a flat list would.</summary>
    public required bool Truncated { get; init; }
}

public sealed record ProjectSummary
{
    public required string ProductGuid { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required int TotalNodes { get; init; }
    public required IReadOnlyDictionary<string, int> NodesByKind { get; init; }

    /// <summary>Added for Plan 11 Task 3's <c>GET /control/projects</c>, which needs both node
    /// and edge counts per project - see <see cref="GraphDatabase.TotalEdges"/>.</summary>
    public required int TotalEdges { get; init; }
    public DateTimeOffset? LastIndexedUtc { get; init; }

    /// <summary>
    /// Plan 15 Task 3 Step 4 + Task 4: the C# preprocessor symbols Hades actually applied while
    /// parsing this project's scripts - see <see cref="Projects.ProjectDefines"/>'s own class doc
    /// comment for what goes into this set (UNITY_EDITOR, always; the Unity-version ladder from
    /// ProjectVersion.txt; scriptingDefineSymbols' "Standalone" target from ProjectSettings.asset;
    /// and every asmdef's own versionDefines whose named package resolves, via Packages/
    /// manifest.json or packages-lock.json, to a version satisfying its expression) and,
    /// critically, the limitation it does NOT hide: this is one project-wide UNION applied to
    /// every file, not the real compiler's per-assembly set - an asmdef that would NOT actually
    /// compile with one of these symbols is still indexed as if it did, and code gated on a
    /// symbol OUTSIDE this list (a platform define, a csc.rsp-only symbol, or a versionDefine
    /// keyed to a built-in Unity module rather than an installed package) does not appear in the
    /// graph at all. Reported explicitly, sorted, so that gap is something a caller can SEE - the
    /// same standard <c>/control/settings</c> and <c>find_orphan_scripts</c> already hold
    /// themselves to: an honest superset/approximation beats a silent one.
    /// </summary>
    public required IReadOnlyList<string> AppliedDefines { get; init; }
}

public sealed record SceneSummary
{
    public required string Path { get; init; }
    public required int GameObjectCount { get; init; }
    public required int RootCount { get; init; }
    public required IReadOnlyDictionary<string, int> ComponentsByKind { get; init; }
}

/// <summary>One authored memory document's listing entry - see <see cref="ProjectService.GetMemorySummary"/>.</summary>
public sealed record MemoryDocumentInfo
{
    public required string Name { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>The document's frontmatter "last_reviewed" value verbatim, or null when the
    /// document has no frontmatter or no such field.</summary>
    public string? LastReviewed { get; init; }
}

/// <summary>The result of one <see cref="ProjectService.GetMemorySummary"/> call.</summary>
public sealed record MemorySummary
{
    /// <summary>False for a project with no authored memory written yet - the ordinary state for
    /// a brand-new project, not an error.</summary>
    public required bool HasMemory { get; init; }

    public required IReadOnlyList<MemoryDocumentInfo> Documents { get; init; }
}

/// <summary>One stale reference found by <see cref="ProjectService.ValidateMemory"/>: a script
/// path a memory document names that no longer resolves in the graph.</summary>
public sealed record MemoryValidationFinding
{
    public required string Document { get; init; }
    public required string ScriptPath { get; init; }
}

/// <summary>
/// One component on a GameObject, resolved: what <see cref="ProjectService.GetComponents"/>
/// enriches <see cref="ComponentSummary"/> into by taking its one graph touch. Builtin components
/// need no resolution — <see cref="TypeName"/> is just their Unity class name — but a
/// MonoBehaviour's <c>m_Script</c> guid only names a script; <see cref="TypeName"/> becomes that
/// script's project-relative path once <see cref="Graph.GraphDatabase.PathForGuid"/> resolves it.
/// </summary>
public sealed record ComponentInfo
{
    public required long FileId { get; init; }

    /// <summary>The resolved type: a Unity class name for a builtin component, or a MonoBehaviour's
    /// script path once resolved. Null only when <see cref="Missing"/> is true.</summary>
    public string? TypeName { get; init; }

    /// <summary>The MonoBehaviour's raw <c>m_Script</c> guid. Null for every non-MonoBehaviour
    /// component.</summary>
    public string? ScriptGuid { get; init; }

    /// <summary>True for a MonoBehaviour whose <c>m_Script</c> guid did not resolve to any script
    /// node in the graph — a deleted or unindexed script. A genuinely useful finding surfaced with
    /// the raw guid, not an error swallowed into an empty or missing entry.</summary>
    public required bool Missing { get; init; }
}

public sealed record RebuildResult
{
    public required int NodesBefore { get; init; }
    public required int NodesAfter { get; init; }

    /// <summary>I5: per-file diagnostics from this rebuild — <see
    /// cref="Indexing.ScriptIndexer"/> and <see cref="Indexing.AssetIndexer"/> already build
    /// this list (a file that could not be read, or - I1 - could not be parsed); <see
    /// cref="ProjectService.Reindex"/> used to discard it entirely. Empty, never null, when
    /// nothing went wrong.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// One project's live Editor attachment, as reported by hades_charon_status - see
/// <see cref="ProjectService.GetCharonStatus"/>. Three states, not two:
///  - No registration in <see cref="EditorRegistry"/> at all: <see cref="Attached"/> false,
///    <see cref="Busy"/> false, every other field null EXCEPT <see cref="PluginVersionOnDisk"/>,
///    which may still report an on-disk install even with nothing attached - see that property's
///    own doc comment. "Not attached."
///  - A registration whose main thread answered the busy probe within the timeout:
///    <see cref="Attached"/> true, <see cref="Busy"/> false. "Attached."
///  - A registration whose main thread did NOT answer in time: <see cref="Attached"/> true,
///    <see cref="Busy"/> true. "Busy" - the connection itself is alive (proven by the
///    registration existing at all), only the main thread is not currently draining its queue.
/// The hello-derived fields (<see cref="UnityVersion"/>, <see cref="ProjectPath"/>,
/// <see cref="ProcessId"/>, <see cref="ConnectionAge"/>) are populated whenever
/// <see cref="Attached"/> is true, regardless of <see cref="Busy"/> - they come from the hello
/// sent at connect time, not from the probe, so a blocked main thread does not hide them.
/// </summary>
public sealed record CharonStatus
{
    public required bool Attached { get; init; }
    public required bool Busy { get; init; }
    public string? UnityVersion { get; init; }
    public string? ProjectPath { get; init; }
    public long? ProcessId { get; init; }
    public TimeSpan? ConnectionAge { get; init; }

    /// <summary>The attached plugin's own self-reported version - hello-derived, same "populated
    /// whenever Attached is true, regardless of Busy" rule as every other hello-derived field on
    /// this record (see this type's own class doc comment). Spec #4 §6: "the plugin reports its
    /// version on connect" - this is that report, surfaced live rather than re-derived from a file
    /// scan of the project's installed plugin (see Editors.PluginVersionSkew's own class doc
    /// comment for why the live value is preferred where callers compare it against
    /// Editors.PluginInstaller.AppPluginVersion).</summary>
    public string? PluginVersion { get; init; }

    /// <summary>
    /// The plugin version found on disk at the project's own
    /// <c>Assets/Hades/Runtime/HadesBoot.cs</c> - see
    /// <see cref="Editors.PluginInstaller.InstalledPluginVersion"/> - populated ONLY when <see
    /// cref="Attached"/> is false. Closes a real gap: with no Editor attached, <see
    /// cref="PluginVersion"/> is always null (it is hello-derived - there is no hello to derive it
    /// from), so a caller could not previously tell "the plugin is installed on disk, Unity just
    /// has not imported/reconnected it yet" apart from "nothing is installed in this project at
    /// all" - two very different remedies (open/focus Unity, vs. installPlugin first). Always null
    /// while <see cref="Attached"/> is true: the live <see cref="PluginVersion"/> is authoritative
    /// then, and re-reading the disk copy on top of it would add a second, potentially-stale
    /// source of truth for the exact same fact (see <see cref="Editors.PluginVersionSkew"/>'s own
    /// class doc comment for why the live value is preferred). Also null, even while detached,
    /// when nothing is installed on disk either - see
    /// <see cref="Editors.PluginInstaller.InstalledPluginVersion"/>'s own doc comment for why
    /// "not installed" and "installed but unreadable" collapse to the same null.
    /// </summary>
    public string? PluginVersionOnDisk { get; init; }
}

/// <summary>
/// One object reference, resolved: what <see cref="ProjectService.GetReference"/> (and, per
/// listener, <see cref="ProjectService.GetEventListeners"/>) enriches
/// <see cref="Reading.ObjectReferenceInfo"/> into by taking the one graph touch
/// <see cref="Reading.ReferenceReading"/> itself never does. A LOCAL reference (no guid) resolves
/// to the SAME containing file with no query at all; an EXTERNAL reference's guid is resolved via
/// <see cref="Graph.GraphDatabase.PathForGuid"/>, exactly like a MonoBehaviour's raw
/// <c>m_Script</c> guid in <see cref="ComponentInfo"/>.
/// </summary>
public sealed record ResolvedReference
{
    public required long FileId { get; init; }
    public string? Guid { get; init; }

    /// <summary>True for Unity's own null reference. <see cref="ResolvedPath"/> is always null and
    /// <see cref="Resolved"/> always false when this is true - there is nothing to resolve.</summary>
    public required bool IsUnset { get; init; }

    /// <summary>True when this points at another object in the SAME file rather than another
    /// asset.</summary>
    public required bool IsLocal { get; init; }

    /// <summary>The project-relative path this reference points at: the containing file itself
    /// when <see cref="IsLocal"/>, the resolved external asset when not, or null when unresolved
    /// or unset.</summary>
    public string? ResolvedPath { get; init; }

    public required bool Resolved { get; init; }
}

/// <summary>One persistent (Inspector-wired) UnityEvent listener, target resolved - see
/// <see cref="ProjectService.GetEventListeners"/>.</summary>
public sealed record EventListenerInfo
{
    public required string EventField { get; init; }
    public required int Index { get; init; }
    public required ResolvedReference Target { get; init; }
    public string? TargetAssemblyTypeName { get; init; }
    public required string MethodName { get; init; }
    public required string Mode { get; init; }
    public required string CallState { get; init; }
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; }
}

/// <summary>
/// The core's façade: adopt a project, index it, query it. Everything above this —
/// MCP tools, the control API, a future CLI — goes through here, so behaviour is
/// defined once and stays headless-testable.
/// </summary>
public sealed class ProjectService(AppPaths paths, EditorRegistry? registry = null)
{
    readonly ProjectStore _store = new(paths);
    readonly Memory.MemoryStore _memory = new(paths);
    readonly Memory.MemoryProposals _proposals = new(paths);

    // Optional constructor parameter, not a hard DI dependency: the app wires one shared
    // EditorRegistry through here (see Program.cs) so hades_charon_status sees the same editors
    // EditorListener registers, but every other caller — direct construction throughout the test
    // suite, anything that never touches Charon status — should not have to know this type
    // exists. A fresh, permanently-empty registry is a correct answer for "no editor ever
    // attached", which is exactly what those callers want.
    readonly EditorRegistry _editorRegistry = registry ?? new EditorRegistry();

    // Dictionary<TKey,TValue> is not safe for concurrent writers, even to different keys —
    // internal bucket/resize state races, which surfaces as InvalidOperationException or
    // silently lost writes under enough concurrent projects and threads. Reindex() writes
    // this on every call, and nothing otherwise serialises EnsureIndexed for different
    // projects — exactly what a server fielding concurrent requests across multiple roots does.
    readonly ConcurrentDictionary<string, DateTimeOffset> _lastIndexed = new();

    // One semaphore per productGuid, created on first use. Makes EnsureIndexed single-flight
    // per project: without it, check-then-act still lets concurrent callers all pass the
    // "not yet indexed" check and all reindex — correct (delete-then-insert is idempotent) but
    // wasted work, which this closes for cheap alongside the correctness fix above.
    readonly ConcurrentDictionary<string, SemaphoreSlim> _indexGates = new();

    // Guards memory import to at most once per project per process, for the same reason
    // Adopt itself must stay cheap: it runs on every routed tool call (see RootsRouter), and
    // MemoryStore.ImportFromArcforge's own per-file "already exists" check already makes a
    // second scan pointless work, not a correctness risk. No wait-for-completion semantics are
    // needed here the way _indexGates provides for Reindex: at worst two racing first-adopters
    // both pass this check, and the loser's TryAdd simply loses, it does not block.
    readonly ConcurrentDictionary<string, byte> _memoryImported = new();

    public AppPaths Paths => paths;

    /// <summary>How long <see cref="GetCharonStatus"/> waits for its busy probe to answer before
    /// concluding the main thread is blocked. <c>init</c>, not a constructor parameter — matches
    /// ObservationService's PeriodicInterval/Debounce convention for a tunable that almost never
    /// needs tuning. Only the busy path ever waits this long; an idle Editor answers in well
    /// under a frame.</summary>
    public TimeSpan CharonProbeTimeout { get; init; } = TimeSpan.FromSeconds(1.5);

    public IReadOnlyList<UnityProject> KnownProjects() => _store.All();

    public UnityProject? Get(string productGuid) => _store.Get(productGuid);

    /// <summary>
    /// Raised whenever <see cref="Adopt"/> (directly, or via <see cref="AdoptAndIndex"/>)
    /// registers or re-registers a project — including on every routing call through RootsRouter,
    /// which adopts on every request by design (see <see cref="Adopt"/>'s own doc comment: "cheap
    /// enough to call on every request"). Exists so <see cref="Observation.ObservationService"/>
    /// can enroll a live watcher for a project added at runtime, after it already started (F14:
    /// Start() only ever watched what <see cref="KnownProjects"/> listed AT THAT MOMENT — a
    /// project registered afterwards, via POST /control/projects/add or RootsRouter, never got
    /// one), without ProjectService taking a hard dependency on Observation — same reasoning as
    /// ObservationService's own ProjectSynced event.
    /// </summary>
    public event Action<UnityProject>? ProjectAdopted;

    /// <summary>Raised when <see cref="RemoveProject"/> actually deregisters a project Hades
    /// previously knew — never for a productGuid Hades never knew (see
    /// <see cref="ProjectStore.Remove"/>'s own doc comment: that's the one case it returns false).
    /// Exists so <see cref="Observation.ObservationService"/> can dispose that project's live
    /// watcher — see <see cref="ProjectAdopted"/>'s own doc comment for why this is an event
    /// rather than a direct dependency.</summary>
    public event Action<string>? ProjectRemoved;

    /// <summary>Deregisters a project - see <see cref="ProjectStore.Remove"/>'s own doc comment
    /// for exactly what "deregister" means (nothing on disk is ever deleted, least of all
    /// authored memory). Returns false when <paramref name="productGuid"/> is not, or was never,
    /// known.</summary>
    public bool RemoveProject(string productGuid)
    {
        if (!_store.Remove(productGuid)) return false;

        ProjectRemoved?.Invoke(productGuid);
        return true;
    }

    /// <summary>
    /// Persists what a Unity Editor's Hello just reported: the live <see
    /// cref="UnityProject.UnityVersion"/> (project.json's own copy is otherwise written once, at
    /// <see cref="Adopt"/> time, and never revisited — it stays null forever for a project adopted
    /// before any Editor ever attached) and a fresh <see cref="UnityProject.LastSeen"/>. Called once
    /// per successful <see cref="EditorListener.Register"/> — see that method's own doc comment for
    /// exactly when "a Hello completes" means. Deliberately minimal: an attach-time update only, no
    /// periodic write while the connection stays open.
    ///
    /// A no-op — never a throw — when <paramref name="productGuid"/> is not (yet) known: the normal
    /// startup path always Adopts before an Editor can attach, but a Hello can in principle arrive
    /// for a project Hades has never adopted through any route, and there is nothing to update.
    /// <paramref name="unityVersion"/> blank/whitespace (a well-formed Hello can still report an
    /// empty string — Hello only requires "unityVersion" to be present and a string, never that it
    /// be non-empty) leaves the previously recorded version alone rather than clobbering a real
    /// value with an unhelpful blank; <see cref="UnityProject.LastSeen"/> still bumps either way,
    /// since a Hello arriving at all is itself the liveness signal, independent of what it reports.
    /// </summary>
    public void RecordEditorAttached(string productGuid, string? unityVersion)
    {
        if (_store.Get(productGuid) is not { } project) return;

        _store.Save(project with
        {
            UnityVersion = string.IsNullOrWhiteSpace(unityVersion) ? project.UnityVersion : unityVersion,
            LastSeen = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Registers a project without indexing it, and imports any pre-existing
    /// .arcforge/memory content the first time this process adopts it (see
    /// <see cref="Memory.MemoryStore.ImportFromArcforge"/>) — a user who has been authoring
    /// memory inside the Unity project itself must not have that work silently abandoned by
    /// storage moving to app-space. Still cheap enough to call on every request: routing must
    /// never trigger a scan as a side effect, so the import is attempted at most once per
    /// project per process.
    /// </summary>
    public UnityProject? Adopt(string projectRoot)
    {
        var project = _store.Adopt(projectRoot);
        if (project is null) return null;

        if (_memoryImported.TryAdd(project.ProductGuid, 0))
        {
            try
            {
                _memory.ImportFromArcforge(project.ProductGuid, projectRoot);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The project genuinely registered above (_store.Adopt already wrote
                // project.json), so a failed import must not abort adoption - throwing here
                // would leave a half-adopted project whose ProjectAdopted below never fires.
                // Removing the marker keeps the import retryable on a later Adopt instead of
                // this process permanently giving up after one environmental failure.
                _memoryImported.TryRemove(project.ProductGuid, out _);
            }
        }

        ProjectAdopted?.Invoke(project);
        return project;
    }

    /// <summary>Registers a project (if it is one), imports its memory, and performs a full
    /// index. Returns null when the directory is not a Unity project or its settings are
    /// unreadable.</summary>
    public UnityProject? AdoptAndIndex(string projectRoot)
    {
        var project = Adopt(projectRoot);
        if (project is null) return null;

        Reindex(project);
        return project;
    }

    /// <summary>Indexes the project only if it has never been indexed in this process.
    /// Plan 3 replaces this with FSEvents-driven incremental indexing.</summary>
    public void EnsureIndexed(string productGuid)
    {
        if (_lastIndexed.ContainsKey(productGuid)) return;

        var gate = _indexGates.GetOrAdd(productGuid, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            // Re-check inside the gate: another caller may have already indexed this project
            // while this one waited, which is the normal case under concurrent load.
            if (_lastIndexed.ContainsKey(productGuid)) return;
            if (_store.Get(productGuid) is { } project) Reindex(project);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Full reindex of one project: walks scripts and assets from scratch, then reconciles
    /// file_state against disk. Returns the merged per-file diagnostics <see
    /// cref="Indexing.ScriptIndexer"/> and <see cref="Indexing.AssetIndexer"/> collected (I5) —
    /// previously discarded here entirely, so a poison file's own I1 warning never reached any
    /// caller of <see cref="RebuildGraph"/>.
    ///
    /// I6: each file's recorded on-disk state (mtime/size) is captured BEFORE indexing reads its
    /// content, not after. Recording it after — the previous order — meant a file that changed
    /// between being read and being stat'd was recorded with its NEW stamp against the OLD
    /// content that was actually indexed: a silent, permanent miss, since the next sweep would
    /// see recorded and on-disk state already agreeing and never revisit it. Stamping first means
    /// the worst case shifts to one redundant reindex next sweep, never a missed one.
    ///
    /// I8: SweepStaleNodes (called inside IndexProject, once per scan root) already removes a
    /// deleted file's graph nodes and edges, but never its file_state row. <see
    /// cref="GraphDatabase.DeleteNodesForPath"/> does remove that row too (see its own doc
    /// comment) — calling it here for every path this run's sweep found deleted closes that gap
    /// the exact same way <see cref="SyncChanges"/> already handles its own deletions. Safe to
    /// call even for a path SweepStaleNodes already cleared: a DELETE against rows that no
    /// longer exist is a no-op.
    /// </summary>
    IReadOnlyList<string> Reindex(UnityProject project)
    {
        using var database = OpenGraph(project.ProductGuid);

        // Sweep BEFORE indexing, not after — see this method's own doc comment (I6). What
        // "changed since last recorded" means is unaffected by whether IndexProject has run yet:
        // both sides of that comparison (file_state, on-disk) are independent of the graph.
        var sweep = ProjectSweeper.Sweep(project.Path, database);
        var freshState = ProjectSweeper.StateFor(project.Path, sweep.Added.Concat(sweep.Changed));

        var scripts = ScriptIndexer.IndexProject(project.Path, database);
        var assets = AssetIndexer.IndexProject(project.Path, database);

        database.UpsertFileState(freshState);

        // I8: file_state rows for files gone since the state above was captured.
        foreach (var deleted in sweep.Deleted) database.DeleteNodesForPath(deleted);

        _lastIndexed[project.ProductGuid] = DateTimeOffset.UtcNow;

        return [.. sweep.Warnings, .. scripts.Warnings, .. assets.Warnings];
    }

    /// <summary>
    /// Brings the graph up to date by indexing only what differs from the recorded state. Returns
    /// the sweep so a caller can log or act on what moved; a project with no changes costs one
    /// sweep and nothing else.
    ///
    /// I2: acquires the SAME per-project <see cref="_indexGates"/> semaphore <see
    /// cref="RebuildGraph"/> and <see cref="EnsureIndexed"/> both do, before touching the graph —
    /// see <see cref="RebuildGraph"/>'s own doc comment for the full lock-ordering reasoning. This
    /// method used to acquire no gate at all, so a concurrent <see cref="RebuildGraph"/> (or a
    /// second, overlapping call here) could run against the same project through two independent
    /// database connections with nothing stopping the two from interleaving writes and losing
    /// updates — the same class of race <see cref="RebuildGraph"/>'s own gate was added to close,
    /// just never extended to this, its most frequent caller (the ObservationService watcher and
    /// periodic sweep both reach the graph only through here).
    /// </summary>
    public SweepResult? SyncChanges(string productGuid)
    {
        if (_store.Get(productGuid) is not { } project) return null;

        var gate = _indexGates.GetOrAdd(productGuid, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            using var database = OpenGraph(productGuid);
            var sweep = ProjectSweeper.Sweep(project.Path, database);

            if (!sweep.AnythingChanged) return sweep;

            // Deletions first, and by explicit path — never by sweeping a partial visited-set.
            foreach (var deleted in sweep.Deleted) database.DeleteNodesForPath(deleted);

            var toIndex = sweep.NeedsIndexing;
            var warnings = sweep.Warnings;
            if (toIndex.Count > 0)
            {
                // I6: stat BEFORE indexing reads file content — see Reindex's own doc comment
                // for why the order matters.
                var preReadState = ProjectSweeper.StateFor(project.Path, toIndex);

                var scripts = ScriptIndexer.IndexFiles(project.Path, database, toIndex);
                var assets = AssetIndexer.IndexFiles(project.Path, database, toIndex);
                database.UpsertFileState(preReadState);

                warnings = [.. warnings, .. scripts.Warnings, .. assets.Warnings]; // I5
            }

            _lastIndexed[productGuid] = DateTimeOffset.UtcNow;
            return sweep with { Warnings = warnings };
        }
        finally
        {
            gate.Release();
        }
    }

    public IReadOnlyList<GraphNode> Search(string productGuid, string pattern, string? kind = null, int limit = 50)
    {
        if (_store.Get(productGuid) is null) return [];

        using var database = OpenGraph(productGuid);
        return database.SearchByName(pattern, kind, limit);
    }

    /// <summary>
    /// Everything referencing the asset at <paramref name="assetPath"/>. Returns null when the
    /// path is unknown to the graph, which the caller must distinguish from "known, zero
    /// references" — those mean very different things to someone asking what would break.
    /// </summary>
    public ReferenceQueryResult? FindReferencesTo(string productGuid, string assetPath, int limit = 100)
    {
        if (_store.Get(productGuid) is null) return null;

        using var database = OpenGraph(productGuid);

        var guid = database.GuidForPath(assetPath);
        if (guid is null) return null;

        var totalReferences = database.CountReferencesTo(guid, assetPath);
        var totalFiles = database.CountReferencingFiles(guid, assetPath);
        var files = database.ReferencingFiles(guid, assetPath, limit);

        return new ReferenceQueryResult
        {
            AssetPath = assetPath,
            Guid = guid,
            TotalReferences = totalReferences,
            ReferencingFileCount = totalFiles,
            Files = files,
            Truncated = totalFiles > files.Count,
        };
    }

    /// <summary>
    /// Everything <paramref name="rootPath"/> depends on, walking `references` edges outward —
    /// plus (F6-honesty) every dangling dependency found along the way, see
    /// <see cref="Graph.DependencyTrace"/>. Returns null when the project or the root path itself
    /// is unknown to the graph — mirrors <see cref="FindReferencesTo"/>'s null-means-unknown-path
    /// convention, including its use of <see cref="GraphDatabase.GuidForPath"/> as the existence
    /// check, even though the walk itself only ever needs paths, not the root's own GUID.
    /// </summary>
    public DependencyTrace? TraceDependencies(string productGuid, string rootPath, int maxDepth = 3)
    {
        if (_store.Get(productGuid) is null) return null;

        using var database = OpenGraph(productGuid);
        if (database.GuidForPath(rootPath) is null) return null;

        return database.TraceDependencies(rootPath, maxDepth);
    }

    /// <summary>
    /// True when <paramref name="assetPath"/> — or its .meta sibling — exists on disk under
    /// <paramref name="productGuid"/>'s project root. F6-honesty: the only use is
    /// <see cref="Mcp.HadesTools.FindReferencesTo"/> distinguishing, for a path
    /// <see cref="FindReferencesTo"/> could not resolve in the graph, "exists but is not a graph
    /// node" from "genuinely does not exist". The former no longer means "an asset type Hades does
    /// not index" for textures, models, audio, fonts, shaders, or animation clips specifically —
    /// those ARE indexed today, as meta-only nodes (path, name, kind from the extension, guid from
    /// the sibling .meta; no content ever read, no edges ever written FROM one — see
    /// <see cref="BinaryAssetIndexer"/> and <see cref="ImportedAssetKind"/>). What still resolves
    /// true here instead: a path outside every root Hades scans at all — a package resolved into
    /// Library/PackageCache is walked by nothing regardless of its type (see
    /// <see cref="ProjectWalker.ResolveScanRoots"/>) — or an asset kind neither
    /// <see cref="AssetIndexer"/>'s YAML parsing nor <see cref="ImportedAssetKind"/>'s binary
    /// mapping recognises at all (video, terrain data, and the rest of Unity's importer surface —
    /// see that type's own doc comment for the deliberately-smaller-than-complete extension list).
    /// The .meta check alone covers a folder asset (whose own content is a directory, not a file) without a separate
    /// Directory.Exists call — Unity always gives a folder a sibling .meta file the same way it
    /// gives every other asset one.
    ///
    /// False for an unknown project (nothing to resolve <paramref name="assetPath"/> against), or
    /// for a path that is not even a well-formed project-relative path at all
    /// (<see cref="ReadThrough.ResolveAssetPath"/> throwing <see cref="ArgumentException"/> for a
    /// rooted path, one that escapes every scan root, or one that exits via a symlink) — every
    /// failure mode collapses to the same conservative "cannot confirm existence" as a genuinely
    /// missing file, never an exception a caller of <see cref="FindReferencesTo"/> would have to
    /// handle separately.
    /// </summary>
    public bool ExistsOnDisk(string productGuid, string assetPath)
    {
        if (_store.Get(productGuid) is not { } project) return false;

        try
        {
            var resolved = ReadThrough.ResolveAssetPath(project.Path, assetPath);
            return File.Exists(resolved) || File.Exists(resolved + ".meta");
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Prefabs (never scenes) with a component referencing the script at <paramref
    /// name="scriptPath"/>. Returns null when the project or the script path is unknown — same
    /// convention as <see cref="FindReferencesTo"/>.
    /// </summary>
    public IReadOnlyList<ReferencingFile>? FindPrefabsWithComponent(string productGuid, string scriptPath, int limit = 100)
    {
        if (_store.Get(productGuid) is null) return null;

        using var database = OpenGraph(productGuid);

        var guid = database.GuidForPath(scriptPath);
        if (guid is null) return null;

        return database.PrefabsReferencing(guid, limit);
    }

    /// <summary>
    /// Prefabs and scenes with a component referencing any script whose name matches <paramref
    /// name="namePattern"/>. A pattern search, not an existence lookup — an unknown project or a
    /// pattern that matches nothing both mean "no results", so this returns an empty list rather
    /// than null in either case (unlike <see cref="FindPrefabsWithComponent"/>, which resolves a
    /// specific path that either exists or doesn't).
    /// </summary>
    public IReadOnlyList<ComponentUsage> FindComponentsUsingPattern(string productGuid, string namePattern, int limit = 100)
    {
        if (_store.Get(productGuid) is null) return [];

        using var database = OpenGraph(productGuid);
        return database.ComponentsUsingPattern(namePattern, limit);
    }

    /// <summary>Class-kind scripts with no incoming Unity reference. See
    /// <see cref="Graph.GraphDatabase.OrphanScripts"/> for why this is an honest superset, not a
    /// confirmed dead-code list. Pattern-search shape like <see cref="FindComponentsUsingPattern"/>:
    /// an unknown project just means an empty list.</summary>
    public IReadOnlyList<GraphNode> FindOrphanScripts(string productGuid, int limit = 100)
    {
        if (_store.Get(productGuid) is null) return [];

        using var database = OpenGraph(productGuid);
        return database.OrphanScripts(limit);
    }

    /// <summary>Components anywhere in the project matching a type-name pattern. See
    /// <see cref="Graph.GraphDatabase.ComponentsMatching"/>. Pattern-search shape like
    /// <see cref="FindComponentsUsingPattern"/>: an unknown project just means an empty list.</summary>
    public IReadOnlyList<ComponentMatch> ComponentFind(string productGuid, string typeNamePattern, int limit = 100)
    {
        if (_store.Get(productGuid) is null) return [];

        using var database = OpenGraph(productGuid);
        return database.ComponentsMatching(typeNamePattern, limit);
    }

    /// <summary>
    /// Distinct project files behind asset_find, optionally narrowed by extension-derived type and
    /// path prefix. The type filter cannot be pushed into <see cref="Graph.GraphDatabase.DistinctPaths"/>'s
    /// SQL - a file's <see cref="Unity.AssetType"/> is derived from its extension, not stored as a
    /// column - so it is applied here instead, after the (already prefix-narrowed) path list comes
    /// back. Pattern-search shape like <see cref="ComponentFind"/>: an unknown project just means
    /// an empty list. <paramref name="limit"/> is applied last, exactly like every other
    /// pattern-search method here - callers pass limit + 1 to detect truncation the same way.
    /// </summary>
    public IReadOnlyList<AssetMatch> FindAssets(string productGuid, string? typeFilter, string? pathPrefix, int limit = 100)
    {
        if (_store.Get(productGuid) is null) return [];

        using var database = OpenGraph(productGuid);

        return database.DistinctPaths(pathPrefix)
            .Select(path => new AssetMatch { Path = path, Type = AssetType.FromPath(path) })
            .Where(asset => typeFilter is null || asset.Type == typeFilter)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Distinct project files by extension-derived type and path prefix, sourced from
    /// <c>file_state</c> rather than the graph's <c>nodes</c> table - see
    /// <see cref="Graph.GraphDatabase.DistinctFileStatePaths"/> for why. This is what closes Plan
    /// 10 Task 6's asset_find gap: graph_query's <c>fileType</c> filter calls this, not
    /// <see cref="FindAssets"/> (which asset_find itself used, and which Task 6 removes along with
    /// that tool). Same pattern-search shape as <see cref="FindAssets"/> throughout, including
    /// where the type filter is applied (C#, after the prefix-narrowed path list comes back, for
    /// the identical reason - a file's <see cref="Unity.AssetType"/> is derived from its extension,
    /// not stored as a column).
    /// </summary>
    public IReadOnlyList<AssetMatch> FindAssetsByFileState(string productGuid, string? typeFilter, string? pathPrefix, int limit = 100)
    {
        if (_store.Get(productGuid) is null) return [];

        using var database = OpenGraph(productGuid);

        return database.DistinctFileStatePaths(pathPrefix)
            .Select(path => new AssetMatch { Path = path, Type = AssetType.FromPath(path) })
            .Where(asset => typeFilter is null || asset.Type == typeFilter)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Every component on one GameObject, resolved: <see cref="ReadThrough.GetComponents"/> reads
    /// the named file (no graph involved), and this is where its raw MonoBehaviour script guids
    /// get their one graph touch — <see cref="Graph.GraphDatabase.PathForGuid"/> — turning "some
    /// MonoBehaviour" into the actual script, or into a clearly-flagged missing reference when the
    /// guid resolves to nothing. Returns null when the project is unknown; anything
    /// <see cref="ReadThrough.GetComponents"/> itself throws (bad path, unknown fileId, ...)
    /// propagates uncaught, exactly as <see cref="Mcp.InspectionTools"/>'s other read-through
    /// calls expect.
    /// </summary>
    public IReadOnlyList<ComponentInfo>? GetComponents(string productGuid, string relativePath, long gameObjectFileId)
    {
        if (_store.Get(productGuid) is not { } project) return null;

        var raw = ReadThrough.GetComponents(project.Path, relativePath, gameObjectFileId);

        using var database = OpenGraph(productGuid);

        return raw.Select(component =>
        {
            if (component.Kind != "MonoBehaviour")
            {
                return new ComponentInfo { FileId = component.FileId, TypeName = component.Kind, Missing = false };
            }

            var resolved = component.ScriptGuid is not null ? database.PathForGuid(component.ScriptGuid) : null;
            return new ComponentInfo
            {
                FileId = component.FileId,
                TypeName = resolved,
                ScriptGuid = component.ScriptGuid,
                Missing = resolved is null,
            };
        }).ToList();
    }

    /// <summary>
    /// Resolves one named field on one object, read straight from the file
    /// (<see cref="ReferenceReading.GetReference"/>), then takes the one graph touch to resolve it
    /// - see <see cref="ResolveTarget"/>. Returns null when the project is unknown; anything
    /// <see cref="ReferenceReading.GetReference"/> itself throws (bad path, unknown fileId, unknown
    /// property, not a reference-shaped field) propagates uncaught, exactly as
    /// <see cref="GetComponents"/>'s own doc comment describes for the same shape of call.
    /// </summary>
    public ResolvedReference? GetReference(string productGuid, string relativePath, long fileId, string property)
    {
        if (_store.Get(productGuid) is not { } project) return null;

        var raw = ReferenceReading.GetReference(project.Path, relativePath, fileId, property);

        using var database = OpenGraph(productGuid);
        return ResolveTarget(database, relativePath, raw);
    }

    /// <summary>
    /// Every persistent listener on one object's UnityEvent fields, read straight from the file
    /// (<see cref="ReferenceReading.GetEventListeners"/>), each listener's target resolved the same
    /// way <see cref="GetReference"/> resolves one. Returns null when the project is unknown;
    /// anything <see cref="ReferenceReading.GetEventListeners"/> itself throws propagates uncaught.
    /// </summary>
    public IReadOnlyList<EventListenerInfo>? GetEventListeners(string productGuid, string relativePath, long fileId)
    {
        if (_store.Get(productGuid) is not { } project) return null;

        var raw = ReferenceReading.GetEventListeners(project.Path, relativePath, fileId);

        using var database = OpenGraph(productGuid);

        return raw.Select(call => new EventListenerInfo
        {
            EventField = call.EventField,
            Index = call.Index,
            Target = ResolveTarget(database, relativePath, call.Target),
            TargetAssemblyTypeName = call.TargetAssemblyTypeName,
            MethodName = call.MethodName,
            Mode = call.Mode,
            CallState = call.CallState,
            Arguments = call.Arguments,
        }).ToList();
    }

    /// <summary>
    /// Turns a RAW object reference - fileId plus optional guid, exactly as
    /// <see cref="ReferenceReading"/> reads it straight off disk - into a resolved path. A LOCAL
    /// reference (no guid, not unset) resolves trivially to <paramref name="containingPath"/> -
    /// there is nothing to query, it IS the containing file. An EXTERNAL reference's guid is
    /// resolved via <see cref="Graph.GraphDatabase.PathForGuid"/>; a guid that does not resolve
    /// (a built-in resource, or an asset outside any scan root) is reported unresolved rather than
    /// failing the call, the same "still return everything else, honestly" stance
    /// <see cref="ReadThrough.GetMaterialProperties"/> takes for an unresolved shader guid.
    /// </summary>
    static ResolvedReference ResolveTarget(GraphDatabase database, string containingPath, ObjectReferenceInfo raw)
    {
        if (raw.IsUnset)
        {
            return new ResolvedReference
            {
                FileId = raw.FileId, Guid = raw.Guid, IsUnset = true, IsLocal = false, ResolvedPath = null, Resolved = false,
            };
        }

        if (raw.Guid is null)
        {
            return new ResolvedReference
            {
                FileId = raw.FileId, Guid = null, IsUnset = false, IsLocal = true, ResolvedPath = containingPath, Resolved = true,
            };
        }

        var resolvedPath = database.PathForGuid(raw.Guid);
        return new ResolvedReference
        {
            FileId = raw.FileId, Guid = raw.Guid, IsUnset = false, IsLocal = false,
            ResolvedPath = resolvedPath, Resolved = resolvedPath is not null,
        };
    }

    /// <summary>UnityEvent fields anywhere in the project with at least one wired listener. See
    /// <see cref="Graph.GraphDatabase.FindUnityEvents"/> for why this is an honest superset, not a
    /// census of every UnityEvent field. Pattern-search shape like <see cref="ComponentFind"/>: an
    /// unknown project just means an empty list.</summary>
    public IReadOnlyList<Graph.UnityEventHit> FindUnityEvents(string productGuid, int limit = 100)
    {
        if (_store.Get(productGuid) is null) return [];

        using var database = OpenGraph(productGuid);
        return database.FindUnityEvents(limit);
    }

    /// <summary>The structured filter behind query_graph/graph_query. See
    /// <see cref="Graph.GraphDatabase.QueryGraph"/> for the filter semantics and why raw SQL input
    /// is structurally impossible here. Pattern-search shape like <see cref="ComponentFind"/>: an
    /// unknown project just means an empty list.
    ///
    /// <paramref name="edgeTargetPath"/> (Plan 10 Task 4, graph_query only - query_graph's own MCP
    /// tool never passes it) is resolved to a guid HERE, the same layering
    /// <see cref="FindPrefabsWithComponent"/> already established for its own scriptPath parameter -
    /// <see cref="Graph.GraphDatabase"/> works purely in guid-space. Unlike
    /// FindPrefabsWithComponent, an unresolvable path does NOT throw: graph_query is a filter tool
    /// where every other criterion (kind, namePattern, pathPrefix) already degrades to "no results"
    /// rather than an error when it matches nothing, and a target path nothing in the graph owns is
    /// the same kind of "matches nothing", not a distinct failure - so this short-circuits to an
    /// empty list without even opening a reader, the identical outcome a caller would see from a
    /// query that legitimately found zero rows.</summary>
    public IReadOnlyList<GraphNode> QueryGraph(string productGuid, string? kind, string? namePattern,
        string? pathPrefix, string? edgeKind, string edgeDirection, int limit = 100,
        string? edgeTargetPath = null, string? edgeTargetNamePattern = null, bool edgeAbsent = false,
        string? kindPattern = null, string? edgeTargetKind = null)
    {
        if (_store.Get(productGuid) is null) return [];

        using var database = OpenGraph(productGuid);

        string? edgeTargetGuid = null;
        if (edgeTargetPath is not null)
        {
            edgeTargetGuid = database.GuidForPath(edgeTargetPath);
            if (edgeTargetGuid is null) return [];
        }

        return database.QueryGraph(kind, namePattern, pathPrefix, edgeKind, edgeDirection, limit,
            edgeTargetGuid, edgeTargetNamePattern, edgeAbsent, kindPattern, edgeTargetKind);
    }

    /// <summary>
    /// The distinct node kinds actually present in this project's graph, sorted - the same source
    /// <see cref="ProjectSummary.NodesByKind"/> (get_project_summary's own "nodesByKind") already
    /// computes, reused here so graph_query's <c>kind</c> validation (F7) can distinguish an
    /// unrecognised kind - a typo, or a plausible-but-wrong guess like "Scene", never a real node
    /// kind - from a genuinely empty result for a valid one, and name the real vocabulary rather
    /// than leaving a caller to guess. Pattern-search shape like <see cref="Search"/>: an unknown
    /// project just means an empty list.
    /// </summary>
    public IReadOnlyList<string> KnownNodeKinds(string productGuid)
    {
        if (_store.Get(productGuid) is null) return [];

        using var database = OpenGraph(productGuid);
        return database.CountByKind().Keys.ToList();
    }

    public ProjectSummary? Summary(string productGuid)
    {
        var project = _store.Get(productGuid);
        if (project is null) return null;

        using var database = OpenGraph(productGuid);

        return new ProjectSummary
        {
            ProductGuid = project.ProductGuid,
            Name = project.Name,
            Path = project.Path,
            TotalNodes = database.TotalNodes(),
            NodesByKind = database.CountByKind(),
            TotalEdges = database.TotalEdges(),
            LastIndexedUtc = _lastIndexed.TryGetValue(productGuid, out var at) ? at : null,

            // Resolved fresh from disk, same "reflects last saved state" convention as every
            // other project-level fact Hades reports - see ProjectDefines.Resolve's own doc
            // comment for why recomputing here (two small file reads) is cheap enough to not
            // need caching alongside the indexed graph.
            AppliedDefines = ProjectDefines.Resolve(project.Path).Symbols,
        };
    }

    /// <summary>
    /// GameObject count, root-GameObject count, and a per-kind breakdown for one scene or prefab.
    /// Returns null when the project or the path itself is unknown to the graph — same
    /// null-means-unknown-path convention as <see cref="FindReferencesTo"/>.
    /// </summary>
    public SceneSummary? GetSceneSummary(string productGuid, string path)
    {
        if (_store.Get(productGuid) is null) return null;

        using var database = OpenGraph(productGuid);
        if (database.GuidForPath(path) is null) return null;

        var byKind = database.CountByKindForPath(path);

        return new SceneSummary
        {
            Path = path,
            GameObjectCount = byKind.GetValueOrDefault("GameObject"),
            RootCount = database.CountRootGameObjects(path),
            ComponentsByKind = byKind,
        };
    }

    /// <summary>
    /// Recently touched files, newest first. Reads <c>file_state</c>, populated at index time —
    /// no rescan, no new storage. An unknown project yields an empty list rather than null:
    /// pattern-search shape like <see cref="Search"/>, since there is no single "target" path that
    /// can be unknown here.
    /// </summary>
    public IReadOnlyList<FileState> RecentlyChanged(string productGuid, DateTimeOffset? since = null, int limit = 50)
    {
        if (_store.Get(productGuid) is null) return [];

        using var database = OpenGraph(productGuid);
        return database.RecentlyChanged(since?.ToUnixTimeMilliseconds(), limit);
    }

    /// <summary>
    /// Forces a full reindex regardless of whether this process already indexed the project this
    /// run, and reports the node count immediately before and after, plus any per-file warnings
    /// the reindex collected (I5). Returns null when the project is unknown.
    ///
    /// Acquires the SAME per-project <see cref="_indexGates"/> semaphore <see
    /// cref="EnsureIndexed"/> AND <see cref="SyncChanges"/> both do before touching <see
    /// cref="Reindex"/> - a forced rebuild used to call <see cref="Reindex"/> directly, bypassing
    /// that gate entirely, so it could run concurrently with a routine <see cref="EnsureIndexed"/>
    /// OR an ObservationService sweep - which reaches the graph only through <see
    /// cref="SyncChanges"/>, and (I2) used to acquire no gate of its own at all - reindexing the
    /// SAME project through a second, independent database connection, with nothing stopping the
    /// two from interleaving writes and losing updates.
    ///
    /// I2's lock-ordering design, in full: there is exactly ONE lock per project - this same
    /// <see cref="_indexGates"/> semaphore - and all three entry points (this method, <see
    /// cref="EnsureIndexed"/>, <see cref="SyncChanges"/>) acquire it and nothing else while
    /// touching this project's graph, so there is no second lock to order against and therefore
    /// no cycle that could deadlock. This blocks (synchronously) until any such indexing already
    /// in flight for this project finishes first - exactly as two concurrent <see
    /// cref="EnsureIndexed"/> callers already block on each other - never runs Reindex twice for
    /// one call, and cannot deadlock against <see cref="EnsureIndexed"/> or <see
    /// cref="SyncChanges"/>: every caller of all three methods (hades_rebuild_graph and the
    /// Control API's async rebuild operation here; RootsRouter and EnsureIndexed's own re-check
    /// there; the ObservationService watcher/periodic sweep for SyncChanges) is a top-level entry
    /// point that never runs from inside an already-held gate.
    /// </summary>
    public RebuildResult? RebuildGraph(string productGuid)
    {
        if (_store.Get(productGuid) is not { } project) return null;

        var gate = _indexGates.GetOrAdd(productGuid, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var before = Summary(productGuid)?.TotalNodes ?? 0;
            var warnings = Reindex(project);
            var after = Summary(productGuid)?.TotalNodes ?? 0;

            return new RebuildResult { NodesBefore = before, NodesAfter = after, Warnings = warnings };
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------------------------------------------------------------- Charon (Editor attachment)

    /// <summary>
    /// A JSON-RPC method the plugin has no handler for yet - literally any method other than
    /// "keepalive" currently does (see HadesBoot.HandleRequest), because none of the ~50
    /// Editor-dependent tools exist yet. That is exactly what makes it a useful probe: "keepalive"
    /// is deliberately answered on the plugin's background I/O thread and so proves nothing about
    /// the MAIN thread (see HadesClient's class doc comment), while everything else - including
    /// this - is dispatched through MainThreadPump and only answered once EditorApplication.update
    /// actually runs. A prompt reply (even an error reply) proves the main thread is draining its
    /// queue; silence past the timeout is what "busy" means here.
    /// </summary>
    const string BusyProbeMethod = "hades/mainThreadProbe";

    /// <summary>
    /// Reports one project's live Editor attachment for hades_charon_status - see
    /// <see cref="CharonStatus"/> for the three states this distinguishes. No registration in the
    /// editor registry at all means "not attached", decided synchronously - but still worth one
    /// file read: <see cref="Editors.PluginInstaller.InstalledPluginVersion"/> against this
    /// project's own path, so <see cref="CharonStatus.PluginVersionOnDisk"/> can tell "plugin
    /// installed, Unity has not (re)connected" apart from "nothing installed at all" (see that
    /// property's own doc comment). A registration answers the hello-derived fields immediately -
    /// no round trip needed, they were sent at connect time - and only <see
    /// cref="CharonStatus.Busy"/> needs one, via <see cref="BusyProbeMethod"/>. Returns null only
    /// when <paramref name="productGuid"/> itself is unknown to Hades - same
    /// null-means-unknown-project convention as <see cref="GetMemorySummary"/>.
    /// </summary>
    public async Task<CharonStatus?> GetCharonStatus(string productGuid)
    {
        if (_store.Get(productGuid) is not { } project) return null;

        var editor = _editorRegistry.Get(productGuid);
        if (editor is null)
        {
            return new CharonStatus
            {
                Attached = false,
                Busy = false,
                PluginVersionOnDisk = PluginInstaller.InstalledPluginVersion(project.Path),
            };
        }

        var responsive = await MainThreadIsResponsiveAsync(editor.Session, CharonProbeTimeout).ConfigureAwait(false);

        return new CharonStatus
        {
            Attached = true,
            Busy = !responsive,
            UnityVersion = editor.Hello.UnityVersion,
            ProjectPath = editor.Hello.ProjectPath,
            ProcessId = editor.Hello.ProcessId,
            ConnectionAge = DateTimeOffset.UtcNow - editor.ConnectedAtUtc,
            PluginVersion = editor.Hello.PluginVersion,
        };
    }

    /// <summary>
    /// True once <see cref="BusyProbeMethod"/> gets ANY reply - success or error, the content is
    /// irrelevant, only that the main thread produced one - within <paramref name="timeout"/>.
    /// False for every failure to confirm that within the window: a timeout (the ordinary "busy"
    /// case), or the session ending mid-probe (a race with disconnect - the NEXT call will find no
    /// registration at all and correctly report not-attached instead). A bare catch is deliberate
    /// here: every failure mode collapses to the same "not confirmed responsive right now" answer,
    /// so there is nothing for a caller to do differently based on which one occurred.
    /// </summary>
    static async Task<bool> MainThreadIsResponsiveAsync(EditorSession session, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await session.SendRequestAsync(BusyProbeMethod, cancellationToken: cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- Memory

    /// <summary>
    /// Every top-level authored document under memory/ (never memory/proposals/ - see
    /// <see cref="Memory.MemoryProposals"/>'s class doc comment for why that boundary matters),
    /// with its size and last-reviewed date. Returns null when the project itself is unknown -
    /// same defensive null-means-unknown-project convention as <see cref="Summary"/>. A project
    /// that has never written any memory reports <see cref="MemorySummary.HasMemory"/> false with
    /// an empty document list rather than throwing - "nothing recorded yet" is the ordinary state
    /// for a new project, not an error condition.
    /// </summary>
    public MemorySummary? GetMemorySummary(string productGuid)
    {
        if (_store.Get(productGuid) is null) return null;

        var documents = new List<MemoryDocumentInfo>();
        var memoryDir = paths.MemoryDir(productGuid);

        if (Directory.Exists(memoryDir))
        {
            foreach (var path in Directory.EnumerateFiles(memoryDir, "*.md").OrderBy(p => p, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (_memory.Read(productGuid, name) is not { } file) continue;

                documents.Add(new MemoryDocumentInfo
                {
                    Name = name,
                    SizeBytes = new FileInfo(path).Length,
                    LastReviewed = file.Frontmatter.GetValueOrDefault("last_reviewed"),
                });
            }
        }

        return new MemorySummary { HasMemory = documents.Count > 0, Documents = documents };
    }

    /// <summary>
    /// Ranked full-text search over authored memory documents - see <see cref="Memory.MemoryIndex"/>.
    /// The index is resynced from disk on every call before searching: a memory corpus is a
    /// handful of small files, so the cost is negligible, and resyncing is what guarantees a
    /// result reflects whatever a human most recently saved with a text editor rather than a stale
    /// snapshot from the last time this project's index happened to be touched. Pattern-search
    /// shape like <see cref="Search"/>: an unknown project just means an empty list.
    /// </summary>
    public IReadOnlyList<Memory.MemorySearchHit> RecallMemory(string productGuid, string query, int limit = 10)
    {
        if (_store.Get(productGuid) is null) return [];

        using var index = OpenMemoryIndex(productGuid);
        index.SyncFromDirectory(paths.MemoryDir(productGuid));
        return index.Search(query, limit);
    }

    /// <summary>
    /// Writes a new proposal under memory/proposals/ - never an authored document. See
    /// <see cref="Memory.MemoryProposals"/> for the boundary this maintains and the validation
    /// <paramref name="targetFile"/> is subject to. Returns null when the project itself is
    /// unknown - same convention as <see cref="GetMemorySummary"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="targetFile"/> is null/blank or is not a
    /// plain document name.</exception>
    public Memory.MemoryProposal? ProposeMemoryUpdate(string productGuid, string targetFile, string content, string rationale)
    {
        if (_store.Get(productGuid) is null) return null;

        return _proposals.Write(productGuid, targetFile, content, rationale, DateTimeOffset.UtcNow);
    }

    /// <summary>Backtick-quoted script paths inside a markdown code span, e.g.
    /// `` `Assets/Scripts/Foo.cs` `` - see <see cref="ValidateMemory"/>.</summary>
    static readonly Regex ScriptReferencePattern = new(@"`((?:Assets|Packages)/[^`\r\n]+\.cs)`", RegexOptions.Compiled);

    /// <summary>
    /// Cross-checks authored memory against the live graph: every backtick-quoted script path
    /// mentioned in a document's body (e.g. `` `Assets/Scripts/Foo.cs` ``) that no longer resolves
    /// in the graph is reported, once per (document, path) pair. HONEST LIMITATION, the same shape
    /// as <see cref="Graph.GraphDatabase.OrphanScripts"/>'s own caveat: only an explicit,
    /// backtick-quoted project-relative .cs path is recognised - a bare class name or a prose
    /// mention is not detected, which keeps false positives at zero at the cost of missing some
    /// real mentions. Read-only: this only ever reads memory documents and queries the graph, never
    /// writes - it reports drift, a human decides what to do about it. Pattern-search shape like
    /// <see cref="Search"/>: an unknown project just means an empty list.
    /// </summary>
    public IReadOnlyList<MemoryValidationFinding> ValidateMemory(string productGuid, int limit = 100)
    {
        if (_store.Get(productGuid) is null) return [];

        var memoryDir = paths.MemoryDir(productGuid);
        if (!Directory.Exists(memoryDir)) return [];

        using var database = OpenGraph(productGuid);
        var findings = new List<MemoryValidationFinding>();

        foreach (var path in Directory.EnumerateFiles(memoryDir, "*.md").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (_memory.Read(productGuid, name) is not { } file) continue;

            foreach (var scriptPath in ScriptReferencePattern.Matches(file.Body)
                         .Select(m => m.Groups[1].Value)
                         .Distinct(StringComparer.Ordinal))
            {
                if (database.GuidForPath(scriptPath) is null)
                    findings.Add(new MemoryValidationFinding { Document = name, ScriptPath = scriptPath });
            }
        }

        return findings.Take(Math.Clamp(limit, 1, MaxValidateMemoryFetch)).ToList();
    }

    /// <summary>The largest <c>limit</c> validate_memory documents as its own maximum (MemoryTools.
    /// ValidateMemory: "Maximum findings to return (1-500, default 100)") - what a CALLER may
    /// legitimately request. Deliberately kept separate from <see cref="MaxValidateMemoryFetch"/>,
    /// the ceiling actually applied to the <c>Take</c> above - conflating the two was exactly
    /// <see cref="Graph.GraphDatabase.MaxSearchLimit"/>'s own pre-fix defect (see that constant's
    /// doc comment for the general mechanism), reproduced here: this method kept a single clamp at
    /// 500 - the SAME value as the documented max - so MemoryTools.ValidateMemory's own "limit + 1"
    /// truncation-detection sentinel was silently discarded at exactly limit=500, and `truncated`
    /// could never read true no matter how many real findings existed beyond it.</summary>
    const int MaxValidateMemoryLimit = 500;

    /// <summary>One more than <see cref="MaxValidateMemoryLimit"/>, so a caller AT the documented
    /// maximum - whose own limit+1 sentinel becomes exactly <see cref="MaxValidateMemoryLimit"/> + 1
    /// - still gets an honest <c>truncated</c> answer. Mirrors <see
    /// cref="Graph.GraphDatabase.MaxSearchFetch"/> exactly; see that constant's own doc comment for
    /// why the extra one is what makes the sentinel trick work at the boundary, not just below it.</summary>
    const int MaxValidateMemoryFetch = MaxValidateMemoryLimit + 1;

    /// <summary>
    /// Reads one authored memory document directly - the Control API's "read one" surface (Plan 11
    /// Task 6; every existing MCP-facing method above only ever summarizes, searches, or validates
    /// across every document, never returns one's full content). Null when the project is unknown
    /// OR the document does not exist - same collapsed-null convention as every other method in
    /// this region; a caller that has already resolved <paramref name="productGuid"/> against
    /// <see cref="KnownProjects"/> (as every Control endpoint does before reaching here) only ever
    /// observes the second case.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a safe basename.</exception>
    public Memory.MemoryFile? ReadMemoryDocument(string productGuid, string name) =>
        _store.Get(productGuid) is null ? null : _memory.Read(productGuid, name);

    /// <summary>
    /// Writes (creating or overwriting) one authored memory document directly - the Control API's
    /// "write one" surface (Plan 11 Task 6): a HUMAN, via the shell's own memory editor, saving a
    /// document they opened and edited. Distinct from <see cref="ProposeMemoryUpdate"/>, which is
    /// the only writer <see cref="Hades.Server.Mcp.MemoryTools"/> (an AGENT) can reach - see
    /// <see cref="Memory.MemoryProposals"/>'s own class doc comment for why that boundary matters.
    /// </summary>
    /// <returns>False when the project is unknown; true once written.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a safe basename.</exception>
    public bool WriteMemoryDocument(string productGuid, string name, string content)
    {
        if (_store.Get(productGuid) is null) return false;

        _memory.Write(productGuid, name, content);
        return true;
    }

    /// <summary>Every proposal for this project, newest first - the Control API's proposal-queue
    /// surface (Plan 11 Task 6, spec #3 §3.4's Accept/Dismiss/Defer). Empty when the project is
    /// unknown or nothing has ever been proposed.</summary>
    public IReadOnlyList<Memory.MemoryProposalInfo> ListMemoryProposals(string productGuid) =>
        _store.Get(productGuid) is null ? [] : _proposals.List(productGuid);

    /// <summary>One proposal by its plain basename. Null when the project is unknown or the
    /// proposal does not exist.</summary>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is not a safe basename.</exception>
    public Memory.MemoryProposalInfo? ReadMemoryProposal(string productGuid, string fileName) =>
        _store.Get(productGuid) is null ? null : _proposals.Read(productGuid, fileName);

    /// <summary>Rewrites one proposal's status (e.g. "accepted", "deferred") - never deletes it.
    /// See <see cref="Memory.MemoryProposals.SetStatus"/>'s own doc comment.</summary>
    /// <returns>False when the project or the proposal is unknown.</returns>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is not a safe basename.</exception>
    public bool SetMemoryProposalStatus(string productGuid, string fileName, string status) =>
        _store.Get(productGuid) is not null && _proposals.SetStatus(productGuid, fileName, status);

    /// <summary>Deletes one proposal file - the ONLY memory action anywhere in this class that
    /// deletes anything, and only when its own caller (the Control API's confirmed Dismiss action,
    /// Plan 11 Task 6 - Hades.Server never referenced from here, see Hades.Core.csproj's
    /// EnsureHeadless guard) explicitly calls it.</summary>
    /// <returns>False when the project or the proposal is unknown.</returns>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is not a safe basename.</exception>
    public bool DeleteMemoryProposal(string productGuid, string fileName) =>
        _store.Get(productGuid) is not null && _proposals.Delete(productGuid, fileName);

    Memory.MemoryIndex OpenMemoryIndex(string productGuid)
    {
        paths.EnsureProjectDir(productGuid);
        return Memory.MemoryIndex.Open(paths.MemoryIndexPath(productGuid));
    }

    GraphDatabase OpenGraph(string productGuid)
    {
        paths.EnsureProjectDir(productGuid);
        return GraphDatabase.Open(paths.GraphDb(productGuid));
    }
}
