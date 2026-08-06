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
}

/// <summary>
/// One project's live Editor attachment, as reported by hades_charon_status - see
/// <see cref="ProjectService.GetCharonStatus"/>. Three states, not two:
///  - No registration in <see cref="EditorRegistry"/> at all: <see cref="Attached"/> false,
///    <see cref="Busy"/> false, every other field null. "Not attached."
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

    /// <summary>Deregisters a project - see <see cref="ProjectStore.Remove"/>'s own doc comment
    /// for exactly what "deregister" means (nothing on disk is ever deleted, least of all
    /// authored memory). Returns false when <paramref name="productGuid"/> is not, or was never,
    /// known.</summary>
    public bool RemoveProject(string productGuid) => _store.Remove(productGuid);

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

        if (project is not null && _memoryImported.TryAdd(project.ProductGuid, 0))
        {
            _memory.ImportFromArcforge(project.ProductGuid, projectRoot);
        }

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

    void Reindex(UnityProject project)
    {
        using var database = OpenGraph(project.ProductGuid);
        ScriptIndexer.IndexProject(project.Path, database);
        AssetIndexer.IndexProject(project.Path, database);

        // Record what everything looked like, so the next sweep can tell what moved.
        var sweep = ProjectSweeper.Sweep(project.Path, database);
        database.UpsertFileState(ProjectSweeper.StateFor(project.Path, sweep.Added.Concat(sweep.Changed)));

        _lastIndexed[project.ProductGuid] = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Brings the graph up to date by indexing only what differs from the recorded state. Returns
    /// the sweep so a caller can log or act on what moved; a project with no changes costs one
    /// sweep and nothing else.
    /// </summary>
    public SweepResult? SyncChanges(string productGuid)
    {
        if (_store.Get(productGuid) is not { } project) return null;

        using var database = OpenGraph(productGuid);
        var sweep = ProjectSweeper.Sweep(project.Path, database);

        if (!sweep.AnythingChanged) return sweep;

        // Deletions first, and by explicit path — never by sweeping a partial visited-set.
        foreach (var deleted in sweep.Deleted) database.DeleteNodesForPath(deleted);

        var toIndex = sweep.NeedsIndexing;
        if (toIndex.Count > 0)
        {
            ScriptIndexer.IndexFiles(project.Path, database, toIndex);
            AssetIndexer.IndexFiles(project.Path, database, toIndex);
            database.UpsertFileState(ProjectSweeper.StateFor(project.Path, toIndex));
        }

        _lastIndexed[productGuid] = DateTimeOffset.UtcNow;
        return sweep;
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
    /// Everything <paramref name="rootPath"/> depends on, walking `references` edges outward.
    /// Returns null when the project or the root path itself is unknown to the graph — mirrors
    /// <see cref="FindReferencesTo"/>'s null-means-unknown-path convention, including its use of
    /// <see cref="GraphDatabase.GuidForPath"/> as the existence check, even though the walk
    /// itself only ever needs paths, not the root's own GUID.
    /// </summary>
    public IReadOnlyList<DependencyHit>? TraceDependencies(string productGuid, string rootPath, int maxDepth = 3)
    {
        if (_store.Get(productGuid) is null) return null;

        using var database = OpenGraph(productGuid);
        if (database.GuidForPath(rootPath) is null) return null;

        return database.TraceDependencies(rootPath, maxDepth);
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
    /// run, and reports the node count immediately before and after. Returns null when the
    /// project is unknown.
    /// </summary>
    public RebuildResult? RebuildGraph(string productGuid)
    {
        if (_store.Get(productGuid) is not { } project) return null;

        var before = Summary(productGuid)?.TotalNodes ?? 0;
        Reindex(project);
        var after = Summary(productGuid)?.TotalNodes ?? 0;

        return new RebuildResult { NodesBefore = before, NodesAfter = after };
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
    /// editor registry at all means "not attached", decided synchronously. A registration answers
    /// the hello-derived fields immediately - no round trip needed, they were sent at connect time
    /// - and only <see cref="CharonStatus.Busy"/> needs one, via <see cref="BusyProbeMethod"/>.
    /// Returns null only when <paramref name="productGuid"/> itself is unknown to Hades - same
    /// null-means-unknown-project convention as <see cref="GetMemorySummary"/>.
    /// </summary>
    public async Task<CharonStatus?> GetCharonStatus(string productGuid)
    {
        if (_store.Get(productGuid) is null) return null;

        var editor = _editorRegistry.Get(productGuid);
        if (editor is null) return new CharonStatus { Attached = false, Busy = false };

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

        return findings.Take(Math.Clamp(limit, 1, 500)).ToList();
    }

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
