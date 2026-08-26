using Hades.Core.Storage;

namespace Hades.Core.Tests.Storage;

public class AppPathsTests : IDisposable
{
    string? _tempRoot;

    [Fact]
    public void DefaultRoot_IsUnderTheOsApplicationDataFolder()
    {
        var paths = new AppPaths();

        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), paths.Root);
        Assert.EndsWith("Hades", paths.Root);
    }

    [Fact]
    public void ProjectDir_IsKeyedByProductGuid()
    {
        var paths = new AppPaths("/tmp/hades-test");

        Assert.Equal("/tmp/hades-test/projects/15c012f27331e49229cef25e74537816",
            paths.ProjectDir("15c012f27331e49229cef25e74537816"));
    }

    [Fact]
    public void GraphDbPath_SitsInsideTheProjectDir()
    {
        var paths = new AppPaths("/tmp/hades-test");

        Assert.Equal("/tmp/hades-test/projects/abc/graph.db", paths.GraphDb("abc"));
    }

    [Fact]
    public void MemoryIndexPath_SitsInsideTheProjectDir()
    {
        var paths = new AppPaths("/tmp/hades-test");

        Assert.Equal("/tmp/hades-test/projects/abc/memory-index.db", paths.MemoryIndexPath("abc"));
    }

    [Fact]
    public void EditorTokenFile_SitsUnderRootNotAnyProject()
    {
        var paths = new AppPaths("/tmp/hades-test");

        // App-level, not per-project: one listener authenticates editors for every known
        // project, so the token lives beside config.json, not inside projects/<guid>/.
        Assert.Equal("/tmp/hades-test/editor.token", paths.EditorTokenFile);
    }

    [Fact]
    public void ControlTokenFile_SitsUnderRootNotAnyProject()
    {
        var paths = new AppPaths("/tmp/hades-test");

        // Its own file, deliberately separate from EditorTokenFile - see AppPaths.ControlTokenFile's
        // own doc comment for why a shared discovery file would be the wrong call here.
        Assert.Equal("/tmp/hades-test/control.token", paths.ControlTokenFile);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("a/b")]
    [InlineData(null)]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("a/../..")]
    [InlineData("sub/dir")]
    public void ProjectDir_RejectsAnyIdThatCouldEscapeTheProjectsRoot(string? productGuid)
    {
        var paths = new AppPaths("/tmp/hades-test");

        Assert.Throws<ArgumentException>(() => paths.ProjectDir(productGuid!));
    }

    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Windows)]
    public void DefaultRootIsMachineLocalOnWindows()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hades");

        Assert.Equal(expected, new AppPaths().Root);
    }

    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Unix)]
    public void DefaultRootIsApplicationSupportOnMac()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hades");

        Assert.Equal(expected, new AppPaths().Root);
    }

    [Fact]
    public void EnsureProjectDir_CreatesTheDirectory()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var paths = new AppPaths(_tempRoot);

        var dir = paths.EnsureProjectDir("abc");

        Assert.True(Directory.Exists(dir));
    }

    public void Dispose()
    {
        if (_tempRoot is not null && Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
