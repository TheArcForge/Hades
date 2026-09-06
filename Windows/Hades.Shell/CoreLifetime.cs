using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace Hades.Shell;

/// <summary>How the core will be launched, and which of the two paths was taken.</summary>
/// <param name="Executable">What to run.</param>
/// <param name="Arguments">Its arguments, or empty.</param>
/// <param name="IsInstalled">True for the shipped layout; false for the development fallback.</param>
public sealed record CoreLaunch(string Executable, string Arguments, bool IsInstalled);

/// <summary>
/// Locates the core. The port of <c>AppDelegate.makeConfiguration</c>, including its habit of saying
/// loudly which path it took.
///
/// Two paths, and the difference matters:
///
/// <list type="bullet">
/// <item><b>Installed</b> - <c>core\Hades.Server.exe</c> beside the shell, per Spec #5 §8.1. This is
/// the only path a distributed Hades ever takes.</item>
/// <item><b>Development</b> - <c>dotnet run --project &lt;repo&gt;\Core\src\Hades.Server
/// --no-launch-profile</c>. This needs the .NET SDK and this exact source checkout on THIS machine,
/// which is right for a developer build and never right for a shipped app - hence the warning.</item>
/// </list>
///
/// Resolution is a pure function of the two paths so it can be tested without either existing.
/// </summary>
public static class CoreLifetime
{
    /// <summary>
    /// The core's location, given where the shell is installed and where the source tree is.
    /// </summary>
    /// <param name="appDirectory">The shell's own directory.</param>
    /// <param name="repositoryRoot">The source checkout, for the development fallback.</param>
    /// <param name="fileExists">Injected so a test need not create a real executable.</param>
    public static CoreLaunch Resolve(
        string appDirectory,
        string repositoryRoot,
        Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;

        var installed = Path.Combine(appDirectory, "core", "Hades.Server.exe");
        if (exists(installed))
        {
            return new CoreLaunch(installed, string.Empty, IsInstalled: true);
        }

        var project = Path.Combine(repositoryRoot, "Core", "src", "Hades.Server");

        // --no-launch-profile so launchSettings.json cannot quietly change the port or environment
        // out from under the supervisor - the Mac's own fallback passes it for the same reason.
        return new CoreLaunch(
            "dotnet",
            $"run --project \"{project}\" --no-launch-profile",
            IsInstalled: false);
    }

    /// <summary>
    /// Resolves against this build's own locations and says which path it took.
    ///
    /// Logging goes to <see cref="Trace"/> rather than a file: the fallback only ever happens during
    /// development, where a developer has a debugger or DebugView attached, and a tray app that
    /// started writing its own log file would be inventing a logging story this plan never asked for.
    /// </summary>
    public static CoreLaunch ResolveForThisBuild()
    {
        var launch = Resolve(AppContext.BaseDirectory, RepositoryRoot());

        if (launch.IsInstalled)
        {
            Trace.WriteLine($"[Hades] Launching the installed core: {launch.Executable}");
        }
        else
        {
            Trace.WriteLine(
                $"[Hades] No core found at {Path.Combine(AppContext.BaseDirectory, "core", "Hades.Server.exe")} - "
                + $"falling back to `dotnet {launch.Arguments}`. This needs the .NET SDK and this exact source "
                + "checkout on THIS machine; expected for a developer build, never for an installed Hades.");
        }

        return launch;
    }

    /// <summary>
    /// The repository root, derived from this file's own compile-time path rather than from the
    /// build output's directory depth - the C# equivalent of the Swift original's <c>#filePath</c>.
    /// Walking up from <c>bin\Debug\net10.0-windows</c> instead would break the moment a
    /// configuration or TFM changed the nesting.
    /// </summary>
    static string RepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        // CoreLifetime.cs -> Hades.Shell\ -> Windows\ -> repo root
        var shellDirectory = Path.GetDirectoryName(sourcePath);
        var windowsDirectory = Path.GetDirectoryName(shellDirectory);

        return Path.GetDirectoryName(windowsDirectory) ?? string.Empty;
    }
}
