namespace Hades.Supervision.Tests;

/// <summary>
/// Locates FakeCore's build output as a sibling of this test project's own output, the same way
/// JobObjectTests.cs's own private <c>FindFakeCoreDll</c> does (kept as a separate copy there
/// rather than refactored to share this - see PlatformTraits.cs's own doc comment for why a small,
/// test-only helper duplicated once is preferable to a shared package; this file exists because
/// CoreSupervisorTests.cs and its own support classes need the exact same lookup a second time
/// within the SAME project, where duplicating again would just be copy-paste with no offsetting
/// benefit).
/// </summary>
internal static class FakeCoreLocator
{
    public static string FindDll()
    {
        var testOutputDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfmDir = new DirectoryInfo(testOutputDir);
        var configuration = tfmDir.Parent?.Name
            ?? throw new DirectoryNotFoundException($"Unexpected test output layout: {testOutputDir}");
        // .../Hades.Supervision.Tests/bin/<Configuration>/<TFM>/ -> .../Windows/
        var windowsRoot = tfmDir.Parent?.Parent?.Parent?.Parent
            ?? throw new DirectoryNotFoundException($"Unexpected test output layout: {testOutputDir}");

        var candidate = Path.Combine(windowsRoot.FullName, "FakeCore", "bin", configuration, tfmDir.Name, "FakeCore.dll");
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException(
                $"FakeCore.dll not found at '{candidate}' - build Windows/FakeCore before running these tests.", candidate);
        }

        return candidate;
    }
}
