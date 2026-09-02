using Hades.Shell;

namespace Hades.Shell.Tests;

public class CoreLifetimeTests
{
    const string AppDir = @"C:\Program Files\Hades";
    const string RepoRoot = @"D:\Fork\Hades";

    static string InstalledCore => System.IO.Path.Combine(AppDir, "core", "Hades.Server.exe");

    [Fact]
    public void PrefersTheInstalledCoreBesideTheShell()
    {
        var launch = CoreLifetime.Resolve(AppDir, RepoRoot, path => path == InstalledCore);

        Assert.True(launch.IsInstalled);
        Assert.Equal(InstalledCore, launch.Executable);
        Assert.Equal(string.Empty, launch.Arguments);
    }

    /// <summary>
    /// The development fallback needs the .NET SDK and this exact source checkout, so it must only
    /// ever apply when there is no installed core - never as a silent second choice at runtime.
    /// </summary>
    [Fact]
    public void FallsBackToDotnetRunWhenNoCoreIsInstalled()
    {
        var launch = CoreLifetime.Resolve(AppDir, RepoRoot, _ => false);

        Assert.False(launch.IsInstalled);
        Assert.Equal("dotnet", launch.Executable);
        Assert.Contains(@"D:\Fork\Hades\Core\src\Hades.Server", launch.Arguments);
    }

    /// <summary>
    /// --no-launch-profile is not optional: without it launchSettings.json can change the port or
    /// environment out from under the supervisor, which then adopts or spawns against the wrong one.
    /// </summary>
    [Fact]
    public void TheFallbackSuppressesTheLaunchProfile()
    {
        var launch = CoreLifetime.Resolve(AppDir, RepoRoot, _ => false);

        Assert.Contains("--no-launch-profile", launch.Arguments);
    }

    /// <summary>The project path is quoted, or a repo checked out under a path with spaces - which
    /// "C:\Program Files" and most Windows user directories are - would split into two arguments.</summary>
    [Fact]
    public void TheFallbackQuotesTheProjectPath()
    {
        var launch = CoreLifetime.Resolve(AppDir, @"C:\Users\Some One\Hades", _ => false);

        Assert.Contains(@"""C:\Users\Some One\Hades\Core\src\Hades.Server""", launch.Arguments);
    }

    [Fact]
    public void LooksForTheCoreInTheShellsOwnCoreSubdirectory()
    {
        string? probed = null;
        CoreLifetime.Resolve(AppDir, RepoRoot, path => { probed = path; return false; });

        Assert.Equal(InstalledCore, probed);
    }
}
