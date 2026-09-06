namespace Hades.Cli.Tests;

/// <summary>
/// Where <c>hades serve</c> looks for the core. Four candidates in a fixed order, and the order is
/// the point: an installed layout must never be beaten by a development fallback that happens to
/// also be present on a maintainer's machine.
/// </summary>
public class CoreLocatorTests
{
    const string AppDir = @"C:\Program Files\Hades";
    const string RepoRoot = @"D:\Fork\Hades";

    static string ExecutableName => OperatingSystem.IsWindows() ? "Hades.Server.exe" : "Hades.Server";

    static CoreLaunch? Resolve(params string[] existingFiles) =>
        CoreLocator.Resolve(
            AppDir,
            RepoRoot,
            fileExists: path => existingFiles.Contains(path, StringComparer.OrdinalIgnoreCase),
            directoryExists: _ => true);

    [Fact]
    public void PrefersTheCoreSittingBesideTheCli()
    {
        var beside = Path.Combine(AppDir, ExecutableName);

        var launch = Resolve(beside);

        Assert.NotNull(launch);
        Assert.Equal(beside, launch!.Executable);
        Assert.Equal(string.Empty, launch.Arguments);
    }

    /// <summary>The Windows shell's install layout: a CLI beside the shell finds the shell's core
    /// rather than needing a second copy of it.</summary>
    [Fact]
    public void FindsTheCoreInTheShellsCoreSubdirectory()
    {
        var inCoreFolder = Path.Combine(AppDir, "core", ExecutableName);

        var launch = Resolve(inCoreFolder);

        Assert.Equal(inCoreFolder, launch!.Executable);
    }

    /// <summary>
    /// The macOS app bundle: build-app.sh puts the CLI in Contents/Resources/HadesCli/ and the core
    /// in Contents/Resources/HadesServer/, so they are siblings. Resolving relative to this
    /// executable is what lets the /usr/local/bin symlink work without knowing where the bundle is.
    /// </summary>
    [Fact]
    public void FindsTheCoreAsASiblingInTheMacOsBundleLayout()
    {
        var sibling = Path.Combine(AppDir, "..", "HadesServer", ExecutableName);

        var launch = Resolve(sibling);

        Assert.NotNull(launch);
        // Returned fully resolved, so the spawned process does not carry a ".." through its own
        // command line and working directory.
        Assert.DoesNotContain("..", launch!.Executable);
        Assert.EndsWith(Path.Combine("HadesServer", ExecutableName), launch.Executable);
    }

    [Fact]
    public void FallsBackToDotnetRunWhenNoCoreIsInstalled()
    {
        var launch = Resolve();

        Assert.NotNull(launch);
        Assert.Equal("dotnet", launch!.Executable);
        Assert.Contains("--no-launch-profile", launch.Arguments);
        Assert.Contains(Path.Combine("Core", "src", "Hades.Server"), launch.Arguments);
    }

    /// <summary>
    /// The one case with genuinely nothing to run. Saying so beats spawning `dotnet run` against a
    /// project directory that is not there and surfacing the SDK's error instead of ours.
    /// </summary>
    [Fact]
    public void ReturnsNullWhenEvenTheSourceTreeIsAbsent()
    {
        var launch = CoreLocator.Resolve(
            AppDir, RepoRoot, fileExists: _ => false, directoryExists: _ => false);

        Assert.Null(launch);
    }

    /// <summary>
    /// Order matters: an installed core wins over the development fallback even on a machine that
    /// has both, which is every maintainer's machine.
    /// </summary>
    [Fact]
    public void AnInstalledCoreBeatsTheDevelopmentFallback()
    {
        var launch = Resolve(Path.Combine(AppDir, ExecutableName));

        Assert.NotEqual("dotnet", launch!.Executable);
    }

    /// <summary>The working directory is the core's own: it reads appsettings.json from the current
    /// directory, so launching it from anywhere else silently drops its configuration.</summary>
    [Fact]
    public void AnInstalledCoreRunsFromItsOwnDirectory()
    {
        var beside = Path.Combine(AppDir, ExecutableName);

        var launch = Resolve(beside);

        Assert.Equal(AppDir, launch!.WorkingDirectory);
    }
}
