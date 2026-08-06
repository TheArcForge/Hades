using System.Text.Json;
using Hades.Server.Control;

namespace Hades.Server.Tests;

/// <summary>
/// Plan 14 Task 1 (spec #4 §1-§2): <c>Plugin-ClaudeCode~/</c> is the second of the three install
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
///    <c>scripts/sync-plugin.sh</c> already packages from for the pre-existing (not locally
///    installed) <c>.claude-plugin/</c> proto-plugin. Byte-identical comparison mirrors
///    <see cref="PluginWireContractTests"/>'s own reasoning for <c>Plugin~/Contract</c>: a copy
///    that can drift from its source is worse than no copy, and hand-retyped content is exactly
///    the kind of copy that drifts silently.
///
/// Repo-root discovery walks up from <see cref="AppContext.BaseDirectory"/> the same way
/// <c>PluginRequiredFields.FindPluginToolsDirectory</c> finds <c>Plugin~</c> - landmark directories
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
            if (Directory.Exists(Path.Combine(dir.FullName, "App~"))
                && Directory.Exists(Path.Combine(dir.FullName, "skills"))
                && Directory.Exists(Path.Combine(dir.FullName, "commands")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the Hades repo root (App~ + skills + commands siblings) by walking up "
            + "from " + AppContext.BaseDirectory + ". Is this test running from within the normal "
            + "App~/tests checkout layout?");
    });

    static string RepoRoot => LazyRepoRoot.Value;
    static string PluginRoot => Path.Combine(RepoRoot, "Plugin-ClaudeCode~");
    static string SourceSkillsDir => Path.Combine(RepoRoot, "skills");
    static string SourceCommandsDir => Path.Combine(RepoRoot, "commands");

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

    // -------------------------------------------------------------------------------- skills parity

    [Fact]
    public void EveryShippedSkill_IsPresentInThePlugin_ByteIdenticalToTheSourceOfTruth()
    {
        var sourceSkillNames = Directory.GetDirectories(SourceSkillsDir)
            .Select(d => Path.GetFileName(d)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // Not asserting a hardcoded "22" - this fails honestly if the source of truth itself
        // changes, rather than silently agreeing with a count that may already be stale (the task
        // brief explicitly asks to report the real number, not pad to match a stale spec figure).
        Assert.NotEmpty(sourceSkillNames);

        var missing = new List<string>();
        var diverged = new List<string>();
        foreach (var name in sourceSkillNames)
        {
            var sourceFile = Path.Combine(SourceSkillsDir, name, "SKILL.md");
            var pluginFile = Path.Combine(PluginRoot, "skills", name, "SKILL.md");
            if (!File.Exists(pluginFile)) { missing.Add(name); continue; }
            if (!FileBytesEqual(sourceFile, pluginFile)) diverged.Add(name);
        }

        Assert.True(missing.Count == 0,
            "Skills missing from Plugin-ClaudeCode~/skills: " + string.Join(", ", missing));
        Assert.True(diverged.Count == 0,
            "Skills whose SKILL.md differs from the source of truth (hand-retyped instead of "
            + "copied?): " + string.Join(", ", diverged));
    }

    [Fact]
    public void PluginSkillsDirectory_ShipsExactlyTheSourceOfTruthSet_NoStaleExtras()
    {
        var sourceNames = Directory.GetDirectories(SourceSkillsDir).Select(d => Path.GetFileName(d)!).ToHashSet();
        var pluginSkillsDir = Path.Combine(PluginRoot, "skills");
        Assert.True(Directory.Exists(pluginSkillsDir), $"Missing {pluginSkillsDir}");

        var pluginNames = Directory.GetDirectories(pluginSkillsDir).Select(d => Path.GetFileName(d)!).ToHashSet();
        Assert.True(pluginNames.SetEquals(sourceNames),
            "Plugin-ClaudeCode~/skills/ set differs from the source skills/ set. "
            + $"Extra in plugin: [{string.Join(", ", pluginNames.Except(sourceNames))}]. "
            + $"Missing from plugin: [{string.Join(", ", sourceNames.Except(pluginNames))}].");
    }

    // ------------------------------------------------------------------------------ commands parity

    [Fact]
    public void EveryShippedCommand_IsPresentInThePlugin_ByteIdenticalToTheSourceOfTruth_UnderItsUnprefixedName()
    {
        var sourceCommandFiles = Directory.GetFiles(SourceCommandsDir, "*.md")
            .Select(f => Path.GetFileName(f)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(sourceCommandFiles);

        var missing = new List<string>();
        var diverged = new List<string>();
        foreach (var sourceName in sourceCommandFiles)
        {
            var pluginName = PluginCommandNameFor(sourceName);
            var sourceFile = Path.Combine(SourceCommandsDir, sourceName);
            var pluginFile = Path.Combine(PluginRoot, "commands", pluginName);
            if (!File.Exists(pluginFile)) { missing.Add(pluginName); continue; }
            if (!FileBytesEqual(sourceFile, pluginFile)) diverged.Add(pluginName);
        }

        Assert.True(missing.Count == 0,
            "Commands missing from Plugin-ClaudeCode~/commands (expected under their unprefixed "
            + "name): " + string.Join(", ", missing));
        Assert.True(diverged.Count == 0,
            "Commands whose content differs from the source of truth (hand-retyped instead of "
            + "copied?): " + string.Join(", ", diverged));
    }

    [Fact]
    public void PluginCommandsDirectory_ShipsExactlyTheSourceOfTruthSet_UnprefixedNoStaleExtras()
    {
        var expectedPluginNames = Directory.GetFiles(SourceCommandsDir, "*.md")
            .Select(f => PluginCommandNameFor(Path.GetFileName(f)!))
            .ToHashSet();
        var pluginCommandsDir = Path.Combine(PluginRoot, "commands");
        Assert.True(Directory.Exists(pluginCommandsDir), $"Missing {pluginCommandsDir}");

        var pluginNames = Directory.GetFiles(pluginCommandsDir, "*.md").Select(f => Path.GetFileName(f)!).ToHashSet();
        Assert.True(pluginNames.SetEquals(expectedPluginNames),
            "Plugin-ClaudeCode~/commands/ set differs from the source commands/ set (unprefixed). "
            + $"Extra in plugin: [{string.Join(", ", pluginNames.Except(expectedPluginNames))}]. "
            + $"Missing from plugin: [{string.Join(", ", expectedPluginNames.Except(pluginNames))}].");
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
        // The ~ suffix keeps Unity from ever scanning Plugin-ClaudeCode~ - nothing should hand it
        // Unity .meta crumbs to not-scan in the first place. Matches scripts/sync-plugin.sh's own
        // --exclude='*.meta' when it packages skills/commands from this same source today.
        Assert.True(Directory.Exists(PluginRoot), $"Missing {PluginRoot}");
        var metaFiles = Directory.GetFiles(PluginRoot, "*.meta", SearchOption.AllDirectories);
        Assert.True(metaFiles.Length == 0, "Unity .meta files leaked into the plugin: " + string.Join(", ", metaFiles));
    }
}
