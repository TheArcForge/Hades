using System.Security.AccessControl;
using System.Security.Principal;
using Hades.Core.Storage;

namespace Hades.Core.Tests.Storage;

public class TokenFileWriterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void WritesTheContentAndCreatesMissingDirectories()
    {
        var path = Path.Combine(_dir, "nested", "control.token");

        TokenFileWriter.Write(path, """{"port":1,"token":"t"}""");

        Assert.Equal("""{"port":1,"token":"t"}""", File.ReadAllText(path));
    }

    // CA1416: File.GetUnixFileMode/File.SetUnixFileMode are POSIX-only. Hades only ever runs on
    // macOS/Unix (see AppPaths and every other Unix-assuming path in this codebase), and these
    // tests are gated by [Trait(PlatformTraits.Key, PlatformTraits.Unix)] to run only there, so
    // the platform-compatibility warning has nothing to protect here - suppressed for exactly the
    // two call sites that need it, same pattern as IncrementalIndexTests.
#pragma warning disable CA1416
    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Unix)]
    public void RestrictsToTheOwnerOnUnix()
    {
        var path = Path.Combine(_dir, "control.token");

        TokenFileWriter.Write(path, "x");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Unix)]
    public void NarrowsAPreExistingWiderFile()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "control.token");
        File.WriteAllText(path, "stale");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        TokenFileWriter.Write(path, "fresh");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal("fresh", File.ReadAllText(path));
    }
#pragma warning restore CA1416

    // CA1416: FileSystemSecurity/FileSystemAccessRule/WindowsIdentity are Windows-only. This test
    // is gated by [Trait(PlatformTraits.Key, PlatformTraits.Windows)] to run only there, so the
    // platform-compatibility warning has nothing to protect here - suppressed for exactly this call
    // site, same pattern as IncrementalIndexTests.
#pragma warning disable CA1416
    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Windows)]
    public void RestrictsToTheOwnerOnWindows()
    {
        var path = Path.Combine(_dir, "control.token");

        TokenFileWriter.Write(path, "x");

        var security = new FileInfo(path).GetAccessControl();

        // Protection severed inheritance: the parent's ACEs must NOT have come along, or the
        // explicit rule below adds nothing at all.
        Assert.True(security.AreAccessRulesProtected);

        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        var me = WindowsIdentity.GetCurrent().User!;
        Assert.All(rules, rule => Assert.Equal(me, rule.IdentityReference));
        Assert.Contains(rules, rule =>
            rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights.HasFlag(FileSystemRights.Read));
    }
#pragma warning restore CA1416
}
