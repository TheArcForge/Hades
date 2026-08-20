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
    /// True once <see cref="ProjectStore.Remove"/> has deregistered this project - see that
    /// method's own doc comment. Deliberately not a deletion of anything: <see cref="ProjectStore.All"/>
    /// excludes a removed project, but its project.json (and every derived/authored file under
    /// its app-storage directory - graph.db, memory/, ...) is untouched. Defaults to false so
    /// every pre-existing project.json without this key (written before Plan 11 Task 3) decodes
    /// as an ordinary, active project.
    /// </summary>
    public bool Removed { get; init; }
}
