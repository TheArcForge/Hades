using Hades.Core.Projects;

namespace Hades.Core.Tests.Projects;

public class ProjectIdentityTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    void WriteProjectSettings(string contents)
    {
        var dir = Path.Combine(_root, "ProjectSettings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ProjectSettings.asset"), contents);
    }

    const string RealHeader = """
        %YAML 1.1
        %TAG !u! tag:unity3d.com,2011:
        --- !u!129 &1
        PlayerSettings:
          m_ObjectHideFlags: 0
          serializedVersion: 26
          productGUID: 15c012f27331e49229cef25e74537816
          AndroidProfiler: 0
          companyName: DefaultCompany
          productName: Hades-Unity-Client
        """;

    [Fact]
    public void ReadsProductGuidFromRealProjectSettings()
    {
        WriteProjectSettings(RealHeader);

        Assert.Equal("15c012f27331e49229cef25e74537816", ProjectIdentity.TryReadProductGuid(_root));
    }

    [Fact]
    public void NormalisesGuidToLowercase()
    {
        WriteProjectSettings("  productGUID: 15C012F27331E49229CEF25E74537816\n");

        Assert.Equal("15c012f27331e49229cef25e74537816", ProjectIdentity.TryReadProductGuid(_root));
    }

    [Fact]
    public void ReturnsNullWhenProjectSettingsMissing()
    {
        Directory.CreateDirectory(_root);

        Assert.Null(ProjectIdentity.TryReadProductGuid(_root));
    }

    [Fact]
    public void ReturnsNullWhenFileIsBinary()
    {
        // Force Binary / Mixed serialization: no readable productGUID. The caller
        // must report this rather than silently producing an incomplete graph.
        var dir = Path.Combine(_root, "ProjectSettings");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ProjectSettings.asset"),
            [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x00, 0x42]);

        Assert.Null(ProjectIdentity.TryReadProductGuid(_root));
    }

    [Fact]
    public void ReturnsNullWhenFileIsUnreadable()
    {
        // UnauthorizedAccessException derives from SystemException, not IOException, so a
        // catch (IOException) misses it. Realistic trigger: a Hades server without macOS
        // Full Disk Access reading a project under ~/Documents or ~/Desktop. The caller must
        // get the documented "returns null", not an unhandled exception.
        if (OperatingSystem.IsWindows())
        {
            return; // File.SetUnixFileMode is a Unix chmod equivalent; Windows ACLs differ.
        }

        if (Environment.IsPrivilegedProcess)
        {
            return; // Root bypasses permission bits entirely; the chmod below would not bite.
        }

        WriteProjectSettings(RealHeader);
        var path = ProjectIdentity.SettingsPath(_root);
        File.SetUnixFileMode(path, UnixFileMode.None);

        try
        {
            Assert.Null(ProjectIdentity.TryReadProductGuid(_root));
        }
        finally
        {
            // Restore permissions so Dispose() can delete the directory.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void IsUnityProject_TrueOnlyWhenProjectSettingsExists()
    {
        Directory.CreateDirectory(_root);
        Assert.False(ProjectIdentity.IsUnityProject(_root));

        WriteProjectSettings(RealHeader);
        Assert.True(ProjectIdentity.IsUnityProject(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
