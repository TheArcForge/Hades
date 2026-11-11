using System.ComponentModel;
using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Reading;
using Hades.Core.Unity;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Hades.Server.Mcp;

// ---------------------------------------------------------------- inspect_asset result shapes

public sealed record InspectHierarchyNodeResult
{
    [JsonPropertyName("fileId")] public required long FileId { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("sourcePrefabGuid")] public string? SourcePrefabGuid { get; init; }
    [JsonPropertyName("components")] public required IReadOnlyList<string> Components { get; init; }
    [JsonPropertyName("children")] public required IReadOnlyList<InspectHierarchyNodeResult> Children { get; init; }
}

public sealed record InspectHierarchyResult
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("roots")] public required IReadOnlyList<InspectHierarchyNodeResult> Roots { get; init; }

    /// <summary>True when more GameObjects exist in the file than <see cref="TotalReturned"/> - see
    /// <see cref="InspectTool.TruncateNodes"/>. Honest, not silent: a caller must know some of the
    /// tree is missing rather than assuming this is the whole file.</summary>
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }

    [JsonPropertyName("totalReturned")] public required int TotalReturned { get; init; }
}

public sealed record InspectAssetReferenceResult
{
    [JsonPropertyName("guid")] public string? Guid { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("resolved")] public required bool Resolved { get; init; }
}

public sealed record InspectMaterialTextureResult
{
    [JsonPropertyName("property")] public required string Property { get; init; }
    [JsonPropertyName("guid")] public string? Guid { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("resolved")] public required bool Resolved { get; init; }
}

public sealed record InspectMaterialResult
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("shader")] public required InspectAssetReferenceResult Shader { get; init; }
    [JsonPropertyName("floats")] public required IReadOnlyDictionary<string, string> Floats { get; init; }
    [JsonPropertyName("colors")] public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Colors { get; init; }
    [JsonPropertyName("textures")] public required IReadOnlyList<InspectMaterialTextureResult> Textures { get; init; }
}

public sealed record InspectAnimatorMotionResult
{
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("subAssetName")] public string? SubAssetName { get; init; }
    [JsonPropertyName("isBlendTree")] public required bool IsBlendTree { get; init; }
}

public sealed record InspectAnimatorStateResult
{
    [JsonPropertyName("fileId")] public required long FileId { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("isDefaultState")] public required bool IsDefaultState { get; init; }
    [JsonPropertyName("layer")] public string? Layer { get; init; }

    /// <summary>Null when the state has no motion assigned. When
    /// <see cref="InspectAnimatorMotionResult.IsBlendTree"/> is true, this offline reader names the
    /// sub-document but does not descend into it - use animation_apply's 'describe' op (a live
    /// Editor read) to see a blend tree's own contents, DESIGN-05 §6.3 Tier 2/Tier 1 split.</summary>
    [JsonPropertyName("motion")] public InspectAnimatorMotionResult? Motion { get; init; }
}

public sealed record InspectAnimatorLayerResult
{
    [JsonPropertyName("name")] public required string Name { get; init; }
}

public sealed record InspectAnimatorParameterResult
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("defaultValue")] public required string DefaultValue { get; init; }
}

public sealed record InspectAnimatorConditionResult
{
    [JsonPropertyName("parameter")] public required string Parameter { get; init; }
    [JsonPropertyName("conditionMode")] public required string ConditionMode { get; init; }
    [JsonPropertyName("threshold")] public required string Threshold { get; init; }
}

public sealed record InspectAnimatorTransitionResult
{
    [JsonPropertyName("fileId")] public required long FileId { get; init; }
    [JsonPropertyName("sourceState")] public string? SourceState { get; init; }
    [JsonPropertyName("destinationState")] public string? DestinationState { get; init; }
    [JsonPropertyName("conditions")] public required IReadOnlyList<InspectAnimatorConditionResult> Conditions { get; init; }
}

public sealed record InspectAnimatorControllerResult
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("states")] public required IReadOnlyList<InspectAnimatorStateResult> States { get; init; }
    [JsonPropertyName("transitions")] public required IReadOnlyList<InspectAnimatorTransitionResult> Transitions { get; init; }
    [JsonPropertyName("layers")] public required IReadOnlyList<InspectAnimatorLayerResult> Layers { get; init; }
    [JsonPropertyName("parameters")] public required IReadOnlyList<InspectAnimatorParameterResult> Parameters { get; init; }
}

public sealed record InspectAssetInfoResult
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("guid")] public string? Guid { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
}

// ---------------------------------------------------------------- DESIGN-07: binary asset introspection

public sealed record InspectTexturePlatformResult
{
    [JsonPropertyName("buildTarget")] public required string BuildTarget { get; init; }
    [JsonPropertyName("compressed")] public required bool Compressed { get; init; }
    [JsonPropertyName("compressionLevel")] public string? CompressionLevel { get; init; }
    [JsonPropertyName("crunched")] public required bool Crunched { get; init; }
    [JsonPropertyName("quality")] public int? Quality { get; init; }
    [JsonPropertyName("overridden")] public required bool Overridden { get; init; }
}

public sealed record InspectModelClipResult
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("firstFrame")] public int? FirstFrame { get; init; }
    [JsonPropertyName("lastFrame")] public int? LastFrame { get; init; }
    [JsonPropertyName("loops")] public bool? Loops { get; init; }
}

/// <summary>
/// DESIGN-07 — inspect_asset's fifth "structure" branch, populated whenever
/// <see cref="Unity.ImportedAssetKind.KindForPath"/> recognises the path (alongside, not instead
/// of, <see cref="InspectAssetResult.AssetInfo"/>, which every "anything else" path already got).
/// Tier 1 fields (everything except the "source*" ones) come from the sibling .meta, decoded by
/// <see cref="Unity.MetaIntrospection"/>; Tier 2 ("source*", textures only) is measured straight
/// from the file's own header by <see cref="Unity.ImageHeaderReader"/> — this naming convention
/// IS the tier disclosure (DESIGN-07 §6.2c): no separate mode flag. A field that does not apply to
/// this asset's importer type is simply never populated; <see cref="UnavailableReason"/> explains
/// when TIER 1 AS A WHOLE could not be read at all (no .meta yet, or it fails to parse) — the same
/// message <c>ReadThrough.GetImportSettings</c> itself throws, reused verbatim.
/// </summary>
public sealed record InspectIntrospectionResult
{
    [JsonPropertyName("importer")] public string? Importer { get; init; }

    /// <summary>Set only when Tier 1 as a whole is unavailable (no .meta yet, or it does not
    /// parse) — every field below is then null too. Distinct from <see
    /// cref="SourceUnavailableReason"/>, which is Tier 2's own, independent failure.</summary>
    [JsonPropertyName("unavailableReason")] public string? UnavailableReason { get; init; }

    // -- texture (TextureImporter)
    [JsonPropertyName("textureType")] public string? TextureType { get; init; }

    /// <summary>The import CEILING Unity was configured with, NOT the source image's size — a
    /// 64×64 icon and an 8192×8192 texture both report 2048 here under default settings.
    /// <see cref="SourceWidth"/>/<see cref="SourceHeight"/> are the measured values; they are null
    /// for formats Hades does not read headers from.</summary>
    [JsonPropertyName("maxTextureSize")] public int? MaxTextureSize { get; init; }
    [JsonPropertyName("sRGB")] public bool? SRGB { get; init; }
    [JsonPropertyName("generateMipMaps")] public bool? GenerateMipMaps { get; init; }
    [JsonPropertyName("readWriteEnabled")] public bool? ReadWriteEnabled { get; init; }
    [JsonPropertyName("streamingMipmaps")] public bool? StreamingMipmaps { get; init; }
    [JsonPropertyName("alphaIsTransparency")] public bool? AlphaIsTransparency { get; init; }
    [JsonPropertyName("alphaSource")] public string? AlphaSource { get; init; }
    [JsonPropertyName("compression")] public IReadOnlyList<InspectTexturePlatformResult>? Compression { get; init; }

    /// <summary>Tier 2 — measured straight from the file's own header, never from the extension.
    /// Textures only; null for every other kind and for a texture Hades could not read the header
    /// of (see <see cref="SourceUnavailableReason"/>).</summary>
    [JsonPropertyName("sourceWidth")] public int? SourceWidth { get; init; }
    [JsonPropertyName("sourceHeight")] public int? SourceHeight { get; init; }
    [JsonPropertyName("sourceChannels")] public int? SourceChannels { get; init; }
    [JsonPropertyName("sourceFormat")] public string? SourceFormat { get; init; }
    [JsonPropertyName("sourceUnavailableReason")] public string? SourceUnavailableReason { get; init; }

    // -- model (ModelImporter)
    [JsonPropertyName("rigType")] public string? RigType { get; init; }
    [JsonPropertyName("avatarSetup")] public string? AvatarSetup { get; init; }
    [JsonPropertyName("modelIsReadable")] public bool? ModelIsReadable { get; init; }
    [JsonPropertyName("meshCompression")] public string? MeshCompression { get; init; }
    [JsonPropertyName("materialImportMode")] public string? MaterialImportMode { get; init; }
    [JsonPropertyName("boneCount")] public int? BoneCount { get; init; }

    /// <summary>A LOWER BOUND on the FBX's actual clips, not a census — populated only when clips
    /// were configured in the Inspector (see <see cref="Unity.MetaIntrospection.Decode"/>'s own
    /// doc comment).</summary>
    [JsonPropertyName("clips")] public IReadOnlyList<InspectModelClipResult>? Clips { get; init; }

    // -- audio (AudioImporter)
    [JsonPropertyName("loadType")] public string? LoadType { get; init; }
    [JsonPropertyName("audioCompressionFormat")] public string? AudioCompressionFormat { get; init; }
    [JsonPropertyName("quality")] public string? Quality { get; init; }
    [JsonPropertyName("sampleRateSetting")] public string? SampleRateSetting { get; init; }

    /// <summary>Inert unless <see cref="SampleRateSetting"/> is "OverrideSampleRate" - null
    /// otherwise, never reported as "the sample rate" when it does not apply.</summary>
    [JsonPropertyName("sampleRateOverride")] public int? SampleRateOverride { get; init; }
    [JsonPropertyName("forceToMono")] public bool? ForceToMono { get; init; }
    [JsonPropertyName("normalize")] public bool? Normalize { get; init; }
    [JsonPropertyName("preloadAudioData")] public bool? PreloadAudioData { get; init; }
    [JsonPropertyName("loadInBackground")] public bool? LoadInBackground { get; init; }
    [JsonPropertyName("ambisonic")] public bool? Ambisonic { get; init; }
    [JsonPropertyName("is3D")] public bool? Is3D { get; init; }
}

public sealed record InspectComponentResult
{
    [JsonPropertyName("fileId")] public required long FileId { get; init; }
    [JsonPropertyName("typeName")] public string? TypeName { get; init; }
    [JsonPropertyName("scriptGuid")] public string? ScriptGuid { get; init; }
    [JsonPropertyName("missing")] public required bool Missing { get; init; }
}

public sealed record InspectReferenceResult
{
    [JsonPropertyName("targetFileId")] public required long TargetFileId { get; init; }
    [JsonPropertyName("targetGuid")] public string? TargetGuid { get; init; }
    [JsonPropertyName("isUnset")] public required bool IsUnset { get; init; }
    [JsonPropertyName("isLocal")] public required bool IsLocal { get; init; }
    [JsonPropertyName("resolvedPath")] public string? ResolvedPath { get; init; }
    [JsonPropertyName("resolved")] public required bool Resolved { get; init; }
}

public sealed record InspectEventListenerResult
{
    [JsonPropertyName("eventField")] public required string EventField { get; init; }
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("target")] public required InspectReferenceResult Target { get; init; }
    [JsonPropertyName("targetAssemblyTypeName")] public string? TargetAssemblyTypeName { get; init; }
    [JsonPropertyName("methodName")] public required string MethodName { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("callState")] public required string CallState { get; init; }
    [JsonPropertyName("arguments")] public required object? Arguments { get; init; }
}

/// <summary>
/// One <c>inspect_asset</c> call's result. Exactly one of the depth-specific payloads below is set,
/// selected by <see cref="Depth"/> - see <see cref="InspectTool"/>'s own class doc comment for the
/// four depths and what each populates. Absent (never explicit null) fields are simply omitted from
/// the wire, the same MCP SDK default every other tool in this codebase already relies on.
/// </summary>
public sealed record InspectAssetResult
{
    [JsonPropertyName("path")] public required string Path { get; init; }

    /// <summary>Which depth this result answers: "structure", "components", "properties", or
    /// "value" - see <see cref="InspectTool"/>'s own class doc comment.</summary>
    [JsonPropertyName("depth")] public required string Depth { get; init; }

    /// <summary>depth == "structure" only: the file's own GUID, from its sibling .meta file - null
    /// when not yet imported, exactly like asset_get_info always reported it. Populated on EVERY
    /// depth=="structure" result regardless of which type-specific payload below is also set - see
    /// <see cref="InspectTool"/>'s own class doc comment (Plan 10 Task 6 update) for why this field
    /// exists here at all: Hierarchy/Material/AnimatorController carry no guid of their own, so
    /// without this, asset_get_info's {guid} lookup would be unreachable for a Scene, Prefab,
    /// Material, or AnimatorController path specifically - the exact three/four types most likely
    /// to need identity looked up independently of content.</summary>
    [JsonPropertyName("guid")] public string? Guid { get; init; }

    // depth == "structure", path is a scene or prefab
    [JsonPropertyName("hierarchy")] public InspectHierarchyResult? Hierarchy { get; init; }

    // depth == "structure", path is a material
    [JsonPropertyName("material")] public InspectMaterialResult? Material { get; init; }

    // depth == "structure", path is an animator controller
    [JsonPropertyName("animatorController")] public InspectAnimatorControllerResult? AnimatorController { get; init; }

    // depth == "structure", path is anything else
    [JsonPropertyName("assetInfo")] public InspectAssetInfoResult? AssetInfo { get; init; }

    /// <summary>depth == "structure" only, populated alongside <see cref="AssetInfo"/> (never
    /// instead of it) whenever <see cref="Unity.ImportedAssetKind.KindForPath"/> recognises the
    /// path — a texture, model, or audio clip's decoded importer settings plus (textures only)
    /// measured source dimensions. See <see cref="InspectIntrospectionResult"/>'s own doc comment
    /// for the Tier 1/Tier 2 split. DESIGN-07.</summary>
    [JsonPropertyName("introspection")] public InspectIntrospectionResult? Introspection { get; init; }

    // depth == "components"
    [JsonPropertyName("components")] public IReadOnlyList<InspectComponentResult>? Components { get; init; }

    // depth == "properties": field names plus every UnityEvent field's listeners on this component
    [JsonPropertyName("properties")] public IReadOnlyList<string>? Properties { get; init; }
    [JsonPropertyName("events")] public IReadOnlyList<InspectEventListenerResult>? Events { get; init; }

    // depth == "value": the raw field value, plus resolved reference metadata when it is one
    [JsonPropertyName("value")] public object? Value { get; init; }
    [JsonPropertyName("reference")] public InspectReferenceResult? Reference { get; init; }

    /// <summary>depth == "value" or "properties" only: true when a raw field value below (this
    /// result's own <see cref="Value"/>, or an <see cref="InspectEventListenerResult.Arguments"/>
    /// among <see cref="Events"/>) was too large to return in full and was cut - see
    /// <see cref="InspectTool.BoundValue"/>. E3: unlike the hierarchy result, a single component
    /// field has no caller-supplied 'limit' to honour, so this is reported rather than left
    /// silently unbounded (a ~3MB field previously produced a ~6MB response). Omitted for
    /// "structure"/"components", which carry no field-value payload of their own to bound - the
    /// hierarchy's OWN truncation is already reported separately, under <see cref="Hierarchy"/>.
    /// </summary>
    [JsonPropertyName("truncated")] public bool? Truncated { get; init; }
}

// ---------------------------------------------------------------- find_unset_references result shapes

public sealed record InspectUnsetReferenceResult
{
    [JsonPropertyName("fileId")] public required long FileId { get; init; }
    [JsonPropertyName("objectKind")] public required string ObjectKind { get; init; }
    [JsonPropertyName("objectName")] public string? ObjectName { get; init; }
    [JsonPropertyName("propertyPath")] public required string PropertyPath { get; init; }
}

public sealed record InspectUnityEventResult
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("fileId")] public required long FileId { get; init; }
    [JsonPropertyName("eventField")] public required string EventField { get; init; }
    [JsonPropertyName("listenerCount")] public required int ListenerCount { get; init; }
}

/// <summary>
/// One <c>find_unset_references</c> call's result. <see cref="Scope"/> says which of the two modes
/// answered it; only the matching one of <see cref="UnsetReferences"/> / <see cref="UnityEvents"/> is
/// set - see <see cref="InspectTool.FindUnsetReferences"/>'s own doc comment for the two modes.
/// </summary>
public sealed record FindUnsetReferencesResult
{
    [JsonPropertyName("scope")] public required string Scope { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("unsetReferences")] public IReadOnlyList<InspectUnsetReferenceResult>? UnsetReferences { get; init; }
    [JsonPropertyName("unityEvents")] public IReadOnlyList<InspectUnityEventResult>? UnityEvents { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
    [JsonPropertyName("totalReturned")] public required int TotalReturned { get; init; }
}

/// <summary>
/// The two read-through "look at one thing closely" tools: <c>inspect_asset</c> (Plan 10 Task 3),
/// replacing InspectionTools' prefab_get_contents/scene_get_hierarchy/component_get_all/component_
/// get_property/component_list_properties, TypedAssetTools' material_get_properties/animation_get_
/// controller, SettingsTools' asset_get_info, and ReferenceTools' reference_get/event_list_listeners
/// (10 tools) - and <c>find_unset_references</c>, replacing ReferenceTools' reference_find_unset and
/// GraphTools' event_find_all (2 tools). All twelve already used exactly the mechanism this composes
/// (<see cref="ReadThrough"/> plus, for the one graph touch each needs, <see cref="ProjectService"/>);
/// nothing here is new capability, only a new argument shape over the same reads. No lease, no
/// mutation, no Editor involved - see <see cref="ToolSupport.SavedStateClause"/>, which every result
/// below carries, exactly as the twelve tools it replaces did.
///
/// <para><b>inspect_asset's four depths.</b> One path, progressively narrowed by which of 'target'
/// (a GameObject's fileId), 'component' (a component's fileId) and 'property' (a field name) are
/// also given - each argument requires every one before it, refused otherwise (see
/// <see cref="InspectAsset"/>'s own validation), the same "narrow one step at a time" shape the
/// results themselves are named for:</para>
/// <list type="bullet">
/// <item><description><b>path only -> "structure".</b> The file's own shape: a GameObject hierarchy
/// for a scene or prefab (<see cref="ReadThrough.GetHierarchy"/>, truncated honestly past 'limit' -
/// see <see cref="TruncateNodes"/>), shader/property values for a material
/// (<see cref="ReadThrough.GetMaterialProperties"/>), states/transitions for an animator controller
/// (<see cref="ReadThrough.GetAnimatorController"/>), or identity for anything else
/// (<see cref="ReadThrough.GetAssetInfo"/>). Dispatched by <see cref="AssetType.FromPath"/>, exactly
/// the classification asset_get_info/asset_find already used. <see cref="InspectAssetResult.Guid"/>
/// (Plan 10 Task 6 update) is ALSO populated on every one of these four branches - a second, small
/// <see cref="ReadThrough.GetAssetInfo"/> call for the hierarchy/material/controller branches
/// (deliberately AFTER their own type-specific read succeeds, not before: that ordering is what
/// keeps a missing file's error the specific, already-tested "no longer on disk" message every
/// read-through entry point shares, rather than shadowing it with GetAssetInfo's own, more generic
/// one), the SAME single call the "anything else" branch always needed anyway. This is
/// what keeps asset_get_info's {guid} lookup reachable for a Scene/Prefab/Material/AnimatorController
/// path specifically, which the hierarchy/material/controller payloads carry no guid of their own to
/// answer.</description></item>
/// <item><description><b>+ target -> "components".</b> That GameObject's components, resolved
/// through the one graph touch <see cref="ProjectService.GetComponents"/> already does (a
/// MonoBehaviour's script guid to its script path, or a clearly-flagged missing=true when it does
/// not resolve - never silently dropped).</description></item>
/// <item><description><b>+ component -> "properties".</b> That component's field names
/// (<see cref="ReadThrough.GetComponentProperties"/>'s keys) PLUS every UnityEvent field's listeners
/// on it (<see cref="ProjectService.GetEventListeners"/>) - event_list_listeners' capability lands
/// here because it takes exactly the same two inputs (path, one component's fileId) as component_
/// list_properties did, so "everything about this one component" is the natural, argument-free home
/// for both at once.</description></item>
/// <item><description><b>+ property -> "value".</b> That one field's raw value
/// (<see cref="ReadThrough.GetComponentProperties"/> again), exactly as component_get_property
/// returned it. When the raw value is itself reference-shaped (a <c>{fileID, guid, type}</c>
/// dictionary), it is ALSO resolved through <see cref="ProjectService.GetReference"/> - reference_
/// get's own capability, merged in rather than requiring a separate call, per the plan's own design:
/// "references resolved to paths". A resolve-time failure on a value that only LOOKED reference-shaped
/// does not fail the call - the raw value above is still the honest, correct answer either
/// way.</description></item>
/// </list>
///
/// <para><b>find_unset_references' two scopes.</b> reference_find_unset and event_find_all answered
/// genuinely different-shaped questions - one file-scoped and read-through, the other project-wide
/// and graph-served - so this keeps that shape rather than inventing a project-wide unset-reference
/// scan (new capability the plan does not ask for) or a file-scoped event scan (ditto). 'path' given
/// scans that one file for unset ({fileID: 0}) references, byte-for-byte
/// <see cref="ReferenceReading.FindUnsetReferences"/>; 'path' omitted (or blank - both mean the same
/// "no path" thing, since a caller who left it out and one who sent whitespace by accident want the
/// same result) instead finds UnityEvents with at least one wired listener across the WHOLE PROJECT,
/// byte-for-byte <see cref="ProjectService.FindUnityEvents"/>.</para>
/// </summary>
[McpServerToolType]
public sealed class InspectTool(ProjectService projects)
{
    const int DefaultHierarchyLimit = 100;
    const int MaxHierarchyLimit = 500;

    /// <summary>
    /// E3: the cap <see cref="BoundValue"/> applies to a single depth="value"/"properties" raw
    /// field - characters for a string, elements for a list, entries for a dictionary - mirroring
    /// the node-count budget <see cref="TruncateNodes"/> already gives the hierarchy result. A
    /// single component field has no caller-supplied 'limit' the way a whole-file hierarchy does,
    /// so without a cap of its own a pathologically large one (a baked mesh's float array, an
    /// embedded text blob) returns wholesale - measured at a ~3MB field producing a ~6MB response.
    /// Deliberately generous: a normal component field (a handful of scalars, a Vector3, a short
    /// list) never comes close.
    /// </summary>
    const int MaxValueUnits = 2000;

    [McpServerTool(Name = "inspect_asset", Title = "Inspect Asset", ReadOnly = true, UseStructuredContent = true)]
    [Description("Reads one named asset from disk, narrowing as more arguments are given. 'path' "
               + "alone returns the file's structure (hierarchy for a scene/prefab, properties for "
               + "a material, states for a controller, or identity otherwise) plus its 'guid'; "
               + "textures/models/audio also return importer 'introspection'. Add 'target' (a "
               + "fileId) for that object's components, 'component' for its fields/listeners, "
               + "'property' for that value - each needs every prior argument. A hierarchy over "
               + "'limit' truncates - re-call with 'target' instead of re-requesting the file."
               + ToolSupport.SavedStateClause)]
    public async Task<InspectAssetResult> InspectAsset(
        [Description("Project-relative asset path, as returned by search_by_name.")] string path,
        [Description("A GameObject's fileId, from the structure result. Requires a prefab or scene path.")] long? target = null,
        [Description("A component's fileId, from the 'target' result. Requires 'target'.")] long? component = null,
        [Description("A field name, from the 'component' result. Requires 'component'.")] string? property = null,
        [Description("Maximum GameObjects in a whole-file hierarchy result (1-500, default 100). Ignored once 'target' is given.")] int limit = DefaultHierarchyLimit,
        [Description("Project handle from hades_status (omit if only one).")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException(
                "inspect_asset needs a 'path' — the project-relative asset path to inspect, e.g. "
                + "{\"path\": \"Assets/Prefabs/Enemy.prefab\"}. search_by_name returns paths in "
                + "exactly this form.");
        }

        if (property is not null && (component is null || target is null))
        {
            throw new McpException(
                "inspect_asset needs 'target' and 'component' before 'property' can narrow further. "
                + "Call inspect_asset with just 'path' to see the structure, then add 'target' to "
                + "see a GameObject's components, then 'component' to see that component's fields.");
        }

        if (component is not null && target is null)
        {
            throw new McpException(
                "inspect_asset needs 'target' — the GameObject's fileId — before 'component' can "
                + "narrow further. Call inspect_asset with 'path' and 'target' to see that "
                + "GameObject's components first.");
        }

        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var unityProject = ResolveProjectPath(productGuid);

        if (property is not null)
            return Guarded(() => ValueResult(productGuid, unityProject, path, component!.Value, property));

        if (component is not null)
            return Guarded(() => PropertiesResult(productGuid, unityProject, path, component.Value));

        if (target is not null)
            return Guarded(() => ComponentsResult(productGuid, path, target.Value));

        return Guarded(() => StructureResult(unityProject, path, limit));
    }

    [McpServerTool(Name = "find_unset_references", Title = "Find Unset References", ReadOnly = true, UseStructuredContent = true)]
    [Description("Finds wiring gaps and UnityEvent listeners across a scope. Given 'path' (a prefab "
               + "or scene), scans that file for every object-reference field set to Unity's null "
               + "reference ({fileID: 0}) - an unassigned GameObject/Component field, for instance. "
               + "Hades cannot tell a deliberately-optional field from a forgotten one, so every "
               + "occurrence is reported. Omit 'path' to instead find UnityEvents with a wired "
               + "listener across the WHOLE PROJECT; 'listenerCount' is a lower bound."
               + ToolSupport.SavedStateClause)]
    public async Task<FindUnsetReferencesResult> FindUnsetReferences(
        [Description("Project-relative prefab or scene path to scan for unset references. Omit (or leave blank) to instead find UnityEvents with wired listeners across the whole project.")] string? path = null,
        [Description("Maximum results to return (1-500, default 100)")] int limit = 100,
        [Description("Project handle from hades_status (omit if only one).")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        var clampedLimit = Math.Clamp(limit, 1, 500);

        if (string.IsNullOrWhiteSpace(path))
        {
            var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
            var found = projects.FindUnityEvents(productGuid, clampedLimit + 1);
            var truncated = found.Count > clampedLimit;
            var hits = found.Take(clampedLimit).Select(h => new InspectUnityEventResult
            {
                Path = h.Path,
                FileId = h.FileId,
                EventField = h.EventField,
                ListenerCount = h.ListenerCount,
            }).ToList();

            return new FindUnsetReferencesResult
            {
                Scope = "project",
                UnityEvents = hits,
                Truncated = truncated,
                TotalReturned = hits.Count,
            };
        }

        var (fileProductGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var unityProject = ResolveProjectPath(fileProductGuid);
        var all = Guarded(() => ReferenceReading.FindUnsetReferences(unityProject, path));
        var limited = all.Take(clampedLimit).ToList();

        return new FindUnsetReferencesResult
        {
            Scope = "file",
            Path = path,
            UnsetReferences = limited.Select(h => new InspectUnsetReferenceResult
            {
                FileId = h.FileId,
                ObjectKind = h.ObjectKind,
                ObjectName = h.ObjectName,
                PropertyPath = h.PropertyPath,
            }).ToList(),
            Truncated = all.Count > limited.Count,
            TotalReturned = limited.Count,
        };
    }

    // ---------------------------------------------------------------- inspect_asset: depth "structure"

    InspectAssetResult StructureResult(string unityProject, string path, int limit)
    {
        var assetType = AssetType.FromPath(path);

        if (assetType is "Scene" or "Prefab")
        {
            // GetHierarchy first, unconditionally - it is what gives a missing file its specific,
            // already-tested "no longer on disk" message (LoadValidatedContent). Guid is read only
            // once that succeeds, so a missing file still fails exactly as it always did, never
            // shadowed by GetAssetInfo's OWN (more generic) not-on-disk message.
            var hierarchy = ReadThrough.GetHierarchy(unityProject, path);
            var guid = ReadThrough.GetAssetInfo(unityProject, path).Guid;

            var remaining = Math.Clamp(limit, 1, MaxHierarchyLimit);
            var budget = remaining;
            var truncated = false;
            var roots = TruncateNodes(hierarchy.Roots, ref remaining, ref truncated);

            return new InspectAssetResult
            {
                Path = path,
                Depth = "structure",
                Guid = guid,
                Hierarchy = new InspectHierarchyResult
                {
                    Path = hierarchy.Path,
                    Roots = roots,
                    Truncated = truncated,
                    TotalReturned = budget - remaining,
                },
            };
        }

        if (assetType == "Material")
        {
            var material = ReadThrough.GetMaterialProperties(unityProject, path);
            var guid = ReadThrough.GetAssetInfo(unityProject, path).Guid;
            return new InspectAssetResult { Path = path, Depth = "structure", Guid = guid, Material = ToMaterialResult(material) };
        }

        if (assetType == "AnimatorController")
        {
            var controller = ReadThrough.GetAnimatorController(unityProject, path);
            var guid = ReadThrough.GetAssetInfo(unityProject, path).Guid;
            return new InspectAssetResult { Path = path, Depth = "structure", Guid = guid, AnimatorController = ToControllerResult(controller) };
        }

        var info = ReadThrough.GetAssetInfo(unityProject, path);
        return new InspectAssetResult
        {
            Path = path,
            Depth = "structure",
            Guid = info.Guid,
            AssetInfo = new InspectAssetInfoResult { Path = info.Path, Guid = info.Guid, Type = info.Type },
            Introspection = ImportedAssetKind.KindForPath(path) is { } kind
                ? BuildIntrospection(unityProject, path, kind)
                : null,
        };
    }

    /// <summary>DESIGN-07 §8.1. Tier 1 failures (no .meta, unparseable .meta) do not fail the
    /// whole inspect_asset call — they degrade to <see
    /// cref="InspectIntrospectionResult.UnavailableReason"/>, reusing
    /// <see cref="ReadThrough.GetImportSettings"/>'s own exception messages verbatim (the same
    /// "no longer on disk" / "not yet imported" register every other read-through failure uses).
    /// Tier 2 (<see cref="ImageHeaderReader"/>) never throws at all — see its own doc comment.
    /// </summary>
    static InspectIntrospectionResult BuildIntrospection(string unityProject, string path, string kind)
    {
        ImageHeaderResult? header = null;
        if (kind == "Texture2D")
        {
            header = ImageHeaderReader.Read(ReadThrough.ResolveAssetPath(unityProject, path));
        }

        MetaIntrospectionResult? decoded = null;
        string? unavailableReason = null;
        try
        {
            decoded = MetaIntrospection.Decode(ReadThrough.GetImportSettings(unityProject, path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException)
        {
            unavailableReason = ex.Message;
        }

        return new InspectIntrospectionResult
        {
            Importer = decoded?.ImporterType,
            UnavailableReason = unavailableReason,

            TextureType = decoded?.TextureType,
            MaxTextureSize = decoded?.MaxTextureSize,
            SRGB = decoded?.SRGB,
            GenerateMipMaps = decoded?.GenerateMipMaps,
            ReadWriteEnabled = decoded?.ReadWriteEnabled,
            StreamingMipmaps = decoded?.StreamingMipmaps,
            AlphaIsTransparency = decoded?.AlphaIsTransparency,
            AlphaSource = decoded?.AlphaSource,
            Compression = decoded?.Compression?.Select(c => new InspectTexturePlatformResult
            {
                BuildTarget = c.BuildTarget,
                Compressed = c.Compressed,
                CompressionLevel = c.CompressionLevel,
                Crunched = c.Crunched,
                Quality = c.Quality,
                Overridden = c.Overridden,
            }).ToList(),

            SourceWidth = header?.Width,
            SourceHeight = header?.Height,
            SourceChannels = header?.Channels,
            SourceFormat = header?.Format,
            SourceUnavailableReason = header?.UnavailableReason,

            RigType = decoded?.RigType,
            AvatarSetup = decoded?.AvatarSetup,
            ModelIsReadable = decoded?.ModelIsReadable,
            MeshCompression = decoded?.MeshCompression,
            MaterialImportMode = decoded?.MaterialImportMode,
            BoneCount = decoded?.BoneCount,
            Clips = decoded?.Clips?.Select(c => new InspectModelClipResult
            {
                Name = c.Name,
                FirstFrame = c.FirstFrame,
                LastFrame = c.LastFrame,
                Loops = c.Loops,
            }).ToList(),

            LoadType = decoded?.LoadType,
            AudioCompressionFormat = decoded?.AudioCompressionFormat,
            Quality = decoded?.Quality,
            SampleRateSetting = decoded?.SampleRateSetting,
            SampleRateOverride = decoded?.SampleRateOverride,
            ForceToMono = decoded?.ForceToMono,
            Normalize = decoded?.Normalize,
            PreloadAudioData = decoded?.PreloadAudioData,
            LoadInBackground = decoded?.LoadInBackground,
            Ambisonic = decoded?.Ambisonic,
            Is3D = decoded?.Is3D,
        };
    }

    /// <summary>
    /// Copies a hierarchy tree in document order, stopping once <paramref name="remaining"/> nodes
    /// have been included - a pre-order, budget-limited walk that preserves the tree shape (a node's
    /// whole subtree is dropped together with it, never leaving an orphaned child behind). Mirrors
    /// <see cref="InspectionTools.ToNode"/>'s field-for-field copy, plus the cap. Whichever nodes are
    /// included stay in the same relative order <see cref="ReadThrough.GetHierarchy"/> itself
    /// returns them in, so a caller narrowing with a fileId from THIS result is always narrowing to
    /// something real, never a node the cap silently invented or reordered.
    /// </summary>
    static List<InspectHierarchyNodeResult> TruncateNodes(
        IReadOnlyList<HierarchyNode> nodes, ref int remaining, ref bool truncated)
    {
        var result = new List<InspectHierarchyNodeResult>();

        foreach (var node in nodes)
        {
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            remaining--;
            var children = TruncateNodes(node.Children, ref remaining, ref truncated);

            result.Add(new InspectHierarchyNodeResult
            {
                FileId = node.FileId,
                Kind = node.Kind,
                Name = node.Name,
                SourcePrefabGuid = node.SourcePrefabGuid,
                Components = node.Components,
                Children = children,
            });
        }

        return result;
    }

    /// <summary>
    /// E3: caps a depth="value"/"properties" raw field to <see cref="MaxValueUnits"/> "units" -
    /// characters for a string, elements for a list, entries for a dictionary - the same
    /// budget-limited-copy idea <see cref="TruncateNodes"/> already applies to a whole-file
    /// hierarchy, mirrored here for the other unbounded payload. Recurses into a list or
    /// dictionary's own elements, so a small container wrapping one huge string (exactly
    /// <see cref="InspectEventListenerResult.Arguments"/>'s own shape - a handful of keys, one of
    /// which can be an arbitrarily long m_StringArgument) is still bounded, not just the outer
    /// shape. Safe to recurse unguarded: every value this walks already passed through
    /// <see cref="ReadThrough"/>'s own <see cref="MaxInspectDepth"/> nesting bound (E1) on the way
    /// in, so this can never recurse deeper than that. <paramref name="truncated"/> is set (never
    /// cleared back to false) the first time anything anywhere in the tree is cut.
    /// </summary>
    static object? BoundValue(object? value, ref bool truncated)
    {
        switch (value)
        {
            case string text when text.Length > MaxValueUnits:
                truncated = true;
                return text[..MaxValueUnits];

            case List<object?> list:
                var boundedList = new List<object?>();
                foreach (var item in list)
                {
                    if (boundedList.Count >= MaxValueUnits) { truncated = true; break; }
                    boundedList.Add(BoundValue(item, ref truncated));
                }
                return boundedList;

            case IReadOnlyDictionary<string, object?> dict:
                var boundedDict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, entry) in dict)
                {
                    if (boundedDict.Count >= MaxValueUnits) { truncated = true; break; }
                    boundedDict[key] = BoundValue(entry, ref truncated);
                }
                return boundedDict;

            default:
                return value;
        }
    }

    static InspectMaterialResult ToMaterialResult(MaterialProperties material) => new()
    {
        Path = material.Path,
        Shader = new InspectAssetReferenceResult { Guid = material.Shader.Guid, Path = material.Shader.Path, Resolved = material.Shader.Resolved },
        Floats = material.Floats,
        Colors = material.Colors,
        Textures = material.Textures.Select(t => new InspectMaterialTextureResult
        {
            Property = t.Property,
            Guid = t.Guid,
            Path = t.Path,
            Resolved = t.Resolved,
        }).ToList(),
    };

    static InspectAnimatorControllerResult ToControllerResult(AnimatorControllerInfo controller) => new()
    {
        Path = controller.Path,
        States = controller.States.Select(s => new InspectAnimatorStateResult
        {
            FileId = s.FileId,
            Name = s.Name,
            IsDefaultState = s.IsDefaultState,
            Layer = s.Layer,
            Motion = s.Motion is { } m ? new InspectAnimatorMotionResult { Path = m.Path, SubAssetName = m.SubAssetName, IsBlendTree = m.IsBlendTree } : null,
        }).ToList(),
        Transitions = controller.Transitions.Select(t => new InspectAnimatorTransitionResult
        {
            FileId = t.FileId,
            SourceState = t.SourceState,
            DestinationState = t.DestinationState,
            Conditions = t.Conditions.Select(c => new InspectAnimatorConditionResult
            {
                Parameter = c.Parameter,
                ConditionMode = c.ConditionMode,
                Threshold = c.Threshold,
            }).ToList(),
        }).ToList(),
        Layers = controller.Layers.Select(l => new InspectAnimatorLayerResult { Name = l.Name }).ToList(),
        Parameters = controller.Parameters.Select(p => new InspectAnimatorParameterResult
        {
            Name = p.Name,
            Type = p.Type,
            DefaultValue = p.DefaultValue,
        }).ToList(),
    };

    // ---------------------------------------------------------------- inspect_asset: depth "components"

    InspectAssetResult ComponentsResult(string productGuid, string path, long targetFileId)
    {
        var components = projects.GetComponents(productGuid, path, targetFileId)
            ?? throw new McpException($"Project {productGuid} is known but its record could not be read. "
                                     + "Call hades_status for details.");

        return new InspectAssetResult
        {
            Path = path,
            Depth = "components",
            Components = components.Select(c => new InspectComponentResult
            {
                FileId = c.FileId,
                TypeName = c.TypeName,
                ScriptGuid = c.ScriptGuid,
                Missing = c.Missing,
            }).ToList(),
        };
    }

    // ---------------------------------------------------------------- inspect_asset: depth "properties"

    InspectAssetResult PropertiesResult(string productGuid, string unityProject, string path, long componentFileId)
    {
        var fields = ReadThrough.GetComponentProperties(unityProject, path, componentFileId);

        var listeners = projects.GetEventListeners(productGuid, path, componentFileId)
            ?? throw new McpException($"Project {productGuid} is known but its record could not be read. "
                                     + "Call hades_status for details.");

        // E3: each listener's own m_Arguments can carry an arbitrarily large slot (a huge
        // m_StringArgument) - bounded exactly like ValueResult's own Value, below.
        var truncated = false;
        var events = listeners.Select(l => new InspectEventListenerResult
        {
            EventField = l.EventField,
            Index = l.Index,
            Target = ToReferenceResult(l.Target),
            TargetAssemblyTypeName = l.TargetAssemblyTypeName,
            MethodName = l.MethodName,
            Mode = l.Mode,
            CallState = l.CallState,
            Arguments = BoundValue(l.Arguments, ref truncated),
        }).ToList();

        return new InspectAssetResult
        {
            Path = path,
            Depth = "properties",
            Properties = fields.Keys.ToList(),
            Events = events,
            Truncated = truncated,
        };
    }

    // ---------------------------------------------------------------- inspect_asset: depth "value"

    InspectAssetResult ValueResult(string productGuid, string unityProject, string path, long componentFileId, string property)
    {
        var fields = ReadThrough.GetComponentProperties(unityProject, path, componentFileId);

        if (!fields.TryGetValue(property, out var value))
        {
            throw new McpException(
                $"'{property}' is not a field on fileID {componentFileId} in '{path}'. Available "
                + "fields: " + string.Join(", ", fields.Keys)
                + ". Call inspect_asset with 'target' and 'component' (no 'property') to confirm.");
        }

        InspectReferenceResult? reference = null;
        if (value is IReadOnlyDictionary<string, object?> { } dict && dict.ContainsKey("fileID"))
        {
            try
            {
                var resolved = projects.GetReference(productGuid, path, componentFileId, property);
                if (resolved is not null) reference = ToReferenceResult(resolved);
            }
            catch (ArgumentException)
            {
                // Looked reference-shaped (had a "fileID" key) but did not fully validate as one -
                // the raw Value above is still the honest, correct answer either way.
            }
        }

        // E3: reference detection above always runs against the RAW value (a {fileID, guid, type}
        // dict is always tiny, never worth bounding), only the returned Value itself is capped.
        var truncated = false;
        var boundedValue = BoundValue(value, ref truncated);

        return new InspectAssetResult
        {
            Path = path,
            Depth = "value",
            Value = boundedValue,
            Reference = reference,
            Truncated = truncated,
        };
    }

    static InspectReferenceResult ToReferenceResult(ResolvedReference reference) => new()
    {
        TargetFileId = reference.FileId,
        TargetGuid = reference.Guid,
        IsUnset = reference.IsUnset,
        IsLocal = reference.IsLocal,
        ResolvedPath = reference.ResolvedPath,
        Resolved = reference.Resolved,
    };

    /// <summary>The known project's filesystem path - what every ReadThrough call needs, resolved
    /// once per tool call the same way InspectionTools/TypedAssetTools/ReferenceTools always have.</summary>
    string ResolveProjectPath(string? project)
    {
        var productGuid = ToolSupport.ResolveProject(projects, project);
        return projects.Get(productGuid)?.Path
            ?? throw new McpException($"Project {productGuid} is known but its record could not be read. "
                                     + "Call hades_status for details.");
    }

    /// <summary>
    /// Every exception type a ReadThrough (or ProjectService, which itself calls straight through to
    /// ReadThrough/ReferenceReading) call documents throwing, translated to a normal tool error - the
    /// same shape InspectionTools/TypedAssetTools/ReferenceTools/SettingsTools each already use, one
    /// copy per file by established convention rather than shared through ToolSupport.
    ///
    /// E2: <see cref="UnityYamlParseException"/> (thrown by <see cref="UnityYamlReader.Read"/> on a
    /// header it cannot parse at all, e.g. a class id overflowing Int32 - I1's own fixture) used to
    /// be missing from this list, so an UNCAUGHT poison-header exception left the MCP SDK's own
    /// wrapper (every tool error reads "An error occurred invoking '{tool}': ...") with nothing to
    /// append after the colon, rather than naming the file and the reason the way every other
    /// read-through failure already does - exactly what AssetIndexer's own equivalent catch (see
    /// AssetIndexer.cs) already caught for the indexer's read path.
    /// </summary>
    static T Guarded<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException
            or InvalidDataException or NotSupportedException or IOException or UnityYamlParseException)
        {
            throw new McpException(ex.Message);
        }
    }
}
