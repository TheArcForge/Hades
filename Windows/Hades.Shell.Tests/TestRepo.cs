namespace Hades.Shell.Tests;

/// <summary>
/// Locates the repository root from the test binary. Several tests here read files that are not
/// build outputs - the generated control-API fixtures, and the Unity plugin source the lease
/// threshold has to agree with - and all of them need the same walk.
/// </summary>
static class TestRepo
{
    /// <summary>
    /// Walks up looking for the two directories that only ever sit together at the repository root.
    /// Same approach as Hades.Control.Client.Tests' own FixtureGenerationTests.RepositoryRoot; this
    /// project cannot reference that one, and the walk has to work from Windows/ as well as Core/.
    /// </summary>
    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !(Directory.Exists(Path.Combine(directory.FullName, "UnityPlugin"))
                    && Directory.Exists(Path.Combine(directory.FullName, "Core"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException(
                   $"Could not locate the repository root from {AppContext.BaseDirectory}.");
    }
}
