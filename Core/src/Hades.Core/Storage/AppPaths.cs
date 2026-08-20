namespace Hades.Core.Storage;

/// <summary>
/// Resolves every path Hades owns. Instance-based with an override root so tests
/// never touch the real application-data directory. Callers own directory
/// creation for non-project paths (<c>Root</c>, <c>LogsDir</c>) — only
/// <see cref="EnsureProjectDir"/> creates anything.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null) => Root = root ?? DefaultRoot();

    public string Root { get; }

    public string ConfigFile => Path.Combine(Root, "config.json");
    public string LogsDir => Path.Combine(Root, "logs");
    public string ProjectsRoot => Path.Combine(Root, "projects");

    /// <summary>
    /// Where <see cref="Editors.EditorListener"/> writes the token a Unity Editor plugin presents
    /// before anything else on the socket, together with the actual (OS-assigned ephemeral) port
    /// to dial - see <c>Contract.Wire.EditorConnectionInfo</c>, the JSON shape written here. The
    /// plugin reads both on connect and on every reconnect attempt, since either can change if
    /// the app restarts. App-level, not per-project: a single listener authenticates editors for
    /// every known project from one socket, so there is one file, not one per project.
    /// </summary>
    public string EditorTokenFile => Path.Combine(Root, "editor.token");

    /// <summary>
    /// Where <c>Server.Control.ControlListener</c> writes the control API's discovery file - its
    /// own port and bearer token, on every listener <c>Start()</c>, for the Swift shell and a
    /// future <c>hades</c> CLI to read. Deliberately a file of its own, not
    /// <see cref="EditorTokenFile"/>: the control API and the editor link are different trust
    /// boundaries with independently generated tokens, and one file for both would risk one
    /// token being presented to the wrong listener. App-level, like <see cref="EditorTokenFile"/>
    /// - one control listener per app instance.
    /// </summary>
    public string ControlTokenFile => Path.Combine(Root, "control.token");

    /// <summary>
    /// The directory for a single project, keyed by its Unity product GUID.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="productGuid"/> is null/blank, is not a single path segment, or
    /// would resolve to <see cref="ProjectsRoot"/> itself or somewhere outside it. This
    /// is the single validation point for all per-project paths below, since each of
    /// them routes through this method.
    /// </exception>
    public string ProjectDir(string productGuid)
    {
        // Not ArgumentException.ThrowIfNullOrWhiteSpace: that throws ArgumentNullException on
        // null, and xUnit's Assert.Throws<T> matches the exact type, not subtypes.
        if (string.IsNullOrWhiteSpace(productGuid))
        {
            throw new ArgumentException("Project id must not be null or blank.", nameof(productGuid));
        }

        // Must be a single path segment: ProjectDir names one directory directly under
        // ProjectsRoot, never a nested path. The containment check below can't fully cover
        // this on its own — "sub/dir" resolves to a path that is still (nested) inside the
        // root, and "./" round-trips through GetFullPath as "<root>/" (trailing separator,
        // no distinct segment), which trivially satisfies StartsWith below.
        if (productGuid.Contains(Path.DirectorySeparatorChar) || productGuid.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                $"Invalid project id '{productGuid}': it must be a single path segment, not a nested path.",
                nameof(productGuid));
        }

        // Assert the invariant itself rather than enumerating ways to violate it: the resolved
        // path must be a STRICT child of ProjectsRoot. This catches ".", "..", rooted paths,
        // and whatever the next unenumerated case turns out to be.
        var projectsRoot = Path.GetFullPath(ProjectsRoot);
        var candidate = Path.GetFullPath(Path.Combine(projectsRoot, productGuid));

        if (candidate.Length <= projectsRoot.Length
            || !candidate.StartsWith(projectsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Invalid project id '{productGuid}': it must name a single directory inside the projects root.",
                nameof(productGuid));
        }

        return Path.Combine(ProjectsRoot, productGuid);
    }

    public string ProjectFile(string productGuid) => Path.Combine(ProjectDir(productGuid), "project.json");

    /// <summary>Derived — safe to delete; rebuilt on demand from the project's source of truth.</summary>
    public string GraphDb(string productGuid) => Path.Combine(ProjectDir(productGuid), "graph.db");

    /// <summary>Derived — safe to delete; rebuilt on demand from the project's source of truth.</summary>
    public string TracesDb(string productGuid) => Path.Combine(ProjectDir(productGuid), "traces.db");

    /// <summary>Authored — nothing regenerates this. Must never be deleted programmatically.</summary>
    public string MemoryDir(string productGuid) => Path.Combine(ProjectDir(productGuid), "memory");

    /// <summary>Derived — safe to delete; rebuilt on demand from <see cref="MemoryDir"/>, the
    /// authored source of truth it indexes.</summary>
    public string MemoryIndexPath(string productGuid) => Path.Combine(ProjectDir(productGuid), "memory-index.db");

    public string EnsureProjectDir(string productGuid)
    {
        var dir = ProjectDir(productGuid);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hades");
}
