using System.Security.AccessControl;
using System.Security.Principal;

namespace Hades.Core.Storage;

/// <summary>
/// Writes a discovery/token file so that only its owner can read it, from the moment it exists.
///
/// One implementation, two callers (<c>Server.Control.ControlAuth</c> and
/// <see cref="Editors.EditorListener"/>): both write a bearer token that authorises project
/// mutations, both had their own copy of this logic, and a Windows branch added twice would drift.
///
/// On Unix, the file is created at mode 0600 in the SAME syscall that creates the inode (see
/// <see cref="FileStreamOptions.UnixCreateMode"/>) - so the token is never briefly sitting in a
/// file at the wider, umask-determined default mode a plain <see cref="File.WriteAllText(string,string)"/>
/// -then-chmod would leave it at for the instant between the two. <see cref="FileStreamOptions.UnixCreateMode"/>
/// only takes effect when the call actually creates a NEW inode, so <see cref="File.SetUnixFileMode"/>
/// still runs unconditionally afterward as a defensive fallback for the one case it cannot cover: a
/// pre-existing file at this path (a stale discovery file from a previous run, or one some other
/// tool wrote) that <see cref="FileMode.Create"/> reuses/truncates instead of replacing, keeping
/// whatever mode it already had unless something narrows it explicitly.
/// </summary>
public static class TokenFileWriter
{
    public static void Write(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Hades targets macOS; File.SetUnixFileMode/FileStreamOptions.UnixCreateMode are
        // unsupported on Windows. OperatingSystem.IsWindows() is the analyzer-recognised
        // platform-guard pattern.
        if (OperatingSystem.IsWindows())
        {
            WriteWindows(path, contents);
            return;
        }

        WriteUnix(path, contents);
    }

    /// <summary>
    /// The Windows equivalent of <see cref="WriteUnix"/>'s atomic 0600: create the file with its
    /// final DACL already applied, in one call, rather than creating it under the directory's
    /// inherited ACL and narrowing it afterward — which would reintroduce precisely the window
    /// UnixCreateMode exists to close.
    ///
    /// SetAccessRuleProtection(true, false) is the load-bearing call, not boilerplate: without it
    /// the parent directory's inherited ACEs come along and the explicit rule below adds nothing.
    ///
    /// Stated honestly: as root can on Unix, Administrators can always read this file, and under
    /// default Windows ACLs other standard users already cannot reach files inside the profile.
    /// This hardens non-default setups; it is not what creates the boundary.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void WriteWindows(string path, string contents)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.Read | FileSystemRights.Write,
            AccessControlType.Allow));

        using var stream = new FileInfo(path).Create(
            FileMode.Create, FileSystemRights.Write, FileShare.None,
            bufferSize: 4096, FileOptions.None, security);

        var bytes = System.Text.Encoding.UTF8.GetBytes(contents);
        stream.Write(bytes, 0, bytes.Length);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    static void WriteUnix(string path, string contents)
    {
        using (var stream = File.Open(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(contents);
            stream.Write(bytes, 0, bytes.Length);
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
