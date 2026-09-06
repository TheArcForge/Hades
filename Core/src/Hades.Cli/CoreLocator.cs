using System.Runtime.CompilerServices;

namespace Hades.Cli;

/// <summary>How the core will be launched. <see cref="WorkingDirectory"/> is null when it does not
/// matter.</summary>
public sealed record CoreLaunch(string Executable, string Arguments, string? WorkingDirectory);

/// <summary>
/// Finds the core for <c>hades serve</c>. The cross-platform sibling of the Windows shell's own
/// <c>CoreLifetime</c>, and it looks in the same places for the same reasons.
///
/// Three candidates, in order:
///
/// <list type="number">
/// <item><c>Hades.Server</c> beside this executable - the layout an installed CLI ships in.</item>
/// <item><c>core/Hades.Server</c> beside it - the layout the Windows shell installs, so a CLI
/// sitting next to the shell finds the shell's core rather than needing its own copy.</item>
/// <item><c>../HadesServer/Hades.Server</c> - the macOS app bundle. build-app.sh publishes the CLI
/// into <c>Contents/Resources/HadesCli/</c> and the core into <c>Contents/Resources/HadesServer/</c>,
/// so they are siblings; resolving RELATIVE to this executable is what lets the
/// <c>/usr/local/bin/hades</c> symlink work, since the symlink target is inside the bundle.</item>
/// <item><c>dotnet run --project &lt;repo&gt;/Core/src/Hades.Server --no-launch-profile</c> - the
/// development fallback, which needs the .NET SDK and this exact source checkout.</item>
/// </list>
///
/// Returns null only when even the source tree is absent, which is the one case where there is
/// genuinely nothing to run and saying so beats guessing.
/// </summary>
public static class CoreLocator
{
    public static CoreLaunch? Resolve(
        string? appDirectory = null,
        string? repositoryRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null)
    {
        var directory = appDirectory ?? AppContext.BaseDirectory;
        var repository = repositoryRoot ?? RepositoryRoot();
        var exists = fileExists ?? File.Exists;
        var directoryPresent = directoryExists ?? Directory.Exists;

        // Windows names it .exe; every other platform ships the bare apphost.
        var executableName = OperatingSystem.IsWindows() ? "Hades.Server.exe" : "Hades.Server";

        var beside = Path.Combine(directory, executableName);
        if (exists(beside)) return new CoreLaunch(beside, string.Empty, Path.GetDirectoryName(beside));

        var inCoreFolder = Path.Combine(directory, "core", executableName);
        if (exists(inCoreFolder)) return new CoreLaunch(inCoreFolder, string.Empty, Path.GetDirectoryName(inCoreFolder));

        // The macOS bundle: Contents/Resources/HadesCli/hades and Contents/Resources/HadesServer/
        // are siblings. Resolved from AppContext.BaseDirectory, which is the REAL directory even
        // when invoked through the /usr/local/bin symlink - so the symlink needs no knowledge of
        // where the bundle lives.
        var sibling = Path.Combine(directory, "..", "HadesServer", executableName);
        if (exists(sibling)) return new CoreLaunch(Path.GetFullPath(sibling), string.Empty, Path.GetDirectoryName(Path.GetFullPath(sibling)));

        var project = Path.Combine(repository, "Core", "src", "Hades.Server");
        if (!directoryPresent(project)) return null;

        // --no-launch-profile so launchSettings.json cannot change the port or environment out from
        // under whoever is about to adopt this core.
        return new CoreLaunch("dotnet", $"run --project \"{project}\" --no-launch-profile", null);
    }

    /// <summary>
    /// The repository root from this file's own compile-time path - the same technique the shell's
    /// CoreLifetime uses, and for the same reason: counting directories up from a build output
    /// breaks the moment a configuration or target framework changes the nesting.
    /// </summary>
    static string RepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        // CoreLocator.cs -> Hades.Cli\ -> src\ -> Core\ -> repo root
        var cli = Path.GetDirectoryName(sourcePath);
        var src = Path.GetDirectoryName(cli);
        var core = Path.GetDirectoryName(src);

        return Path.GetDirectoryName(core) ?? string.Empty;
    }
}
