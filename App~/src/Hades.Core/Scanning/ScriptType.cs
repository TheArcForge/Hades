namespace Hades.Core.Scanning;

/// <summary>One type declaration found in a C# file.</summary>
public sealed record ScriptType
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public string? Namespace { get; init; }
    public int Line { get; init; }
    public IReadOnlyList<string> BaseTypes { get; init; } = [];
}
