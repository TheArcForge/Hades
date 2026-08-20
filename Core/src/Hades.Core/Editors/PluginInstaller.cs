using System.Reflection;
using System.Text.RegularExpressions;

namespace Hades.Core.Editors;

/// <summary>
/// Writes (or updates in place) <c>Assets/Hades/</c> into a target Unity project: the asmdef,
/// <c>Contract/</c>, <c>Runtime/</c>, <c>Transport/</c> - the plugin from <see cref="EditorListener"/>'s
/// own doc comment, the app-side half of the editor link.
///
/// <b>Where the source content comes from:</b> embedded assembly resources, not files read off
/// disk at install time. The app is a compiled binary and <c>UnityPlugin/</c> is a source tree that
/// lives only in this repo's checkout - once packaged (spec #4), nothing guarantees that tree
/// still sits at some fixed path relative to the running executable. Two options exist for
/// getting the plugin's bytes from "this repo, at build time" to "a stranger's Unity project, at
/// install time": embed them as resources baked into this assembly, or copy them to a folder
/// beside the built binary and read them from there at runtime. This class takes the first
/// option. Embedding wins because it has no dependency on deployment layout at all - the bytes
/// travel inside the same DLL that contains this code, so they survive a `dotnet publish`, a move
/// to a different directory, or being bundled into a notarized macOS .app (spec #3), with no
/// second thing to keep in sync. The alternative (files beside the binary) works too, but only for
/// as long as "beside the binary" keeps meaning the same directory relationship it means today -
/// exactly the fragility spec #4's packaging work would be free to break. See
/// <c>Hades.Core.csproj</c>'s <c>&lt;EmbeddedResource&gt;</c> block for what is embedded and why
/// <c>Contract/</c> specifically comes from <c>Hades.Contract/Wire/</c> (the sources this app
/// itself compiled against) rather than the separately maintained <c>UnityPlugin/Assets/Hades/Contract/</c>
/// copy used for Unity-side development - that choice is what makes "the app always ships the
/// contract it compiled against" true by construction instead of by discipline.
///
/// <b>Standing project rule:</b> writes no <c>.meta</c> files and mints no GUIDs. Unity generates
/// both on its next Editor refresh; anything this class wrote here would either collide with that
/// assignment or go stale the moment Unity re-derives it.
///
/// <b>Scope:</b> touches only <c>Assets/Hades/</c>. Nothing under <c>Packages/</c>,
/// <c>ProjectSettings/</c>, or any project-root file (<c>.mcp.json</c>, <c>CLAUDE.md</c>, ...) is
/// read, created, or modified.
/// </summary>
public static class PluginInstaller
{
    /// <summary>Embedded resource logical name (see the csproj) mapped to its path relative to
    /// <c>Assets/Hades/</c> in the target project. A fixed, explicitly listed set rather than
    /// something derived by scanning the assembly's resource names at runtime - the plugin's file
    /// list changes rarely enough that explicit beats clever, and a mismatch here fails loudly
    /// (see <see cref="Install"/>) rather than silently installing an incomplete tree.</summary>
    static readonly (string Resource, string RelativePath)[] Files =
    [
        ("Hades.Plugin.Hades.asmdef", "Hades.asmdef"),
        ("Hades.Plugin.Contract.EditorConnectionInfo.cs", "Contract/EditorConnectionInfo.cs"),
        ("Hades.Plugin.Contract.Hello.cs", "Contract/Hello.cs"),
        ("Hades.Plugin.Contract.JsonRpc.cs", "Contract/JsonRpc.cs"),
        ("Hades.Plugin.Contract.MiniJson.cs", "Contract/MiniJson.cs"),
        ("Hades.Plugin.Runtime.HadesBoot.cs", "Runtime/HadesBoot.cs"),
        ("Hades.Plugin.Runtime.IEditorLockApi.cs", "Runtime/IEditorLockApi.cs"),
        ("Hades.Plugin.Runtime.MainThreadPump.cs", "Runtime/MainThreadPump.cs"),
        ("Hades.Plugin.Runtime.ReloadGate.cs", "Runtime/ReloadGate.cs"),
        ("Hades.Plugin.Runtime.ReloadLease.cs", "Runtime/ReloadLease.cs"),
        ("Hades.Plugin.Tools.AssetPathGuard.cs", "Tools/AssetPathGuard.cs"),
        ("Hades.Plugin.Tools.CommandTable.cs", "Tools/CommandTable.cs"),
        ("Hades.Plugin.Tools.SceneCommands.cs", "Tools/SceneCommands.cs"),
        ("Hades.Plugin.Tools.SceneApplyCommands.cs", "Tools/SceneApplyCommands.cs"),
        ("Hades.Plugin.Tools.ComponentCommands.cs", "Tools/ComponentCommands.cs"),
        ("Hades.Plugin.Tools.MaterialCommands.cs", "Tools/MaterialCommands.cs"),
        ("Hades.Plugin.Tools.MaterialApplyCommands.cs", "Tools/MaterialApplyCommands.cs"),
        ("Hades.Plugin.Tools.AnimationCommands.cs", "Tools/AnimationCommands.cs"),
        ("Hades.Plugin.Tools.AnimationApplyCommands.cs", "Tools/AnimationApplyCommands.cs"),
        ("Hades.Plugin.Tools.TagLayerCommands.cs", "Tools/TagLayerCommands.cs"),
        ("Hades.Plugin.Tools.SceneManagementCommands.cs", "Tools/SceneManagementCommands.cs"),
        ("Hades.Plugin.Tools.AssetCommands.cs", "Tools/AssetCommands.cs"),
        ("Hades.Plugin.Tools.AssetManageCommands.cs", "Tools/AssetManageCommands.cs"),
        ("Hades.Plugin.Tools.SceneManageCommands.cs", "Tools/SceneManageCommands.cs"),
        ("Hades.Plugin.Tools.ProjectSettingsApplyCommands.cs", "Tools/ProjectSettingsApplyCommands.cs"),
        ("Hades.Plugin.Tools.InspectorCommands.cs", "Tools/InspectorCommands.cs"),
        ("Hades.Plugin.Tools.PrefabCommands.cs", "Tools/PrefabCommands.cs"),
        ("Hades.Plugin.Tools.PrefabApplyCommands.cs", "Tools/PrefabApplyCommands.cs"),
        ("Hades.Plugin.Tools.ProjectCommands.cs", "Tools/ProjectCommands.cs"),
        ("Hades.Plugin.Transport.HadesClient.cs", "Transport/HadesClient.cs"),
        ("Hades.Plugin.Transport.HadesConnectionFile.cs", "Transport/HadesConnectionFile.cs"),
    ];

    /// <summary>
    /// Installs into <c>&lt;projectRoot&gt;/Assets/Hades/</c>, creating it if absent or updating
    /// every file in place if present - idempotent either way, since each file is simply
    /// overwritten with the same embedded bytes every time. Safe to call on a project this has
    /// never touched or one it installed into a dozen times before; the result is identical.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="projectRoot"/> is null or blank.</exception>
    /// <exception cref="InvalidOperationException">An entry in <see cref="Files"/> has no
    /// matching embedded resource - a build defect (the csproj's embed list and this list have
    /// drifted apart), never a user-facing condition.</exception>
    public static void Install(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        var targetRoot = Path.Combine(projectRoot, "Assets", "Hades");
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var (resource, relativePath) in Files)
        {
            var destination = Path.Combine(targetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var resourceStream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException(
                    $"Missing embedded plugin resource '{resource}' — Hades.Core.csproj's "
                    + "EmbeddedResource list and PluginInstaller.Files have drifted apart.");

            // Create (not Open/Append): each install run replaces the file wholesale, which is
            // what makes both idempotence and "update in place" trivially true - there is no
            // partial-write or merge logic that could diverge between runs.
            using var fileStream = File.Create(destination);
            resourceStream.CopyTo(fileStream);
        }
    }

    /// <summary>Matches HadesBoot.cs's own <c>public const string PluginVersion = "1.2.0";</c> -
    /// used identically against the embedded resource bytes (<see cref="AppPluginVersion"/>) and
    /// an installed copy on disk (<see cref="InstalledPluginVersion"/>), so both readings can
    /// never disagree about what "the version" means syntactically - only about which copy they
    /// read.</summary>
    static readonly Regex PluginVersionPattern = new(@"PluginVersion\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    /// <summary>
    /// The plugin version this app currently ships - read from the SAME embedded resource bytes
    /// <see cref="Install"/> writes to disk, rather than a second literal kept in sync by hand.
    /// This is what makes a version comparison against <see cref="InstalledPluginVersion"/>
    /// trustworthy: "the app's version" can never drift from "what Install() would actually
    /// write" because they are, byte for byte, the same source. Null only when the embedded
    /// resource is missing (the same build defect <see cref="Install"/> itself throws on) or does
    /// not contain a recognisable <c>PluginVersion</c> constant.
    /// </summary>
    public static string? AppPluginVersion()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Hades.Plugin.Runtime.HadesBoot.cs");
        if (stream is null) return null;

        using var reader = new StreamReader(stream);
        var match = PluginVersionPattern.Match(reader.ReadToEnd());
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The plugin version currently installed at <c>&lt;projectRoot&gt;/Assets/Hades/Runtime/HadesBoot.cs</c>,
    /// or null when the plugin is not installed there at all, or the file is unreadable or does
    /// not contain a recognisable <c>PluginVersion</c> constant (for example, hand-edited). "Not
    /// installed" and "installed but unreadable" are deliberately collapsed to the same null here:
    /// either way there is no version to compare against <see cref="AppPluginVersion"/>, so
    /// nothing can be said to mismatch.
    /// </summary>
    public static string? InstalledPluginVersion(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "Assets", "Hades", "Runtime", "HadesBoot.cs");
        if (!File.Exists(path)) return null;

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var match = PluginVersionPattern.Match(content);
        return match.Success ? match.Groups[1].Value : null;
    }
}
