using System.Runtime.InteropServices;

namespace Hades.Shell.ShellFacts;

/// <summary>
/// Declares this process's AppUserModelID - the identity Windows files notifications under.
///
/// <para><b>Why this exists.</b> Without it, Settings > Notifications listed this app as
/// "Hades.Shell" with a blank blue placeholder where its icon should be, and the toasts themselves
/// were captioned the same way. Neither is a cosmetic accident: a non-packaged app that calls
/// <c>NotifyIcon.ShowBalloonTip</c> and has declared no AUMID gets one SYNTHESISED by the shell -
/// visible in the registry as <c>NotifyIconGeneratedAumid_&lt;hash&gt;</c> under
/// <c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings</c>, alongside the
/// well-behaved apps' real ids (<c>Chrome</c>, <c>com.squirrel.Discord.Discord</c>). A synthesised
/// id has nothing registered against it, so Windows has no name to show but the executable's and no
/// icon at all.</para>
///
/// <para><b>This is only half the fix, and the half that cannot work alone.</b> Declaring the id
/// gives Windows a stable key; it does not tell it what to draw. For a non-packaged desktop app the
/// display name and icon are resolved from a START MENU SHORTCUT carrying the same id in its
/// <c>System.AppUserModel.ID</c> property - which is why <c>Hades.wxs</c> sets that property on the
/// shortcut it installs, and why the two values must stay identical. Change one and the toast goes
/// back to being anonymous.</para>
///
/// <para><b>Consequence worth knowing:</b> a shell run from a build output, with no installed Start
/// menu shortcut, still has no shortcut to resolve against - so a developer build shows the id
/// itself rather than "Hades". That is the mechanism working, not a regression, and it is why this
/// was only ever visible from an installed copy.</para>
/// </summary>
internal static partial class NotificationIdentity
{
    /// <summary>
    /// MUST match the <c>System.AppUserModel.ID</c> shortcut property in
    /// <c>Windows/Installer/Hades.wxs</c>, exactly. The two halves are joined by nothing but this
    /// string being equal in both places.
    ///
    /// <para>Shaped Company.Product, matching what the rest of the install already uses for the
    /// same pairing - the MSI's Manufacturer/Name, and the <c>Software\ArcForge\Hades</c> key its
    /// per-user components key off.</para>
    /// </summary>
    internal const string AppUserModelId = "ArcForge.Hades";

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>
    /// Must be called BEFORE the first <c>NotifyIcon</c> exists: the shell reads the process's id
    /// when the icon is created, and synthesises one if there is nothing to read. Declaring it
    /// afterwards leaves the generated id already in force for this session.
    ///
    /// <para>Deliberately not fatal, and deliberately not reported. A failure here costs an icon and
    /// a caption in a Settings list - the app, the tray and the toast all still work - so it must
    /// never be the reason a shell fails to start.</para>
    /// </summary>
    internal static void Declare()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (DllNotFoundException)
        {
            // shell32 absent: not a Windows this app can run on anyway, and not worth crashing over.
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-Windows 7. The floor is 14393, so this is unreachable in practice - caught rather
            // than assumed away, because an unhandled exception here would kill startup.
        }
    }
}
