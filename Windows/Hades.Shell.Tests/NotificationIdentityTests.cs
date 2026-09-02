using System.Text.RegularExpressions;
using Hades.Shell.ShellFacts;

namespace Hades.Shell.Tests;

/// <summary>
/// Guards the one string that joins the two halves of the app's notification identity.
///
/// The shell declares an AppUserModelID at startup; the MSI puts the SAME id on the Start menu
/// shortcut, which is where Windows reads a non-packaged app's notification name and icon from.
/// Nothing but string equality connects them, and when they disagree nothing errors - Windows
/// silently falls back to the anonymous form, which is a synthesised
/// <c>NotifyIconGeneratedAumid_&lt;hash&gt;</c> with no name but the executable's and no icon at all.
/// That failure is invisible from a developer build (there is no installed shortcut to resolve
/// against either way) and only shows up in Settings > Notifications on an installed copy, which is
/// exactly how it went unnoticed in the first place.
///
/// Same shape and same reasoning as <see cref="LeaseToastTests.ThresholdMatchesTheUnityPlugin"/>:
/// a value duplicated across a boundary no compiler crosses, pinned by reading the other side.
/// </summary>
public class NotificationIdentityTests
{
    [Fact]
    public void AppUserModelIdMatchesTheShortcutPropertyInTheInstaller()
    {
        var path = Path.Combine(TestRepo.Root(), "Windows", "Installer", "Hades.wxs");
        Assert.True(File.Exists(path), $"Hades.wxs not found at {path}");

        var match = Regex.Match(
            File.ReadAllText(path),
            """<ShortcutProperty\s+Key="System\.AppUserModel\.ID"\s+Value="(?<id>[^"]+)"\s*/>""");

        Assert.True(match.Success,
            "could not find the System.AppUserModel.ID shortcut property in Hades.wxs - if it was "
            + "removed, the toast and the Settings entry lose their name and icon silently; if it "
            + "was reshaped, this guard needs updating rather than deleting");

        // Constant first: xUnit2000 requires the compile-time value as 'expected'. The assertion is
        // symmetric anyway - the point is that the two sides are equal, not which one is canonical.
        Assert.Equal(NotificationIdentity.AppUserModelId, match.Groups["id"].Value);
    }

    /// <summary>
    /// An empty or whitespace id is accepted by SetCurrentProcessExplicitAppUserModelID's callers
    /// here but is not a usable identity - it would leave the app anonymous while looking configured.
    /// </summary>
    [Fact]
    public void AppUserModelIdIsNotBlank()
    {
        Assert.False(string.IsNullOrWhiteSpace(NotificationIdentity.AppUserModelId));
    }
}
