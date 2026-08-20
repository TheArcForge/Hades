namespace Hades.Core.Graph;

/// <summary>One component anywhere in the project whose resolved type name matched a search
/// pattern — see <see cref="GraphDatabase.ComponentsMatching"/>.</summary>
public sealed record ComponentMatch
{
    /// <summary>The prefab or scene the component lives in.</summary>
    public required string Path { get; init; }

    /// <summary>The component's own fileId within <see cref="Path"/> — what component_get_all /
    /// component_get_property / component_list_properties address it by.</summary>
    public required long FileId { get; init; }

    /// <summary>The Unity class name for a builtin component (e.g. "Rigidbody"), or the resolved
    /// script class name for a MonoBehaviour.</summary>
    public required string TypeName { get; init; }
}
