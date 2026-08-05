using Hades.Core.Projects;
using Hades.Core.Storage;

namespace Hades.Core.Tests.Projects;

public class ProjectStoreTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    ProjectStore NewStore() => new(new AppPaths(_appRoot));

    void MakeUnityProject(string guid)
    {
        var dir = Path.Combine(_projectRoot, "ProjectSettings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ProjectSettings.asset"), $"  productGUID: {guid}\n");
    }

    [Fact]
    public void Adopt_RegistersAUnityProject()
    {
        MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");

        var project = NewStore().Adopt(_projectRoot);

        Assert.NotNull(project);
        Assert.Equal("aaaabbbbccccddddeeeeffff00001111", project!.ProductGuid);
        Assert.Equal(_projectRoot, project.Path);
        Assert.Equal(Path.GetFileName(_projectRoot), project.Name);
    }

    [Fact]
    public void Adopt_ReturnsNullForNonUnityDirectory()
    {
        Directory.CreateDirectory(_projectRoot);

        Assert.Null(NewStore().Adopt(_projectRoot));
    }

    [Fact]
    public void Adopt_PersistsAcrossInstances()
    {
        MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        NewStore().Adopt(_projectRoot);

        var reloaded = NewStore().Get("aaaabbbbccccddddeeeeffff00001111");

        Assert.NotNull(reloaded);
        Assert.Equal(_projectRoot, reloaded!.Path);
    }

    [Fact]
    public void Adopt_UpdatesPathWhenProjectMoves()
    {
        MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        NewStore().Adopt(_projectRoot);

        var movedRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(movedRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(movedRoot, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: aaaabbbbccccddddeeeeffff00001111\n");

        var store = NewStore();
        store.Adopt(movedRoot);

        Assert.Single(store.All());
        Assert.Equal(movedRoot, store.Get("aaaabbbbccccddddeeeeffff00001111")!.Path);

        Directory.Delete(movedRoot, recursive: true);
    }

    [Fact]
    public void All_ListsEveryKnownProject()
    {
        MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var store = NewStore();
        store.Adopt(_projectRoot);

        Assert.Single(store.All());
    }

    [Fact]
    public void Adopt_QuarantinesCorruptProjectFileInsteadOfDiscardingIt()
    {
        const string guid = "aaaabbbbccccddddeeeeffff00001111";
        MakeUnityProject(guid);
        var store = NewStore();
        store.Adopt(_projectRoot);

        // Simulate the file an interrupted write (or a later schema change adding a new
        // required member) would leave behind: it exists, but Get() cannot deserialize it.
        var projectFile = new AppPaths(_appRoot).ProjectFile(guid);
        File.WriteAllText(projectFile, "{ not valid json");

        var readopted = store.Adopt(_projectRoot);

        Assert.NotNull(readopted);
        Assert.True(File.Exists(projectFile + ".corrupt"));
        Assert.Equal("{ not valid json", File.ReadAllText(projectFile + ".corrupt"));
    }

    [Fact]
    public void Adopt_AbortsRatherThanQuarantiningATransientlyUnreadableFile()
    {
        // A file that is merely unreadable at this instant (a lock, a permission blip) is
        // not evidence of corruption. Quarantining it would destroy a perfectly good record —
        // exactly the data loss the quarantine mechanism exists to prevent, now triggered by
        // a transient read failure instead of by actual corruption.
        if (OperatingSystem.IsWindows())
        {
            return; // File.SetUnixFileMode is a Unix chmod equivalent; Windows ACLs differ.
        }

        if (Environment.IsPrivilegedProcess)
        {
            return; // Root bypasses permission bits entirely; the chmod below would not bite.
        }

        const string guid = "aaaabbbbccccddddeeeeffff00001111";
        MakeUnityProject(guid);
        var store = NewStore();
        store.Adopt(_projectRoot);

        var projectFile = new AppPaths(_appRoot).ProjectFile(guid);
        var originalContent = File.ReadAllText(projectFile);

        File.SetUnixFileMode(projectFile, UnixFileMode.None);

        UnityProject? result;
        try
        {
            result = store.Adopt(_projectRoot);
        }
        finally
        {
            // Restore permissions so Dispose() can delete the directory, and so the
            // assertions below can read the file back.
            File.SetUnixFileMode(projectFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Null(result);
        Assert.False(File.Exists(projectFile + ".corrupt"));
        Assert.Equal(originalContent, File.ReadAllText(projectFile));
    }

    [Fact]
    public void Adopt_SecondCorruptionDoesNotOverwriteTheFirstQuarantineFile()
    {
        const string guid = "aaaabbbbccccddddeeeeffff00001111";
        MakeUnityProject(guid);
        var store = NewStore();
        store.Adopt(_projectRoot);

        var projectFile = new AppPaths(_appRoot).ProjectFile(guid);

        File.WriteAllText(projectFile, "{ first corruption");
        store.Adopt(_projectRoot);
        Assert.Equal("{ first corruption", File.ReadAllText(projectFile + ".corrupt"));

        File.WriteAllText(projectFile, "{ second corruption");
        store.Adopt(_projectRoot);

        // The first quarantine file must still hold the first corruption, untouched.
        Assert.Equal("{ first corruption", File.ReadAllText(projectFile + ".corrupt"));
        Assert.Equal("{ second corruption", File.ReadAllText(projectFile + ".corrupt.1"));
    }

    [Fact]
    public void All_SkipsUnreadableProjectFileInsteadOfThrowing()
    {
        // One bad file must not break the entire listing. Get() previously caught only
        // JsonException, so an unreadable (not merely malformed) file propagated straight
        // out of All().
        if (OperatingSystem.IsWindows())
        {
            return; // File.SetUnixFileMode is a Unix chmod equivalent; Windows ACLs differ.
        }

        if (Environment.IsPrivilegedProcess)
        {
            return; // Root bypasses permission bits entirely; the chmod below would not bite.
        }

        const string guid = "aaaabbbbccccddddeeeeffff00001111";
        MakeUnityProject(guid);
        var store = NewStore();
        store.Adopt(_projectRoot);

        var projectFile = new AppPaths(_appRoot).ProjectFile(guid);
        File.SetUnixFileMode(projectFile, UnixFileMode.None);

        try
        {
            var exception = Record.Exception(() => store.All());

            Assert.Null(exception);
            Assert.Empty(store.All());
        }
        finally
        {
            // Restore permissions so Dispose() can delete the directory.
            File.SetUnixFileMode(projectFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
