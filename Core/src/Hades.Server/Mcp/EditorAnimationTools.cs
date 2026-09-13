using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using WireJson = Hades.Contract.Wire.JsonValue;

namespace Hades.Server.Mcp;

/// <summary>One entry of animation_apply's/animation_create_controller's/animation_edit_controller's
/// parameter lists. 'default' is <see cref="JsonElement"/>-valued (like component_set_property's
/// 'value' used to be) since its shape depends on 'type' (a number for Float/Int, a bool for
/// Bool).</summary>
public sealed record AnimationParameterSpec
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("default")] public JsonElement? Default { get; init; }
}

public sealed record AnimationStateSpec
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("clip")] public string? Clip { get; init; }
    [JsonPropertyName("isDefault")] public bool? IsDefault { get; init; }

    // DESIGN-05 Wave 1 §2 row 4: additive state fields, all optional so every pre-existing caller
    // keeps working unchanged.
    [JsonPropertyName("speed")] public double? Speed { get; init; }
    [JsonPropertyName("mirror")] public bool? Mirror { get; init; }
    [JsonPropertyName("cycleOffset")] public double? CycleOffset { get; init; }
    [JsonPropertyName("writeDefaults")] public bool? WriteDefaults { get; init; }
    [JsonPropertyName("tag")] public string? Tag { get; init; }

    // JsonElement, not double[]: same MCP-SDK-argument-binding reason as SceneApplyOperation's
    // Position field (see that record's own field comment) - a caller-editor-layout hint, [x, y].
    [JsonPropertyName("position")] public JsonElement? Position { get; init; }
}

public sealed record AnimationConditionSpec
{
    [JsonPropertyName("parameter")] public required string Parameter { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("threshold")] public double? Threshold { get; init; }
}

public sealed record AnimationTransitionSpec
{
    [JsonPropertyName("from")] public required string From { get; init; }
    [JsonPropertyName("to")] public required string To { get; init; }
    [JsonPropertyName("hasExitTime")] public bool? HasExitTime { get; init; }
    [JsonPropertyName("duration")] public double? Duration { get; init; }
    [JsonPropertyName("conditions")] public IReadOnlyList<AnimationConditionSpec>? Conditions { get; init; }

    // DESIGN-05 Wave 1 §2 row 4: additive transition fields, all optional so every pre-existing
    // caller keeps working unchanged - closes the "the skill's own example uses exitTime and this
    // vocabulary cannot express it" gap DESIGN-05 §1.2 found.
    [JsonPropertyName("exitTime")] public double? ExitTime { get; init; }
    [JsonPropertyName("offset")] public double? Offset { get; init; }
    [JsonPropertyName("interruptionSource")] public string? InterruptionSource { get; init; }
    [JsonPropertyName("canTransitionToSelf")] public bool? CanTransitionToSelf { get; init; }
}

public sealed record AnimationTransitionRefSpec
{
    [JsonPropertyName("from")] public required string From { get; init; }
    [JsonPropertyName("to")] public required string To { get; init; }
}

/// <summary>One entry of setEvents' 'events' array - AnimationUtility.SetAnimationEvents' own
/// AnimationEvent shape, minus objectReferenceParameter's raw Unity Object (exposed as an asset
/// PATH instead, resolved through AssetDatabase - see AnimationCommands.SetEvents' own doc
/// comment). At most one of Float/Int/String/ObjectReference may be set per event - two is a
/// per-event error into 'errors', not a whole-op rejection (DESIGN-05 §5.5).</summary>
public sealed record AnimationEventSpec
{
    [JsonPropertyName("time")] public required double Time { get; init; }
    [JsonPropertyName("function")] public required string Function { get; init; }
    [JsonPropertyName("float")] public double? Float { get; init; }
    [JsonPropertyName("int")] public long? Int { get; init; }
    [JsonPropertyName("string")] public string? String { get; init; }
    [JsonPropertyName("objectReference")] public string? ObjectReference { get; init; }
}

/// <summary>One keyframe of setCurve's 'keys' array. 'easing' governs the segment ARRIVING at this
/// key (from the previous one) - see DESIGN-05 §5.2 for why that direction, not the one leaving
/// it - and is meaningless (rejected, not ignored) on the first key.</summary>
public sealed record AnimationCurveKeySpec
{
    [JsonPropertyName("time")] public required double Time { get; init; }
    [JsonPropertyName("value")] public required double Value { get; init; }
    [JsonPropertyName("easing")] public string? Easing { get; init; }
}

/// <summary>
/// Plan 10 Task 6 removed this file's four MCP tools (animation_assign_controller,
/// animation_assign_clip, animation_create_controller, animation_edit_controller - folded into
/// animation_apply), but the five wire-conversion helpers below and the four spec records above
/// stayed: AnimationApplyTool.cs (animation_apply's own createController/editController ops) reuses
/// every one of them verbatim rather than a second, divergent
/// AnimationParameterSpec/AnimationStateSpec/AnimationTransitionSpec/AnimationTransitionRefSpec ->
/// WireJson conversion - see that file's own doc comment. <c>internal</c> (not <c>private</c>) is
/// what makes that reuse possible. No <c>[McpServerToolType]</c> registration or live Editor
/// connection is left in this file for <c>Program.cs</c> to register.
/// </summary>
public static class EditorAnimationTools
{
    internal static bool HasItems<T>(IReadOnlyList<T>? list) => list is { Count: > 0 };

    internal static WireJson ToWireParameter(AnimationParameterSpec p)
    {
        var json = WireJson.NewObject().SetProperty("name", WireJson.String(p.Name)).SetProperty("type", WireJson.String(p.Type));
        if (p.Default is { } d) json.SetProperty("default", WireJsonBridge.ToWire(d));
        return json;
    }

    internal static WireJson ToWireState(AnimationStateSpec s)
    {
        var json = WireJson.NewObject().SetProperty("name", WireJson.String(s.Name));
        if (!string.IsNullOrEmpty(s.Clip)) json.SetProperty("clip", WireJson.String(s.Clip));
        if (s.IsDefault is { } isDefault) json.SetProperty("isDefault", WireJson.Bool(isDefault));
        if (s.Speed is { } speed) json.SetProperty("speed", WireJson.Float(speed));
        if (s.Mirror is { } mirror) json.SetProperty("mirror", WireJson.Bool(mirror));
        if (s.CycleOffset is { } cycleOffset) json.SetProperty("cycleOffset", WireJson.Float(cycleOffset));
        if (s.WriteDefaults is { } writeDefaults) json.SetProperty("writeDefaults", WireJson.Bool(writeDefaults));
        if (!string.IsNullOrEmpty(s.Tag)) json.SetProperty("tag", WireJson.String(s.Tag));
        if (s.Position is { } position) json.SetProperty("position", Vector2Array(position, "state '" + s.Name + "'", "position"));
        return json;
    }

    internal static WireJson ToWireTransition(AnimationTransitionSpec t)
    {
        var json = WireJson.NewObject().SetProperty("from", WireJson.String(t.From)).SetProperty("to", WireJson.String(t.To));
        if (t.HasExitTime is { } hasExitTime) json.SetProperty("hasExitTime", WireJson.Bool(hasExitTime));
        if (t.Duration is { } duration) json.SetProperty("duration", WireJson.Float(duration));
        if (t.ExitTime is { } exitTime) json.SetProperty("exitTime", WireJson.Float(exitTime));
        if (t.Offset is { } offset) json.SetProperty("offset", WireJson.Float(offset));
        if (!string.IsNullOrEmpty(t.InterruptionSource)) json.SetProperty("interruptionSource", WireJson.String(t.InterruptionSource));
        if (t.CanTransitionToSelf is { } canTransitionToSelf) json.SetProperty("canTransitionToSelf", WireJson.Bool(canTransitionToSelf));
        if (t.Conditions is { Count: > 0 })
        {
            var conditionsJson = WireJson.NewArray();
            foreach (var c in t.Conditions)
            {
                var cJson = WireJson.NewObject().SetProperty("parameter", WireJson.String(c.Parameter)).SetProperty("mode", WireJson.String(c.Mode));
                if (c.Threshold is { } threshold) cJson.SetProperty("threshold", WireJson.Float(threshold));
                conditionsJson.Add(cJson);
            }
            json.SetProperty("conditions", conditionsJson);
        }
        return json;
    }

    internal static WireJson ToWireEvent(AnimationEventSpec e)
    {
        var json = WireJson.NewObject().SetProperty("time", WireJson.Float(e.Time)).SetProperty("function", WireJson.String(e.Function));
        if (e.Float is { } f) json.SetProperty("float", WireJson.Float(f));
        if (e.Int is { } i) json.SetProperty("int", WireJson.Integer(i));
        if (e.String is not null) json.SetProperty("string", WireJson.String(e.String));
        if (!string.IsNullOrEmpty(e.ObjectReference)) json.SetProperty("objectReference", WireJson.String(e.ObjectReference));
        return json;
    }

    internal static WireJson ToWireCurveKey(AnimationCurveKeySpec k)
    {
        var json = WireJson.NewObject().SetProperty("time", WireJson.Float(k.Time)).SetProperty("value", WireJson.Float(k.Value));
        if (!string.IsNullOrEmpty(k.Easing)) json.SetProperty("easing", WireJson.String(k.Easing));
        return json;
    }

    /// <summary>Converts a JsonElement-bound 2-element [x, y] editor-layout position (see
    /// AnimationStateSpec.Position's own field comment for why JsonElement, not double[]) into the
    /// wire's array-of-numbers shape - the same conversion/error-reporting idea as SceneApplyTool's
    /// own FloatArray helper, sized for 2 elements instead of 3 and phrased for a nested state
    /// entry (no operations[i] index of its own to report) rather than a top-level op field.</summary>
    static WireJson Vector2Array(JsonElement element, string context, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new McpException($"{context}: '{fieldName}' must be a 2-element array [x, y] - got {DescribeShape(element)}.");

        var items = element.EnumerateArray().ToArray();
        if (items.Length != 2)
            throw new McpException($"{context}: '{fieldName}' must have exactly 2 elements [x, y] - got {items.Length}.");

        var array = WireJson.NewArray();
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].ValueKind != JsonValueKind.Number)
                throw new McpException($"{context}: '{fieldName}[{i}]' must be a number, got {DescribeShape(items[i])}.");
            array.Add(WireJson.Float(items[i].GetDouble()));
        }
        return array;
    }

    static string DescribeShape(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        _ => "a non-numeric value",
    };

    internal static IReadOnlyList<string> ToStringList(WireJson value, string key)
    {
        var list = new List<string>();
        if (value.TryGetProperty(key, out var arr) && arr!.Kind == Hades.Contract.Wire.JsonValueKind.Array)
            foreach (var item in arr.Items) list.Add(item.AsString());
        return list;
    }
}
