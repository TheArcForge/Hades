using System.Text.Json;
using ModelContextProtocol;
using WireJson = Hades.Contract.Wire.JsonValue;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Mcp;

/// <summary>
/// Shared helpers used across every Editor-proxying MCP tool file in this directory:
/// blank-parameter validation (<see cref="RequireNonBlank"/>) and terse wire-value accessors
/// (<see cref="Str"/>/<see cref="Int"/>) that read one property off a <see cref="WireJson"/> object
/// without every call site re-deriving the same TryGetProperty/Kind-check boilerplate.
///
/// Plan 10 Task 6 removed this class's own seven MCP tools (component_add, component_remove,
/// component_set_property, component_set_properties, reference_set, event_add_listener,
/// event_remove_listener - folded into scene_apply's operation vocabulary), but these three static
/// helpers stayed load-bearing for essentially every other tool file in this directory (each already
/// spells them <c>EditorComponentTools.RequireNonBlank</c>/<c>.Str</c>/<c>.Int</c>), so the class
/// itself stays too, as a plain static helper holder - no live Editor connection, no
/// <c>[McpServerToolType]</c> registration, nothing left for <c>Program.cs</c> to register.
/// </summary>
public static class EditorComponentTools
{
    internal static void RequireNonBlank(string? value, string paramName, string toolName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new McpException($"{toolName} needs a non-empty '{paramName}' parameter.");
    }

    internal static string Str(WireJson value, string key) =>
        value.TryGetProperty(key, out var v) && v!.Kind == WireKind.String ? v.AsString() : "";

    internal static long Int(WireJson value, string key) =>
        value.TryGetProperty(key, out var v) && v!.Kind == WireKind.Integer ? v.AsInteger() : 0;
}

/// <summary>
/// Bridges <see cref="JsonElement"/> (what an MCP tool parameter deserializes arbitrary JSON into)
/// and <see cref="WireJson"/> (what <see cref="Hades.Core.Editors.EditorProxy"/> sends/receives) -
/// needed because a component property value can be a scalar OR a nested object (Vector3, Color,
/// ...), so it cannot be a plain typed parameter the way every other tool's arguments are. Shared by
/// essentially every mutating tool in this directory (scene_apply, prefab_apply, material_apply,
/// animation_apply, asset_manage, scene_manage, project_settings_apply, ...) for the per-operation
/// values/properties they each accept and echo back, so the two directions of conversion exist
/// exactly once.
/// </summary>
internal static class WireJsonBridge
{
    public static WireJson ToWire(JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => WireJson.Null,
        System.Text.Json.JsonValueKind.True => WireJson.Bool(true),
        System.Text.Json.JsonValueKind.False => WireJson.Bool(false),
        System.Text.Json.JsonValueKind.String => WireJson.String(element.GetString()),
        System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? WireJson.Integer(l) : WireJson.Float(element.GetDouble()),
        System.Text.Json.JsonValueKind.Array => ToWireArray(element),
        System.Text.Json.JsonValueKind.Object => ToWireObject(element),
        _ => WireJson.Null,
    };

    static WireJson ToWireArray(JsonElement element)
    {
        var array = WireJson.NewArray();
        foreach (var item in element.EnumerateArray()) array.Add(ToWire(item));
        return array;
    }

    static WireJson ToWireObject(JsonElement element)
    {
        var obj = WireJson.NewObject();
        foreach (var prop in element.EnumerateObject()) obj.SetProperty(prop.Name, ToWire(prop.Value));
        return obj;
    }

    /// <summary>The reverse direction: a plugin result value into the plain CLR shape (string,
    /// long, double, bool, null, List&lt;object?&gt;, Dictionary&lt;string,object?&gt;) that
    /// System.Text.Json serializes as structured content the same way ReadThrough's own untyped
    /// property values already do elsewhere in this codebase.</summary>
    public static object? ToClr(WireJson value) => value.Kind switch
    {
        WireKind.Null => null,
        WireKind.Boolean => value.AsBoolean(),
        WireKind.Integer => value.AsInteger(),
        WireKind.Float => value.AsDouble(),
        WireKind.String => value.AsString(),
        WireKind.Array => value.Items.Select(ToClr).ToList(),
        WireKind.Object => value.Members.ToDictionary(m => m.Key, m => ToClr(m.Value)),
        _ => null,
    };
}
