namespace Hades.Core.Projects;

/// <summary>A Unity project Hades knows about. Identity is <see cref="ProductGuid"/>; Path is a hint.</summary>
public sealed record UnityProject
{
    public required string ProductGuid { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public string? UnityVersion { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>
    /// When an index of this project last COMPLETED, or null if one never has.
    ///
    /// <para>Persisted, and that is the whole point. This used to live only in a per-process
    /// dictionary in <see cref="ProjectService"/>, which made "has this project ever been indexed"
    /// unanswerable after a restart - and the control API resolved that unknown as "it must be
    /// indexing right now", so a freshly launched Hades reported every project as permanently
    /// indexing: a blue tray icon and an "Indexing X…" headline over a complete graph with nothing
    /// running. Whether a graph was built is a fact about the graph on disk, so it belongs on disk
    /// beside it.</para>
    ///
    /// <para>Written only on successful completion, so an index that was interrupted - a killed
    /// core, a crash mid-walk - correctly still reads as never finished. Null for every project.json
    /// written before this field existed, which decodes as exactly that: not known to be indexed.</para>
    /// </summary>
    public DateTimeOffset? LastIndexedUtc { get; init; }

    /// <summary>
    /// True once <see cref="ProjectStore.Remove"/> has deregistered this project - see that
    /// method's own doc comment. Deliberately not a deletion of anything: <see cref="ProjectStore.All"/>
    /// excludes a removed project, but its project.json (and every derived/authored file under
    /// its app-storage directory - graph.db, memory/, ...) is untouched. Defaults to false so
    /// every pre-existing project.json without this key (written before Plan 11 Task 3) decodes
    /// as an ordinary, active project.
    /// </summary>
    public bool Removed { get; init; }
}
