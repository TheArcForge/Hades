using System.ComponentModel;
using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Reading;
using Hades.Core.Unity;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Hades.Server.Mcp;

public sealed record ImportSettingsResult
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("importerType")] public required string ImporterType { get; init; }

    /// <summary>Raw, unfiltered importer settings - the same document-tree shape
    /// InspectTool.InspectAssetResult.Value uses for a structured field.</summary>
    [JsonPropertyName("settings")] public required IReadOnlyDictionary<string, object?> Settings { get; init; }
}

public sealed record ProjectSettingsResult
{
    [JsonPropertyName("productGuid")] public required string ProductGuid { get; init; }
    [JsonPropertyName("companyName")] public string? CompanyName { get; init; }
    [JsonPropertyName("productName")] public string? ProductName { get; init; }
    [JsonPropertyName("bundleVersion")] public string? BundleVersion { get; init; }
}

public sealed record TagListResult
{
    [JsonPropertyName("tags")] public required IReadOnlyList<string> Tags { get; init; }
}

public sealed record LayerListResult
{
    /// <summary>Always 32 entries, index 0-31, exactly mirroring Unity's own fixed-length layers
    /// array - an unused slot is an empty string at its own index, never omitted or compacted.</summary>
    [JsonPropertyName("layers")] public required IReadOnlyList<string> Layers { get; init; }
}

public sealed record BuildSceneEntry
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
    [JsonPropertyName("guid")] public string? Guid { get; init; }
}

public sealed record SceneListBuildResult
{
    /// <summary>In EditorBuildSettings.asset's own order - the build's scene index order,
    /// which scene 0 loads first matters and must not be re-sorted.</summary>
    [JsonPropertyName("scenes")] public required IReadOnlyList<BuildSceneEntry> Scenes { get; init; }
}

/// <summary>
/// One <c>project_settings</c> call's result. Exactly one of the six section-specific payloads
/// below is set, selected by <see cref="Section"/> - the same "one field per named shape, the rest
/// simply absent from the wire" convention <c>inspect_asset</c> established (see
/// InspectTool.InspectAssetResult's own doc comment). Every payload type is REUSED from the
/// single-purpose reader that used to back that section (before Plan 10 Task 6 removed its own MCP
/// tool registration) - never a new, parallel shape - <see cref="Player"/> is exactly
/// <see cref="ProjectSettingsResult"/>, <see cref="BuildScenes"/> is exactly
/// <see cref="BuildSceneEntry"/>'s list, <see cref="RenderPipeline"/> is exactly
/// <see cref="RenderPipelineResult"/> (TypedAssetTools.cs), and <see cref="ImportSettings"/> is
/// exactly <see cref="ImportSettingsResult"/>.
/// </summary>
public sealed record ProjectSettingsSectionResult
{
    [JsonPropertyName("section")] public required string Section { get; init; }

    // section == "player"
    [JsonPropertyName("player")] public ProjectSettingsResult? Player { get; init; }

    // section == "tags"
    [JsonPropertyName("tags")] public IReadOnlyList<string>? Tags { get; init; }

    // section == "layers" - fixed 32-slot array, see LayerList's own doc comment; unchanged here.
    [JsonPropertyName("layers")] public IReadOnlyList<string>? Layers { get; init; }

    // section == "buildScenes"
    [JsonPropertyName("buildScenes")] public IReadOnlyList<BuildSceneEntry>? BuildScenes { get; init; }

    // section == "renderPipeline"
    [JsonPropertyName("renderPipeline")] public RenderPipelineResult? RenderPipeline { get; init; }

    // section == "importSettings" (needs 'assetPath' too)
    [JsonPropertyName("importSettings")] public ImportSettingsResult? ImportSettings { get; init; }
}

/// <summary>
/// Project-settings tools: one consolidated read, <c>project_settings</c>, selecting one of six
/// sections of Unity's own fixed ProjectSettings/*.asset files via a 'section' parameter - never a
/// caller-supplied path, so it takes no 'path' argument itself. Same conventions as
/// InspectTool/QueryTools throughout - see HadesTools' class doc comment for why project routing
/// uses an explicit handle rather than MCP roots.
///
/// <para><b>Plan 10 Task 6.</b> This file used to also carry asset_get_info and asset_find as their
/// own MCP tools; both are gone now (asset_get_info folded into inspect_asset - see InspectTool.cs's
/// class doc comment; asset_find folded into graph_query's <c>fileType</c> filter - see
/// QueryTools.cs's class doc comment for the file_state-backed mechanism that replaces it and the
/// gap it closes). This file also used to carry project_get_settings, tag_list, layer_list,
/// scene_list_build, and asset_get_import_settings as FIVE MORE separate MCP tools; all five kept
/// their method bodies (renamed nowhere, still called <see cref="ProjectGetSettings"/>/
/// <see cref="TagList"/>/<see cref="LayerList"/>/<see cref="SceneListBuild"/>/
/// <see cref="AssetGetImportSettings"/>) but lost their own <c>[McpServerTool]</c> registration,
/// because <see cref="ProjectSettings"/> DELEGATES to them directly - no read logic is duplicated, so
/// a bug fix to any one of them is automatically visible through project_settings too.</para>
/// </summary>
[McpServerToolType]
public sealed class SettingsTools(ProjectService projects)
{
    public ImportSettingsResult AssetGetImportSettings(
        string path,
        string? project = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException(
                "project_settings section \"importSettings\" needs an 'assetPath' — the "
                + "project-relative asset path to inspect, e.g. {\"section\": \"importSettings\", "
                + "\"assetPath\": \"Assets/Textures/Rock.png\"}. search_by_name returns paths in "
                + "exactly this form.");
        }

        var unityProject = ResolveProjectPath(project);
        var info = Guarded(() => ReadThrough.GetImportSettings(unityProject, path));

        return new ImportSettingsResult { Path = info.Path, ImporterType = info.ImporterType, Settings = info.Settings };
    }

    public ProjectSettingsResult ProjectGetSettings(string? project = null)
    {
        var productGuid = ToolSupport.ResolveProject(projects, project);
        var unityProject = ResolveProjectPath(project);

        var document = Guarded(() => ReadThrough.GetSettingsAsset(unityProject, "ProjectSettings/ProjectSettings.asset"));

        return new ProjectSettingsResult
        {
            // The known project's own productGuid, not re-derived from the document: it is
            // already resolved (this call would not have a project to route to otherwise), and
            // ProjectIdentity.TryReadProductGuid - the one place that value is derived - already
            // lower-cases it, which a raw re-read of the same field here would not.
            ProductGuid = productGuid,
            CompanyName = StringField(document, "companyName"),
            ProductName = StringField(document, "productName"),
            BundleVersion = StringField(document, "bundleVersion"),
        };
    }

    public TagListResult TagList(string? project = null)
    {
        var unityProject = ResolveProjectPath(project);
        var document = Guarded(() => ReadThrough.GetSettingsAsset(unityProject, "ProjectSettings/TagManager.asset"));

        return new TagListResult { Tags = StringList(document, "tags") };
    }

    public LayerListResult LayerList(string? project = null)
    {
        var unityProject = ResolveProjectPath(project);
        var document = Guarded(() => ReadThrough.GetSettingsAsset(unityProject, "ProjectSettings/TagManager.asset"));

        return new LayerListResult { Layers = StringList(document, "layers") };
    }

    public SceneListBuildResult SceneListBuild(string? project = null)
    {
        var unityProject = ResolveProjectPath(project);
        var document = Guarded(() => ReadThrough.GetSettingsAsset(unityProject, "ProjectSettings/EditorBuildSettings.asset"));

        var scenes = document.TryGetValue("m_Scenes", out var raw) && raw is List<object?> list
            ? list.OfType<Dictionary<string, object?>>().Select(entry => new BuildSceneEntry
              {
                  Path = entry.TryGetValue("path", out var p) ? p as string ?? "" : "",
                  Enabled = entry.TryGetValue("enabled", out var e) && e as string == "1",
                  Guid = entry.TryGetValue("guid", out var g) ? g as string : null,
              }).ToList()
            : [];

        return new SceneListBuildResult { Scenes = scenes };
    }

    static readonly string[] ValidSections = ["player", "tags", "layers", "buildScenes", "renderPipeline", "importSettings"];

    [McpServerTool(Name = "project_settings", Title = "Project Settings", ReadOnly = true, UseStructuredContent = true)]
    [Description("One section of the project's configuration, selected by 'section'. "
               + "\"player\" - productGuid/companyName/productName/bundleVersion. \"tags\" - every "
               + "custom tag; Unity's own builtin tags (Untagged, Respawn, ...) are not included. "
               + "\"layers\" - all 32 layer slots, index 0-31 exactly as Unity's Tags & Layers "
               + "window shows them; an unused slot is a real, meaningful empty string AT ITS OWN "
               + "INDEX, never omitted or compacted - layer 8 being free is not the same as layers "
               + "ending at 7. \"buildScenes\" - the Build Settings scene list in build-index order; "
               + "each entry's 'enabled' flag is whether it is actually included in a build, not "
               + "just present in the list. \"renderPipeline\" - \"Built-in\", \"URP\", \"HDRP\", or "
               + "\"unknown\" when a custom pipeline is configured but cannot be positively "
               + "identified - never guessed from a partial match, since Built-in/URP/HDRP differ "
               + "enough that a guess would just be wrong sometimes. \"importSettings\" (also needs "
               + "'assetPath') - one asset's importer block, e.g. a TextureImporter's compression "
               + "settings. An unrecognised 'section' is refused, listing the valid ones."
               + ToolSupport.SavedStateClause)]
    public async Task<ProjectSettingsSectionResult> ProjectSettings(
        [Description("Which section to read: \"player\", \"tags\", \"layers\", \"buildScenes\", \"renderPipeline\", or \"importSettings\"")] string section,
        [Description("Required when section is \"importSettings\": the project-relative asset path to inspect, as returned by search_by_name")] string? assetPath = null,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            throw new McpException(
                "project_settings needs a 'section' - one of: " + string.Join(", ", ValidSections)
                + ". Add one and call again, e.g. {\"section\": \"player\"}.");
        }

        // Both checks below used to live inside the section-specific handlers themselves (see
        // ReadImportSettingsForSection's former doc comment), reached only once a switch arm below
        // had already matched - which meant they always ran BEFORE that handler's own project
        // resolution. Hoisted here, ahead of the SINGLE resolution below, to keep that exact
        // ordering now that resolution can consult MCP roots (a real round trip, occasionally a
        // write - see ToolSupport.ResolveProjectAsync) rather than always being free: an
        // unrecognised section or a missing 'assetPath' must still be refused without ever
        // attempting that, exactly as before.
        if (!ValidSections.Contains(section))
        {
            throw new McpException(
                $"'{section}' is not a recognised project_settings section. Valid sections: {string.Join(", ", ValidSections)}.");
        }

        if (section == "importSettings" && string.IsNullOrWhiteSpace(assetPath))
        {
            throw new McpException(
                "project_settings section \"importSettings\" needs an 'assetPath' - the "
                + "project-relative asset path to inspect, e.g. {\"section\": \"importSettings\", "
                + "\"assetPath\": \"Assets/Textures/Rock.png\"}. search_by_name returns paths in "
                + "exactly this form.");
        }

        // Resolved exactly ONCE per call, here - every section handler below is called with this
        // SAME already-resolved productGuid in place of the raw 'project' handle, never re-run
        // through ToolSupport.ResolveProjectAsync a second time. Passing an already-resolved,
        // non-blank productGuid as a handle is always safe and free (ProjectResolver.Resolve's own
        // "explicit handle" fast path - see ToolSupportTests - never re-consults roots), so every
        // handler below still resolves correctly without needing its own context/await.
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);

        return section switch
        {
            "player" => new ProjectSettingsSectionResult { Section = section, Player = ProjectGetSettings(productGuid) },
            "tags" => new ProjectSettingsSectionResult { Section = section, Tags = TagList(productGuid).Tags },
            "layers" => new ProjectSettingsSectionResult { Section = section, Layers = LayerList(productGuid).Layers },
            "buildScenes" => new ProjectSettingsSectionResult { Section = section, BuildScenes = SceneListBuild(productGuid).Scenes },
            "renderPipeline" => new ProjectSettingsSectionResult { Section = section, RenderPipeline = ReadRenderPipeline(productGuid) },
            "importSettings" => new ProjectSettingsSectionResult { Section = section, ImportSettings = AssetGetImportSettings(assetPath!, productGuid) },
            _ => throw new McpException(
                $"'{section}' is not a recognised project_settings section. Valid sections: {string.Join(", ", ValidSections)}."),
        };
    }

    /// <summary>Calls straight into <see cref="Reading.ReadThrough.AnalyzeRenderPipeline"/> - the
    /// SAME call the old analyze_render_pipeline tool itself used to make, before Plan 10 Task 6
    /// removed it - rather than depending on a separate class for one section.</summary>
    RenderPipelineResult ReadRenderPipeline(string? project)
    {
        var unityProject = ResolveProjectPath(project);
        var info = Guarded(() => ReadThrough.AnalyzeRenderPipeline(unityProject));
        return new RenderPipelineResult { Pipeline = info.Pipeline, PipelineAssetPath = info.PipelineAssetPath };
    }

    /// <summary>A scalar field's string value, or null when the document does not have it - some
    /// ProjectSettings.asset fields are genuinely optional/version-dependent.</summary>
    static string? StringField(IReadOnlyDictionary<string, object?> document, string key) =>
        document.TryGetValue(key, out var value) ? value as string : null;

    /// <summary>A sequence field's scalar entries as strings - what "tags" and "layers" both are.
    /// An absent or non-sequence field degrades to empty rather than throwing: TagManager.asset's
    /// shape has been stable for years, but a field this reads defensively rather than assuming.</summary>
    static IReadOnlyList<string> StringList(IReadOnlyDictionary<string, object?> document, string key) =>
        document.TryGetValue(key, out var value) && value is List<object?> list
            ? list.Select(item => item as string ?? "").ToList()
            : [];

    /// <summary>The known project's filesystem path - what every ReadThrough call needs, resolved
    /// once per tool call the same way InspectionTools always has.</summary>
    string ResolveProjectPath(string? project)
    {
        var productGuid = ToolSupport.ResolveProject(projects, project);
        return projects.Get(productGuid)?.Path
            ?? throw new McpException($"Project {productGuid} is known but its record could not be read. "
                                     + "Call hades_status for details.");
    }

    /// <summary>
    /// Every exception type a ReadThrough call documents throwing, translated to a normal tool
    /// error - see <see cref="ReadThrough"/>'s own doc comments for what each one means. Anything
    /// NOT in this list is a genuine surprise and is deliberately left to propagate rather than be
    /// papered over here. Same shape as InspectionTools' own Guarded - each tools file keeps its
    /// own copy rather than sharing one through ToolSupport, matching the existing convention.
    ///
    /// E2: <see cref="UnityYamlParseException"/> added alongside InspectTool's own Guarded, for the
    /// same reason - see that copy's own doc comment. None of this file's own ReadThrough calls
    /// (GetSettingsAsset, GetImportSettings, AnalyzeRenderPipeline) currently route through
    /// UnityYamlReader.Read themselves, so today this is a consistency/defence-in-depth match
    /// rather than a live gap here specifically, but a Guarded that silently omits a type the
    /// sibling copy already catches is exactly the kind of drift this fix closes.
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
