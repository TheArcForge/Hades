namespace Hades.Core.Reading;

/// <summary>
/// A GUID-valued reference as <see cref="ReadThrough.GetMaterialProperties"/> resolves it: the raw
/// guid the material file itself names, and the project-relative path it resolves to, when Hades
/// can find an asset owning that guid anywhere under a scan root. <see cref="Resolved"/> is false
/// far more often than not - most materials reference a built-in or package shader, which has no
/// file under any scan root Hades walks (see ProjectWalker's own doc comment on why registry
/// packages are excluded) - and that is a normal, common state, not a broken one.
/// </summary>
public sealed record AssetReference
{
    public string? Guid { get; init; }
    public string? Path { get; init; }
    public required bool Resolved { get; init; }
}

/// <summary>One texture property slot on a material, e.g. "_BaseMap" - only ever reported when
/// something is actually assigned; an empty slot ({fileID: 0}, Unity's own "nothing assigned"
/// convention) carries no information about the material's configuration and is omitted.</summary>
public sealed record MaterialTexture
{
    public required string Property { get; init; }
    public string? Guid { get; init; }
    public string? Path { get; init; }
    public required bool Resolved { get; init; }
}

/// <summary>
/// One material's shader and properties, as <c>material_get_properties</c> reports them - read
/// straight from the .mat file, no graph involved beyond the one-shot GUID-to-path search
/// <see cref="ReadThrough.ResolveGuidsToPaths"/> does for the shader and every set texture.
/// Floats and colors are reported by name, raw (a color's r/g/b/a are strings, exactly as
/// <see cref="ReadThrough.GetComponentProperties"/> already leaves every scalar uninterpreted)
/// rather than parsed to numbers Hades would have to get the formatting of back exactly right.
/// </summary>
public sealed record MaterialProperties
{
    public required string Path { get; init; }
    public required AssetReference Shader { get; init; }
    public required IReadOnlyDictionary<string, string> Floats { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Colors { get; init; }
    public required IReadOnlyList<MaterialTexture> Textures { get; init; }
}

/// <summary>One state's m_Motion reference, as <see cref="ReadThrough.GetAnimatorController"/>
/// resolves it - either an external asset (an .anim file, <see cref="Path"/> resolved from its
/// guid when the asset is found under a scan root) or a same-file sub-document (a BlendTree, or
/// - degraded but not thrown on - some other type a hand-edited file might reference), named by
/// <see cref="SubAssetName"/> instead. Never both. <see cref="IsBlendTree"/> is the "use `describe`
/// to see inside" signal DESIGN-05 §6.3 Tier 2 promises: this offline reader intentionally goes no
/// further than naming the sub-document, since a blend tree's own contents need a live Editor
/// (animation_apply's `describe` op) to read at all - see that design's §6.2 for why.</summary>
public sealed record AnimatorMotionInfo
{
    public string? Path { get; init; }
    public string? SubAssetName { get; init; }
    public required bool IsBlendTree { get; init; }
}

/// <summary>One state in an Animator Controller's state machine.</summary>
public sealed record AnimatorStateInfo
{
    public required long FileId { get; init; }
    public required string Name { get; init; }
    public required bool IsDefaultState { get; init; }

    /// <summary>The layer (by name) whose state machine directly owns this state - null when no
    /// layer's own m_ChildStates claims it (not expected for a well-formed controller, degrades
    /// like <see cref="AnimatorTransitionInfo.SourceState"/> does for the same reason).</summary>
    public string? Layer { get; init; }

    /// <summary>Null when the state has no motion assigned (m_Motion: {fileID: 0}) - a real,
    /// common state for a freshly added state, not a parse gap.</summary>
    public AnimatorMotionInfo? Motion { get; init; }
}

/// <summary>One layer in an Animator Controller, by name only - DESIGN-05 §6.3 Tier 2 scope is
/// "what layers, what parameters, what states/transitions, which motion" - structure, not the
/// layer's own weight/blending/mask/ikPass, which stay live-only (`describe`) for now.</summary>
public sealed record AnimatorLayerInfo
{
    public required string Name { get; init; }
}

/// <summary>One parameter declared on an Animator Controller. <see cref="Type"/> is Unity's own
/// numeric AnimatorControllerParameterType left uninterpreted (1=Float, 3=Int, 4=Bool, 9=Trigger),
/// like every other raw enum <see cref="ReadThrough"/> returns elsewhere (see
/// <see cref="AnimatorConditionInfo.ConditionMode"/>) - and <see cref="DefaultValue"/> is whichever
/// of m_DefaultFloat/m_DefaultInt/m_DefaultBool the file actually carries for that type, also raw.</summary>
public sealed record AnimatorParameterInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string DefaultValue { get; init; }
}

/// <summary>
/// One transition between two states (or from the special "Any State" pseudo-state) in an
/// Animator Controller. <see cref="SourceState"/> is null only when neither a state's own
/// m_Transitions nor a state machine's m_AnyStateTransitions claims this transition - not expected
/// in a well-formed controller, but read-through degrades to null rather than throwing when a
/// hand-edited file does not fit the shape it expects. <see cref="DestinationState"/> is null for
/// an exit transition (m_DstState: {fileID: 0}, m_IsExit: 1).
/// </summary>
public sealed record AnimatorTransitionInfo
{
    public required long FileId { get; init; }
    public string? SourceState { get; init; }
    public string? DestinationState { get; init; }
    public required IReadOnlyList<AnimatorConditionInfo> Conditions { get; init; }
}

/// <summary>One condition gating a transition - a parameter name, Unity's own numeric
/// AnimatorConditionMode (left uninterpreted, like every other raw scalar ReadThrough returns),
/// and the threshold it compares against.</summary>
public sealed record AnimatorConditionInfo
{
    public required string Parameter { get; init; }
    public required string ConditionMode { get; init; }
    public required string Threshold { get; init; }
}

/// <summary>States and transitions from one .controller file, across every layer's state
/// machine - what <c>animation_get_controller</c> reports.</summary>
public sealed record AnimatorControllerInfo
{
    public required string Path { get; init; }
    public required IReadOnlyList<AnimatorStateInfo> States { get; init; }
    public required IReadOnlyList<AnimatorTransitionInfo> Transitions { get; init; }
    public required IReadOnlyList<AnimatorLayerInfo> Layers { get; init; }
    public required IReadOnlyList<AnimatorParameterInfo> Parameters { get; init; }
}

/// <summary>
/// Which render pipeline the project is configured to use, as <c>analyze_render_pipeline</c>
/// reports it - "Built-in", "URP", "HDRP", or "unknown" when GraphicsSettings names a custom
/// pipeline Hades cannot positively identify (a third-party SRP, an asset it cannot resolve or
/// read, or a URP/HDRP version whose asset script does not match the known guid). Guessing from a
/// partial match (e.g. assuming "any custom pipeline is URP") is deliberately avoided - "unknown"
/// is the honest answer when the evidence does not clear that bar.
/// </summary>
public sealed record RenderPipelineInfo
{
    public required string Pipeline { get; init; }

    /// <summary>The custom render pipeline asset's project-relative path, whenever Hades resolved
    /// one - null only for "Built-in" (nothing configured) or a guid that resolved to no file at
    /// all. Set even when <see cref="Pipeline"/> is "unknown": an asset that was found but not
    /// identifiable as URP or HDRP is still worth reporting, not hiding.</summary>
    public string? PipelineAssetPath { get; init; }
}
