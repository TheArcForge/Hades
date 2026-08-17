using Hades.Core.Editors;

namespace Hades.Core.Tests.Editors;

/// <summary>
/// Pure classification tests for <see cref="PluginVersionComparison.Classify"/> - see spec #4 §6
/// ("the plugin reports its version on connect... a plugin one minor version behind still serves
/// what it can, with a warning") and <see cref="PluginVersionSkew"/>'s own doc comment for what
/// each case means and why direction (older vs newer) never changes which bucket a pair falls
/// into, only major-vs-same-major does.
/// </summary>
public sealed class PluginVersionSkewTests
{
    [Fact]
    public void IdenticalVersions_AreSame()
    {
        Assert.Equal(PluginVersionSkew.Same, PluginVersionComparison.Classify("1.2.0", "1.2.0"));
    }

    [Fact]
    public void TwoPartVersion_TreatsMissingPatchAsZero_StillSame()
    {
        // "1.2" and "1.2.0" mean the same release - patch defaults to 0 either way.
        Assert.Equal(PluginVersionSkew.Same, PluginVersionComparison.Classify("1.2", "1.2.0"));
    }

    [Fact]
    public void OneMinorBehind_IsMinorSkew_TheSpecsOwnLiteralExample()
    {
        // Spec #4 §6, verbatim: "a plugin one minor version behind still serves what it can".
        Assert.Equal(PluginVersionSkew.Minor, PluginVersionComparison.Classify(pluginVersion: "1.1.0", appVersion: "1.2.0"));
    }

    [Fact]
    public void SeveralMinorVersionsBehind_StillOnlyMinorSkew_NotEscalatedByMagnitude()
    {
        // Matches the pre-existing ProjectsEndpoint fixture (1.0.0 vs 1.2.0) - two minors apart,
        // same bucket as one minor apart. There is no sliding scale within a major version: the
        // spec's line is major-vs-not-major, not "how many minors".
        Assert.Equal(PluginVersionSkew.Minor, PluginVersionComparison.Classify("1.0.0", "1.2.0"));
    }

    [Fact]
    public void PatchOnlyDifference_IsMinorSkew()
    {
        Assert.Equal(PluginVersionSkew.Minor, PluginVersionComparison.Classify("1.2.5", "1.2.0"));
    }

    [Fact]
    public void PluginNewerThanApp_SameMajor_IsOrdinaryMinorSkew_NotEscalated()
    {
        // Explicit decision (see the plan report): "newer than app" only escalates when the MAJOR
        // differs. A same-major newer plugin (e.g. a beta build) degrades exactly like an older
        // one - same bucket, same treatment.
        Assert.Equal(PluginVersionSkew.Minor, PluginVersionComparison.Classify(pluginVersion: "1.3.0", appVersion: "1.2.0"));
    }

    [Fact]
    public void DifferentMajor_PluginBehind_IsMajorSkew()
    {
        Assert.Equal(PluginVersionSkew.Major, PluginVersionComparison.Classify(pluginVersion: "1.2.0", appVersion: "2.0.0"));
    }

    [Fact]
    public void DifferentMajor_PluginAhead_IsMajorSkew_ThePluginFromTheFutureCase()
    {
        Assert.Equal(PluginVersionSkew.Major, PluginVersionComparison.Classify(pluginVersion: "2.0.0", appVersion: "1.2.0"));
    }

    [Fact]
    public void ZeroVersusNonZeroMajor_IsMajorSkew()
    {
        // The exact literal EditorListenerTests already uses as an arbitrary "some other version"
        // fixture (0.9.1) - confirms it lands in the escalated bucket against a 1.2.0 app, not the
        // ordinary one.
        Assert.Equal(PluginVersionSkew.Major, PluginVersionComparison.Classify("0.9.1", "1.2.0"));
    }

    [Theory]
    [InlineData(null, "1.2.0")]
    [InlineData("1.2.0", null)]
    [InlineData(null, null)]
    [InlineData("", "1.2.0")]
    [InlineData("not-a-version", "1.2.0")]
    [InlineData("1", "1.2.0")]
    [InlineData("1.x.0", "1.2.0")]
    public void UnparseableOrMissingVersion_IsUnknown_NothingTrustworthyToCompare(string? pluginVersion, string? appVersion)
    {
        Assert.Equal(PluginVersionSkew.Unknown, PluginVersionComparison.Classify(pluginVersion, appVersion));
    }

    // ---------------------------------------------------------------------------------------
    // A 4-part version or a semver prerelease/build suffix used to fall all the way to Unknown
    // (nothing trustworthy to compare), which suppressed the skew warning even across a major
    // version gap - e.g. a "2.0.0-beta" plugin talking to a 1.4.0 app warned about nothing.
    // TryParse now reads the numeric MAJOR.MINOR(.patch) core and ignores a trailing "-"/"+"
    // suffix or any component past the third, so classification still fires on that core.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void FourPartVersion_ParsesTheNumericCore_IgnoringTheExtraComponent()
    {
        // Core (1,2,3) vs (1,2,0): same major, not identical - ordinary Minor skew, not Unknown.
        Assert.Equal(PluginVersionSkew.Minor, PluginVersionComparison.Classify("1.2.3.4", "1.2.0"));
    }

    [Fact]
    public void PrereleaseSuffix_IsStripped_SoAMajorGapStillWarns_TheSpecsOwnLiteralExample()
    {
        // The finding's own motivating case: "a '2.0.0-beta' plugin vs a 1.4.0 app should warn".
        Assert.Equal(PluginVersionSkew.Major, PluginVersionComparison.Classify(pluginVersion: "2.0.0-beta", appVersion: "1.4.0"));
    }

    [Fact]
    public void PrereleaseSuffix_WithAnIdenticalNumericCore_IsSame()
    {
        Assert.Equal(PluginVersionSkew.Same, PluginVersionComparison.Classify("1.2.0-beta", "1.2.0"));
    }

    [Fact]
    public void BuildMetadataSuffix_IsAlsoStripped_NotJustPrerelease()
    {
        Assert.Equal(PluginVersionSkew.Same, PluginVersionComparison.Classify("1.2.0+build5", "1.2.0"));
    }
}
