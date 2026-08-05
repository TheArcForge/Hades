using System.ComponentModel;
using System.Text.Json.Serialization;
using Hades.Core.Editors;
using ModelContextProtocol.Server;
using WireJson = Hades.Contract.Wire.JsonValue;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Mcp;

public sealed record Vector3Result
{
    [JsonPropertyName("x")] public required double X { get; init; }
    [JsonPropertyName("y")] public required double Y { get; init; }
    [JsonPropertyName("z")] public required double Z { get; init; }
}

/// <summary>One serialized field on one component, as inspector_inspect reports it.
/// <see cref="Value"/> is a raw scalar for a simple field or a nested object/array for a struct-
/// or reference-valued one (a Vector3, a Color, ...) - the same untyped-on-purpose shape
/// InspectAssetResult.Value (InspectTool.cs) already uses, since a single property's value
/// can be any of those depending on the underlying SerializedPropertyType.</summary>
public sealed record InspectorPropertyResult
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("displayName")] public required string DisplayName { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("value")] public object? Value { get; init; }
}

public sealed record InspectorComponentResult
{
    [JsonPropertyName("fileId")] public required long FileId { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("fullType")] public required string FullType { get; init; }
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
    [JsonPropertyName("properties")] public required IReadOnlyList<InspectorPropertyResult> Properties { get; init; }
}

public sealed record InspectorInspectResult
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("fileId")] public required long FileId { get; init; }
    [JsonPropertyName("active")] public required bool Active { get; init; }
    [JsonPropertyName("layer")] public required string Layer { get; init; }
    [JsonPropertyName("tag")] public required string Tag { get; init; }
    [JsonPropertyName("isStatic")] public required bool IsStatic { get; init; }
    [JsonPropertyName("position")] public required Vector3Result Position { get; init; }
    [JsonPropertyName("rotation")] public required Vector3Result Rotation { get; init; }
    [JsonPropertyName("scale")] public required Vector3Result Scale { get; init; }
    [JsonPropertyName("childCount")] public required int ChildCount { get; init; }
    [JsonPropertyName("children")] public required IReadOnlyList<string> Children { get; init; }
    [JsonPropertyName("components")] public required IReadOnlyList<InspectorComponentResult> Components { get; init; }
}

/// <summary>
/// Class-4 (live-state read - see the "52 Editor tools" plan's operation-class table, Task 5)
/// GameObject inspection: inspector_inspect. Every component's CURRENT, live serialized field
/// values, read directly off the attached Editor's own open scene - unlike component_get_all
/// (Plan 5, read-through), which re-parses the scene/prefab file as last SAVED, this reflects
/// unsaved Editor edits too, which is exactly why it needs a live Editor at all (see
/// ToolSupport.LiveStateClause, this tool's own description).
///
/// Plan 10 Task 6 removed this file's other MCP tool, inspector_select - folded into scene_apply's
/// "select" operation (see SceneApplyTool.cs's own class doc comment) - along with its
/// InspectorSelectResult type. See Plugin~'s InspectorCommands.cs for why the removed
/// inspector_select made no explicit Undo call - Unity's own Editor already tracks selection
/// changes on its undo stack automatically; inspector_inspect mutates nothing so the question never
/// arose for it at all.
/// </summary>
[McpServerToolType]
public sealed class EditorInspectorTools(EditorProxy editor)
{
    [McpServerTool(Name = "inspector_inspect", Title = "Inspect GameObject", ReadOnly = true, UseStructuredContent = true)]
    [Description("The live, in-Editor state of one GameObject and every component on it: "
               + "transform, tag/layer/static flag, children, and each component's current "
               + "serialized field values - independent of whatever is currently selected in the "
               + "Editor (addressed by hierarchy path). Needs a "
               + "live Editor - call hades_charon_status first if unsure." + ToolSupport.LiveStateClause)]
    public async Task<InspectorInspectResult> InspectorInspect(
        [Description("Hierarchy path of the GameObject to inspect")] string path,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        EditorComponentTools.RequireNonBlank(path, nameof(path), "inspector_inspect");

        var @params = WireJson.NewObject().SetProperty("path", WireJson.String(path));
        var result = await editor.SendCommandAsync(project, "inspector.inspect", @params).ConfigureAwait(false);

        var children = new List<string>();
        if (result.TryGetProperty("children", out var childrenJson) && childrenJson!.Kind == WireKind.Array)
            foreach (var child in childrenJson.Items)
                if (child.Kind == WireKind.String) children.Add(child.AsString());

        var components = new List<InspectorComponentResult>();
        if (result.TryGetProperty("components", out var componentsJson) && componentsJson!.Kind == WireKind.Array)
            foreach (var component in componentsJson.Items)
                components.Add(ToComponentResult(component));

        return new InspectorInspectResult
        {
            Name = EditorComponentTools.Str(result, "name"),
            Path = EditorComponentTools.Str(result, "path"),
            FileId = EditorComponentTools.Int(result, "fileId"),
            Active = result.TryGetProperty("active", out var a) && a!.Kind == WireKind.Boolean && a.AsBoolean(),
            Layer = EditorComponentTools.Str(result, "layer"),
            Tag = EditorComponentTools.Str(result, "tag"),
            IsStatic = result.TryGetProperty("isStatic", out var s) && s!.Kind == WireKind.Boolean && s.AsBoolean(),
            Position = ToVector3(result, "position"),
            Rotation = ToVector3(result, "rotation"),
            Scale = ToVector3(result, "scale"),
            ChildCount = (int)EditorComponentTools.Int(result, "childCount"),
            Children = children,
            Components = components,
        };
    }

    static InspectorComponentResult ToComponentResult(WireJson component)
    {
        var properties = new List<InspectorPropertyResult>();
        if (component.TryGetProperty("properties", out var propertiesJson) && propertiesJson!.Kind == WireKind.Array)
        {
            foreach (var property in propertiesJson.Items)
            {
                properties.Add(new InspectorPropertyResult
                {
                    Name = EditorComponentTools.Str(property, "name"),
                    DisplayName = EditorComponentTools.Str(property, "displayName"),
                    Type = EditorComponentTools.Str(property, "type"),
                    Value = property.TryGetProperty("value", out var v) ? WireJsonBridge.ToClr(v!) : null,
                });
            }
        }

        return new InspectorComponentResult
        {
            FileId = EditorComponentTools.Int(component, "fileId"),
            Type = EditorComponentTools.Str(component, "type"),
            FullType = EditorComponentTools.Str(component, "fullType"),
            Enabled = component.TryGetProperty("enabled", out var e) && e!.Kind == WireKind.Boolean && e.AsBoolean(),
            Properties = properties,
        };
    }

    static Vector3Result ToVector3(WireJson result, string key)
    {
        if (!result.TryGetProperty(key, out var v) || v!.Kind != WireKind.Object)
            return new Vector3Result { X = 0, Y = 0, Z = 0 };

        double Component(string axis) =>
            v.TryGetProperty(axis, out var c) && (c!.Kind == WireKind.Float || c.Kind == WireKind.Integer) ? c.AsDouble() : 0;

        return new Vector3Result { X = Component("x"), Y = Component("y"), Z = Component("z") };
    }
}
