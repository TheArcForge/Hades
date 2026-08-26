namespace Hades.Core.Tests;

/// <summary>
/// 53 tests across 14 files run the real indexer against real Unity projects on the developer's
/// own machine (Hades-Unity-Client, project_aurora). They have caught real defects that synthetic
/// fixtures did not - see RealCorpusReaderTests' own note that "Plan 1's two worst defects were
/// both found this way". They cannot run in CI: those corpora are not on a runner, and
/// project_aurora is a private production project that should not be.
///
/// Each guards itself with `if (!Directory.Exists(...)) return;`, which xUnit reports as PASSED -
/// so on a machine without the corpora, the pass count silently overstates what was exercised.
/// xUnit 2.9.3 offers no dynamic skip to express this properly (see PlatformTraits), and neither
/// Xunit.SkippableFact nor an xUnit v3 migration is being adopted for it.
///
/// This test is the honest alternative: a machine that is SUPPOSED to have the corpora says so by
/// setting HADES_REQUIRE_CORPUS=1, and finds out here - once, loudly - if they are missing. CI
/// never sets it, so CI simply does not pretend to cover them.
/// </summary>
public class CorpusAvailabilityTests
{
    static readonly string[] Corpora =
    [
        "/Users/mike/Projects/Hades-Unity-Client",
        "/Users/mike/Projects/project_aurora",
    ];

    [Fact]
    public void RequiredCorporaArePresentWhenTheMachineClaimsThem()
    {
        if (Environment.GetEnvironmentVariable("HADES_REQUIRE_CORPUS") != "1") return;

        var missing = Corpora.Where(c => !Directory.Exists(c)).ToList();

        Assert.True(missing.Count == 0,
            "HADES_REQUIRE_CORPUS=1 but these corpora are absent, so the ~53 real-project tests " +
            "silently no-opped and this run's pass count overstates its coverage: " +
            string.Join(", ", missing));
    }
}
