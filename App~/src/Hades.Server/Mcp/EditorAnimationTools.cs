using System.Text.Json;
using System.Text.Json.Serialization;
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
}

public sealed record AnimationTransitionRefSpec
{
    [JsonPropertyName("from")] public required string From { get; init; }
    [JsonPropertyName("to")] public required string To { get; init; }
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
        return json;
    }

    internal static WireJson ToWireTransition(AnimationTransitionSpec t)
    {
        var json = WireJson.NewObject().SetProperty("from", WireJson.String(t.From)).SetProperty("to", WireJson.String(t.To));
        if (t.HasExitTime is { } hasExitTime) json.SetProperty("hasExitTime", WireJson.Bool(hasExitTime));
        if (t.Duration is { } duration) json.SetProperty("duration", WireJson.Float(duration));
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

    internal static IReadOnlyList<string> ToStringList(WireJson value, string key)
    {
        var list = new List<string>();
        if (value.TryGetProperty(key, out var arr) && arr!.Kind == Hades.Contract.Wire.JsonValueKind.Array)
            foreach (var item in arr.Items) list.Add(item.AsString());
        return list;
    }
}
