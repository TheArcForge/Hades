namespace Hades.Core.Graph;

/// <summary>One (file, script) pair where the file has a `references` edge to a script whose
/// name matched a search pattern — see <see cref="GraphDatabase.ComponentsUsingPattern"/>.</summary>
public sealed record ComponentUsage
{
    /// <summary>The prefab or scene doing the referencing.</summary>
    public required string ComponentPath { get; init; }

    public required string ScriptName { get; init; }
    public required string ScriptPath { get; init; }
}
