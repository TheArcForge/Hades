using System.Text.Json;
using Hades.Server.Control;

namespace Hades.Server.Tests;

/// <summary>
/// Plan 14 Task 1 (spec #4 §1-§2): <c>ClaudeCodePlugin/</c> is the second of the three install
/// units - skills, the <c>/hades:*</c> commands, and the MCP server declaration, shipping and
/// versioning together as a single Claude Code plugin (<c>hades@arcforge</c>) instead of the
/// scattered v1.2 install (UPM package, generated <c>.mcp.json</c>, marked <c>CLAUDE.md</c> block,
/// <c>~/.claude/skills/</c>, and the <c>~/.arcforge/hades-hub/launcher.js</c> Node launcher).
///
/// Two ways this package can silently go stale, and the two things these tests guard:
///
/// 1. <b>The MCP endpoint drifting from the app's own documented port.</b>
///    <see cref="SettingsEndpoint.McpPort"/> is the single source of truth (see that class's own
///    doc comment on why 7823 is load-bearing) - never re-typed here as a literal, so a future
///    change to that constant fails this test immediately instead of shipping a plugin pointed at
///    a dead port.
///
/// 2. <b>A skill or command silently missing from the shipped plugin.</b> The real source of truth
///    for what v1.2 ships is <c>/skills/</c> (22 directories, one <c>SKILL.md</c> each) and
///    <c>/commands/</c> (6 files) at the repo root - the same source
///    <c>scripts/sync-plugin.sh</c> already packages from for the retired v1.2 proto-plugin (whose
///    manifest was removed; see <c>Documentation/Retired/root-plugin-manifest.md</c>). Byte-identical comparison mirrors
///    <see cref="PluginWireContractTests"/>'s own reasoning for <c>UnityPlugin/Contract</c>: a copy
///    that can drift from its source is worse than no copy, and hand-retyped content is exactly
///    the kind of copy that drifts silently.
///
/// Repo-root discovery walks up from <see cref="AppContext.BaseDirectory"/> the same way
/// <c>PluginRequiredFields.FindPluginToolsDirectory</c> finds <c>UnityPlugin</c> - landmark directories
/// as the terminating condition, not a hardcoded machine path, so this runs the same in CI as on
/// this checkout.
/// </summary>
public class ClaudeCodePluginTests
{
    // ---------------------------------------------------------------------------- repo-root discovery

    static readonly Lazy<string> LazyRepoRoot = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Core"))
                && Directory.Exists(Path.Combine(dir.FullName, "ClaudeCodePlugin")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the Hades repo root (Core + ClaudeCodePlugin siblings) by walking up "
            + "from " + AppContext.BaseDirectory + ". Is this test running from within the normal "
            + "Core/tests checkout layout?");
    });

    static string RepoRoot => LazyRepoRoot.Value;
    static string PluginRoot => Path.Combine(RepoRoot, "ClaudeCodePlugin");

    static bool FileBytesEqual(string a, string b) =>
        File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    /// <summary>The plugin's own filename for a given source command filename: the source's
    /// leading <c>"hades-"</c> dropped. The plugin is itself named "hades" (see
    /// <see cref="PluginManifest_ExistsUnderDotClaudePluginDirectory_AndIsValidJson"/>), so Claude
    /// Code already namespaces every command as <c>/hades:&lt;filename&gt;</c> - keeping the
    /// source's own "hades-" would stutter to <c>/hades:hades-status</c> instead of the
    /// <c>/hades:status</c> this project's own CLAUDE.md documents. The v1.2 source of truth (and
    /// <c>scripts/sync-plugin.sh</c>, which packages from it) keep their own "hades-" names -
    /// nothing about the source changes, only this new packaging unit's copy of the filename.
    /// Content is still byte-identical; only the name is derived.</summary>
    static string PluginCommandNameFor(string sourceFileName) =>
        sourceFileName.StartsWith("hades-", StringComparison.Ordinal)
            ? sourceFileName["hades-".Length..]
            : sourceFileName;

    // ------------------------------------------------------------------------ plugin.json / .mcp.json

    [Fact]
    public void PluginManifest_ExistsUnderDotClaudePluginDirectory_AndIsValidJson()
    {
        // Required location per Claude Code's own plugin manifest schema (verified against the
        // real, currently-installed plugins on this machine - superpowers, plugin-dev,
        // mcp-tunnels - which all place plugin.json at <root>/.claude-plugin/plugin.json, never at
        // the plugin root directly): Claude Code will not recognize a plugin without it there.
        var path = Path.Combine(PluginRoot, ".claude-plugin", "plugin.json");
        Assert.True(File.Exists(path), $"Missing {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path)); // throws JsonException if invalid
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal("hades", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void McpManifest_ExistsAtPluginRoot_AndIsValidJson()
    {
        var path = Path.Combine(PluginRoot, ".mcp.json");
        Assert.True(File.Exists(path), $"Missing {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void McpManifest_DeclaresHadesServer_AsHttpTypeAtTheDocumentedMcpPort_NeverARetypedLiteral()
    {
        var path = Path.Combine(PluginRoot, ".mcp.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        // A dedicated plugin-root .mcp.json's top level IS the server-name map directly (no
        // "mcpServers" wrapper - that wrapper only applies to the alternative inline-in-plugin.json
        // form). Verified against the real example-plugin shipped in the claude-plugins-official
        // marketplace on this machine.
        var hades = doc.RootElement.GetProperty("hades");
        Assert.Equal("http", hades.GetProperty("type").GetString());

        // The port must come from SettingsEndpoint.McpPort - the app's single source of truth -
        // never a re-typed 7823 literal that could silently drift from it.
        var expectedUrl = $"http://127.0.0.1:{SettingsEndpoint.McpPort}/mcp";
        Assert.Equal(expectedUrl, hades.GetProperty("url").GetString());
    }

    /// <summary>
    /// The repo root must not itself be an installable Claude Code plugin.
    ///
    /// It used to be: a root <c>.claude-plugin/plugin.json</c>, also named "hades". On any checkout
    /// where v1.2 had run, a generated root <c>.mcp.json</c> sat beside it launching
    /// <c>node Bridge~/launcher/dist/index.js</c> - the bridge to the MCP server that ran inside the
    /// Unity Editor. Pointing Claude Code at such a checkout <b>succeeded</b>, silently binding to
    /// the retired ~90-tool surface instead of the standalone app's 32. Nothing failed; it simply
    /// answered as the wrong generation of Hades.
    ///
    /// On a fresh clone the outcome was milder but still wrong: <c>.mcp.json</c> is gitignored as a
    /// machine-specific runtime artifact, so the install produced skills and commands with no MCP
    /// server at all.
    ///
    /// That is why this test exists rather than a doc note: on the machines that mattered most - the
    /// ones already running Hades - the failure was invisible at runtime, so the only durable fix is
    /// making the wrong door absent. The manifest was removed; Documentation/Retired/root-plugin-manifest.md records why.
    /// Restoring either file re-opens the path, so this fails loudly if they come back.
    /// </summary>
    [Fact]
    public void RepoRoot_IsNotItselfAnInstallablePlugin_SoPointingClaudeCodeHereCannotSilentlyLoadTheRetiredV12Surface()
    {
        var rootManifest = Path.Combine(RepoRoot, ".claude-plugin", "plugin.json");
        Assert.False(File.Exists(rootManifest),
            $"{rootManifest} exists again. The repo root must not be an installable plugin - it "
            + "would silently serve the retired v1.2 bridge instead of ClaudeCodePlugin. Move it "
            + "it, and see Documentation/Retired/root-plugin-manifest.md for why it must not exist.");

        var rootMcp = Path.Combine(RepoRoot, ".mcp.json");
        Assert.False(File.Exists(rootMcp),
            $"{rootMcp} exists again. A root .mcp.json makes this checkout installable as a plugin "
            + "and reintroduces the silent wrong-surface path. Move it it, and see Documentation/Retired/root-plugin-manifest.md for why it must not exist.");

        // The replacement must still be here - otherwise this test would "pass" on a checkout with
        // no plugin at all, which is not the state it is describing.
        Assert.True(File.Exists(Path.Combine(PluginRoot, ".claude-plugin", "plugin.json")),
            "ClaudeCodePlugin is missing its manifest, so there is no installable plugin at all.");
    }

    // -------------------------------------------------------------------------------- contents

    /// <summary>
    /// ClaudeCodePlugin/ is the single source of truth for the plugin. It used to be a copy: the
    /// authored skills and commands lived at the repo root (in the v1.2 UPM-package shape, with
    /// .meta files and hades- prefixed command filenames) and four tests here asserted the plugin's
    /// copies stayed byte-identical to them. The root pair went with the Unity package - nothing
    /// shipped or read it any more - so there is no second copy left to drift, and those four tests
    /// went with it. What still needs pinning is that the plugin ships the set it claims to:
    /// .github/workflows/release.yml refuses to publish on a count mismatch, and this catches the
    /// same mistake at `dotnet test` time instead of at release time.
    /// </summary>
    [Fact]
    public void Plugin_ShipsTheDocumentedNumberOfSkillsAndCommands()
    {
        var skills = Directory.GetFiles(Path.Combine(PluginRoot, "skills"), "SKILL.md", SearchOption.AllDirectories);
        Assert.True(skills.Length == 22,
            $"ClaudeCodePlugin ships {skills.Length} skills, not the documented 22. Update the README, "
            + "CONTRIBUTING, and release.yml's SKILL_COUNT gate together with this number - they are "
            + "each asserted separately and drift silently otherwise.");

        var commands = Directory.GetFiles(Path.Combine(PluginRoot, "commands"), "*.md");
        Assert.True(commands.Length == 6,
            $"ClaudeCodePlugin ships {commands.Length} commands, not the documented 6. Same lockstep "
            + "as the skill count above.");
    }

    [Fact]
    public void PluginCommandFilenames_NeverStutterTheHadesPrefix_ThePluginNamespaceAlreadySuppliesIt()
    {
        // The plugin is itself named "hades" (.claude-plugin/plugin.json), so Claude Code already
        // namespaces every command as /hades:<filename>. A command file still named
        // hades-status.md would register as /hades:hades-status - the exact stutter this guards
        // against, and the reason this project's own CLAUDE.md (which documents /hades:status)
        // disagreed with the shipped artifact before this fix.
        var pluginCommandsDir = Path.Combine(PluginRoot, "commands");
        Assert.True(Directory.Exists(pluginCommandsDir), $"Missing {pluginCommandsDir}");

        var stuttering = Directory.GetFiles(pluginCommandsDir, "*.md")
            .Select(f => Path.GetFileName(f)!)
            .Where(n => n.StartsWith("hades-", StringComparison.Ordinal))
            .ToList();

        Assert.True(stuttering.Count == 0,
            "Command filenames that still stutter the plugin's own namespace prefix: "
            + string.Join(", ", stuttering));
    }

    // ------------------------------------------------------------------------------------- hygiene

    [Fact]
    public void NoUnityMetaFilesShipInsideThePlugin()
    {
        // The ~ suffix keeps Unity from ever scanning ClaudeCodePlugin - nothing should hand it
        // Unity .meta crumbs to not-scan in the first place. Matches scripts/sync-plugin.sh's own
        // --exclude='*.meta' when it packages skills/commands from this same source today.
        Assert.True(Directory.Exists(PluginRoot), $"Missing {PluginRoot}");
        var metaFiles = Directory.GetFiles(PluginRoot, "*.meta", SearchOption.AllDirectories);
        Assert.True(metaFiles.Length == 0, "Unity .meta files leaked into the plugin: " + string.Join(", ", metaFiles));
    }

    // ------------------------------------------------------------------- retired-tool-name scan

    /// <summary>
    /// The 103-&gt;32 tool consolidation (see <see
    /// cref="RepoRoot_IsNotItselfAnInstallablePlugin_SoPointingClaudeCodeHereCannotSilentlyLoadTheRetiredV12Surface"/>'s
    /// own "retired ~90-tool surface" note above) left individual retired v1.2 tool names as an
    /// UNPINNED CLASS of regression: each time a skill or command markdown file was found still
    /// naming one, it was fixed as a one-off edit to that one file, never guarded as a class - so a
    /// future skill/command update could reintroduce a stale name unnoticed. This scans every
    /// shipped skill/command markdown file for a curated, conservative sample of retired v1.2 tool
    /// names.
    ///
    /// Each name below is verified retired: present in the v1.2 tree still checked in at the repo
    /// root's own <c>Editor/MCP/Tools/*.cs</c> (kept there for <c>V12Detector</c>/migration, not
    /// shipped), absent from every current <c>[McpServerTool(Name = ...)]</c> registration under
    /// <c>Core/src/Hades.Server/Mcp/*.cs</c> - and, deliberately, never a SUBSTRING of any of
    /// those 32 current names either, so a legitimate mention of a current tool (e.g.
    /// "scene_apply") can never trip this as a false positive.
    /// </summary>
    [Fact]
    public void ShippedSkillsAndCommands_NeverMentionARetiredV12ToolName()
    {
        string[] retiredToolNames =
        [
            "BeginScriptEditing", "EndScriptEditing", "analyze_render_pipeline", "animation_assign_clip",
            "animation_assign_controller", "animation_create_controller", "animation_edit_controller",
            "animation_get_controller", "asset_find", "asset_get_import_settings", "asset_get_info",
            "asset_import", "asset_move", "component_add", "component_find", "component_get_all",
            "component_remove", "component_set_properties", "component_set_property", "event_add_listener",
            "event_find_all", "event_list_listeners", "event_remove_listener", "find_orphan_scripts",
            "find_prefabs_with_component", "inspector_select", "layer_create", "layer_list",
            "material_assign", "material_create", "material_duplicate", "prefab_create",
            "prefab_create_variant", "prefab_instantiate", "reference_set", "scene_create_gameobject",
            "scene_setup", "tag_create", "tag_delete", "tag_list",
        ];

        var markdownFiles = Directory.GetFiles(Path.Combine(PluginRoot, "skills"), "*.md", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(PluginRoot, "commands"), "*.md", SearchOption.AllDirectories))
            .ToList();
        Assert.NotEmpty(markdownFiles);

        var offenders = new List<string>();
        foreach (var file in markdownFiles)
        {
            var text = File.ReadAllText(file);
            foreach (var name in retiredToolNames)
            {
                if (text.Contains(name, StringComparison.Ordinal))
                {
                    offenders.Add($"{file}: '{name}'");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Shipped skill/command markdown mentions retired v1.2 tool name(s): " + string.Join(", ", offenders));
    }
}
