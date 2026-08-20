namespace Hades.Core.Unity;

/// <summary>
/// One entry in a PrefabInstance's m_Modifications: which object it targets, which property it
/// changes, and either a scalar value or an object reference.
/// </summary>
public sealed record UnityModification
{
    public required UnityReference Target { get; init; }
    public required string PropertyPath { get; init; }

    /// <summary>The scalar value. Empty (never null) when the override sets a reference —
    /// Unity writes "value: " with nothing after it in that case.</summary>
    public required string Value { get; init; }

    /// <summary>
    /// Non-null only when this override rewires a reference — 792 of 44,576 in the measured
    /// corpus. These are the only overrides worth putting in a reference graph; the other
    /// 43,784 set scalars and would inflate it ~55x for nothing.
    /// </summary>
    public UnityReference? ObjectReference { get; init; }

    public bool IsReferenceOverride => ObjectReference is not null;
}
