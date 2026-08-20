using System.Reflection;
using Hades.Core.Projects;
using Hades.Core.Storage;

namespace Hades.Core.Tests.Projects;

public class ProjectStoreTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = RealPath(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

    ProjectStore NewStore() => new(new AppPaths(_appRoot));

    /// <summary>
    /// Test-only realpath oracle - invokes the actual (private) <see cref="ProjectStore.Canonicalize"/>
    /// via reflection rather than re-implementing it, so this helper can never drift from what the
    /// method under test actually does. Needed because <see cref="Path.GetTempPath"/> itself sits
    /// under a symlinked ancestor on macOS (<c>/var</c> -&gt; <c>/private/var</c>): now that
    /// Canonicalize resolves the FULL chain (see that method's own doc comment), every fixture root
    /// built from GetTempPath() must be pre-resolved here, ONCE, at construction - otherwise
    /// Assert.Equal(_projectRoot, ...) below would fail for a reason that has nothing to do with the
    /// behavior each test actually names and exercises.
    /// </summary>
    static string RealPath(string path)
    {
        var method = typeof(ProjectStore).GetMethod("Canonicalize", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ProjectStore.Canonicalize not found — has it been renamed?");

        return (string)method.Invoke(null, [path])!;
    }

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

        var movedRoot = RealPath(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
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
    public void Adopt_CanonicalizesATrailingSlash_SoTheStoredNameAndPathAreStable()
    {
        MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var store = NewStore();

        var withTrailingSlash = _projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var project = store.Adopt(withTrailingSlash);

        Assert.NotNull(project);
        Assert.Equal(_projectRoot, project!.Path);
        Assert.Equal(Path.GetFileName(_projectRoot), project.Name);
    }

    [Fact]
    public void Adopt_ThroughASymlinkAlias_ResolvesPathToTheRealDirectory_ButNameTracksTheCallersOwnLeaf()
    {
        MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var store = NewStore();

        var link = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateSymbolicLink(link, _projectRoot);
        try
        {
            var viaLink = store.Adopt(link);
            Assert.NotNull(viaLink);
            // Path always resolves to the REAL directory regardless of which spelling was
            // adopted - see Canonicalize's own doc comment: only the STORED PATH is canonical.
            Assert.Equal(_projectRoot, viaLink!.Path);
            // Name, by contrast, is deliberately NOT canonicalized: it is the leaf of whatever
            // path the caller actually supplied ("link"'s own basename here), not the real
            // directory's name - otherwise a project opened through a symlinked alias would show
            // up renamed to something the user never typed or saw in Finder/Explorer.
            Assert.Equal(Path.GetFileName(link), viaLink.Name);

            // Re-adopting via the real path afterward keeps the SAME identity - same ProductGuid,
            // same canonical Path - it is only Name that tracks each call's own spelling, which
            // here happens to equal _projectRoot's basename because that's what was passed in.
            var viaReal = store.Adopt(_projectRoot);
            Assert.Equal(_projectRoot, viaReal!.Path);
            Assert.Equal(Path.GetFileName(_projectRoot), viaReal.Name);
            Assert.Single(store.All());
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void Adopt_ThroughAnIntermediateSymlinkedDirectory_ResolvesTheFullChain_SoBothSpellingsConvergeOnOneRow()
    {
        // The bug this guards: Canonicalize used to resolve only the LEAF component's own
        // symlink, so a symlink one or more levels ABOVE the leaf - macOS's own /tmp ->
        // /private/tmp is exactly this shape, since /tmp is never the leaf of a project path
        // like /tmp/MyProj - passed straight through unresolved. This test builds that same
        // shape from scratch (scratch/link -> scratch/real, project rooted at scratch/link/Proj)
        // rather than depending on any particular OS's own ambient symlinks.
        const string guid = "aaaabbbbccccddddeeeeffff00002222";

        var scratch = RealPath(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        var real = Path.Combine(scratch, "real");
        var link = Path.Combine(scratch, "link");
        var projectViaReal = Path.Combine(real, "Proj");
        var projectViaLink = Path.Combine(link, "Proj");

        Directory.CreateDirectory(Path.Combine(projectViaReal, "ProjectSettings"));
        File.WriteAllText(Path.Combine(projectViaReal, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {guid}\n");
        Directory.CreateSymbolicLink(link, real);

        try
        {
            var store = NewStore();

            var viaLink = store.Adopt(projectViaLink);
            Assert.NotNull(viaLink);
            // The INTERMEDIATE "link" component is resolved away, not just a leaf-level link.
            Assert.Equal(projectViaReal, viaLink!.Path);

            var viaReal = store.Adopt(projectViaReal);
            Assert.NotNull(viaReal);
            Assert.Equal(projectViaReal, viaReal!.Path);

            // Both spellings must converge on the SAME row, not one each - the exact duplicate
            // registration Canonicalize exists to prevent.
            Assert.Single(store.All());
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(scratch, recursive: true);
        }
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
