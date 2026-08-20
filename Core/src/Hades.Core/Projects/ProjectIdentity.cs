using System.Text.RegularExpressions;

namespace Hades.Core.Projects;

/// <summary>
/// Project identity is Unity's own <c>productGUID</c> from ProjectSettings.asset.
/// Unity generates it per project and it is already committed; Hades never writes it.
/// Using it as the key means a project can move, be renamed, or be re-cloned without
/// orphaning its graph and memory.
/// </summary>
public static partial class ProjectIdentity
{
    // Only the head of the file is read: productGUID sits within the first PlayerSettings
    // block, and ProjectSettings.asset can be large.
    const int HeadBytes = 8 * 1024;

    [GeneratedRegex(@"productGUID:\s*([0-9a-fA-F]{32})")]
    private static partial Regex ProductGuidPattern();

    [GeneratedRegex(@"m_EditorVersion:\s*(\S+)")]
    private static partial Regex EditorVersionPattern();

    public static string SettingsPath(string projectRoot) =>
        Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset");

    public static string ProjectVersionPath(string projectRoot) =>
        Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");

    public static bool IsUnityProject(string projectRoot) => File.Exists(SettingsPath(projectRoot));

    /// <summary>
    /// Walks up from <paramref name="path"/> looking for ProjectSettings/ProjectSettings.asset,
    /// so a path pointing anywhere inside a project — Assets/Scripts, a single file — still
    /// resolves to the project root. Returns null when no ancestor is a Unity project.
    /// </summary>
    public static string? FindProjectRoot(string path)
    {
        var directory = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path).Directory;

        while (directory is not null)
        {
            if (IsUnityProject(directory.FullName)) return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Returns the lowercase productGUID, or null when the project is not a Unity project
    /// or the settings file is binary (Force Binary / Mixed serialization mode).
    /// </summary>
    public static string? TryReadProductGuid(string projectRoot)
    {
        var path = SettingsPath(projectRoot);
        if (!File.Exists(path)) return null;

        string head;
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(HeadBytes, stream.Length)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            head = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var match = ProductGuidPattern().Match(head);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Returns the Unity version this project was last opened with, read from Unity's own
    /// <c>ProjectSettings/ProjectVersion.txt</c> (<c>m_EditorVersion</c>) - or null when the file
    /// does not exist or is unreadable. Unlike <see cref="TryReadProductGuid"/>, there is no
    /// "binary" failure mode to report here: ProjectVersion.txt is always plain text, written by
    /// Unity itself, regardless of the project's asset serialization mode.
    /// </summary>
    public static string? TryReadUnityVersion(string projectRoot)
    {
        var path = ProjectVersionPath(projectRoot);
        if (!File.Exists(path)) return null;

        string head;
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(HeadBytes, stream.Length)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            head = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var match = EditorVersionPattern().Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }
}
