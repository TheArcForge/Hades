using System.Text.RegularExpressions;
using Hades.Control.Client.Dtos;
using Hades.Shell.Tray;

namespace Hades.Shell.Tests;

public class LeaseToastTests
{
    static SummaryLease Lease(string id, int heldForSeconds, string project = "Hades-Unity-Client") => new()
    {
        Project = project,
        LeaseId = id,
        HeldForSeconds = heldForSeconds,
        ExpiresInSeconds = 18,
        Releasable = true,
    };

    [Fact]
    public void NoLeaseHeld_SaysNothing()
    {
        Assert.Null(new LeaseToast().Evaluate(null));
    }

    /// <summary>
    /// An ordinary script-editing round trip takes a lease and gives it back in well under the
    /// threshold. Toasting those would train the user to ignore the one that matters.
    /// </summary>
    [Fact]
    public void ALeaseHeldBrieflySaysNothing()
    {
        Assert.Null(new LeaseToast().Evaluate(Lease("a", heldForSeconds: 3)));
    }

    [Fact]
    public void ALeaseHeldPastTheThreshold_Notifies_NamingTheProject()
    {
        var message = new LeaseToast().Evaluate(Lease("a", heldForSeconds: 11, project: "MyGame"));

        Assert.NotNull(message);
        Assert.Contains("MyGame", message);
    }

    [Fact]
    public void TheThresholdItselfIsInclusive()
    {
        Assert.NotNull(new LeaseToast().Evaluate(Lease("a", heldForSeconds: 10)));
    }

    /// <summary>
    /// Once per continuous hold, not once per poll. At the shell's poll rate a lease held for a
    /// minute would otherwise produce a toast every second - which is not prominence, it is an
    /// unusable machine.
    /// </summary>
    [Fact]
    public void ALeaseHeldWellPastTheThreshold_NotifiesOnlyOnce()
    {
        var toast = new LeaseToast();

        Assert.NotNull(toast.Evaluate(Lease("a", heldForSeconds: 11)));

        for (var held = 12; held < 120; held++)
        {
            Assert.Null(toast.Evaluate(Lease("a", heldForSeconds: held)));
        }
    }

    /// <summary>
    /// A NEW hold notifies again. Otherwise the shell goes quiet forever after the first stuck
    /// lease of the session, and the second one - the one the user has not learned about yet -
    /// arrives in silence.
    /// </summary>
    [Fact]
    public void AFreshLeaseAfterAReleaseNotifiesAgain()
    {
        var toast = new LeaseToast();
        Assert.NotNull(toast.Evaluate(Lease("a", heldForSeconds: 11)));

        Assert.Null(toast.Evaluate(null)); // released
        Assert.NotNull(toast.Evaluate(Lease("b", heldForSeconds: 11)));
    }

    /// <summary>
    /// The same id re-acquired after a release is a new hold too. ReloadGate resets its
    /// _warnedThisHold flag per hold rather than remembering ids forever, and this matches it.
    /// </summary>
    [Fact]
    public void TheSameLeaseIdReacquiredAfterAReleaseNotifiesAgain()
    {
        var toast = new LeaseToast();
        Assert.NotNull(toast.Evaluate(Lease("a", heldForSeconds: 11)));
        Assert.Null(toast.Evaluate(null));

        Assert.NotNull(toast.Evaluate(Lease("a", heldForSeconds: 11)));
    }

    /// <summary>
    /// THE DRIFT GUARD. LeaseToast.HeldWarningThreshold duplicates
    /// ReloadGate.HeldWarningThreshold because the shell cannot reference a Unity project - see that
    /// constant's own doc comment. Duplication is forced; disagreement is not. Two surfaces telling
    /// a user different things about when a reload lock has been held "too long" is precisely the
    /// confusion this feature exists to prevent.
    /// </summary>
    [Fact]
    public void ThresholdMatchesTheUnityPlugin()
    {
        var path = Path.Combine(
            TestRepo.Root(), "UnityPlugin", "Assets", "Hades", "Runtime", "ReloadGate.cs");
        Assert.True(File.Exists(path), $"ReloadGate.cs not found at {path}");

        var match = Regex.Match(
            File.ReadAllText(path),
            @"HeldWarningThreshold\s*=\s*TimeSpan\.FromSeconds\(\s*(?<seconds>\d+(?:\.\d+)?)\s*\)");

        Assert.True(match.Success,
            "could not find ReloadGate.HeldWarningThreshold - if it was renamed or reshaped, this "
            + "guard needs updating rather than deleting: the two values still have to agree");

        var pluginSeconds = double.Parse(match.Groups["seconds"].Value);

        Assert.Equal(pluginSeconds, LeaseToast.HeldWarningThreshold.TotalSeconds);
    }
}
