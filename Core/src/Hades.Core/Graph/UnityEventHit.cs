namespace Hades.Core.Graph;

/// <summary>One UnityEvent field, anywhere in the project, with at least one WIRED persistent-call
/// listener - see <see cref="GraphDatabase.FindUnityEvents"/> for why "wired" (a non-null target)
/// is the only case the graph can see at all.</summary>
public sealed record UnityEventHit
{
    /// <summary>The prefab or scene the component lives in.</summary>
    public required string Path { get; init; }

    /// <summary>The component's own fileId within <see cref="Path"/>.</summary>
    public required long FileId { get; init; }

    /// <summary>The UnityEvent field's own name, e.g. "m_OnClick" - <see cref="GraphDatabase.FindUnityEvents"/>'s
    /// fixed persistent-call suffix stripped off.</summary>
    public required string EventField { get; init; }

    /// <summary>How many wired listeners this event field has - a LOWER BOUND, not always exact:
    /// see <see cref="GraphDatabase.FindUnityEvents"/>'s "HONEST LIMITATION #2" for why two
    /// listeners targeting the same object collapse into one count.</summary>
    public required int ListenerCount { get; init; }
}
