using System.Globalization;
using Hades.Shell.ViewModels;

namespace Hades.Shell.Tests;

/// <summary>
/// Guards how durations and offsets READ.
///
/// The Charon view printed raw <c>{n}ms</c> everywhere, which is exact and nearly unusable past a
/// second or two - a sequence showed "18181ms" where "18 s" is the same fact and instantly
/// comparable against the rows around it. These pin the thresholds, because the whole value of the
/// formatting is that the same magnitude always looks the same.
/// </summary>
public class DurationFormattingTests
{
    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(1, "1 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1_000, "1.0 s")]
    [InlineData(1_240, "1.2 s")]
    [InlineData(9_999, "10.0 s")]
    [InlineData(18_181, "18 s")]
    [InlineData(59_999, "60 s")]
    [InlineData(60_000, "1m 00s")]
    [InlineData(189_945, "3m 09s")]
    [InlineData(3_600_000, "1h 00m")]
    public void FormatsByMagnitude(long milliseconds, string expected) =>
        Assert.Equal(expected, DurationConverter.Format(milliseconds));

    /// <summary>Sub-millisecond work reports as 0 ms rather than being rounded up - a call that took
    /// no measurable time must not be made to look like it took some.</summary>
    [Fact]
    public void ZeroIsZero_NotRoundedUp() => Assert.Equal("0 ms", DurationConverter.Format(0));

    [Fact]
    public void NegativeIsNotRendered_AsAnImpossibleDuration() =>
        Assert.Equal("-", DurationConverter.Format(-1));

    [Fact]
    public void TheConverterAgreesWithTheFormatter()
    {
        var converter = new DurationConverter();

        Assert.Equal(
            DurationConverter.Format(1_240),
            converter.Convert(1_240L, typeof(string), null, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The leading "+" is load-bearing, not decoration: offsets sit in a column immediately beside
    /// durations, and without it the two are indistinguishable at a glance.
    /// </summary>
    [Theory]
    [InlineData(0L, "+0 ms")]
    [InlineData(250L, "+250 ms")]
    [InlineData(1_500L, "+1.5 s")]
    public void OffsetsAreSigned(long milliseconds, string expected) =>
        Assert.Equal(expected, new OffsetConverter().Convert(
            milliseconds, typeof(string), null, CultureInfo.InvariantCulture));

    /// <summary>
    /// "1 calls" read badly on screen and worse aloud - the accessible name is built from the same
    /// value, so an ungrammatical count was being spoken as well as shown.
    /// </summary>
    [Theory]
    [InlineData(0, "0 calls")]
    [InlineData(1, "1 call")]
    [InlineData(2, "2 calls")]
    [InlineData(19, "19 calls")]
    public void CallCountsAgreeWithTheirNoun(int count, string expected) =>
        Assert.Equal(expected, CallCountConverter.Format(count));

    /// <summary>
    /// The same defect, found later and elsewhere: the onboarding Projects step rendered a bare
    /// count followed by the literal word "nodes", so a freshly added single-script project
    /// announced and displayed "1 nodes". Measured during the Task 12 Step 8 walk on the shipped
    /// MSI, not reasoned about - which is why the boundary case is the one pinned here.
    /// </summary>
    [Theory]
    [InlineData(0, "0 nodes")]
    [InlineData(1, "1 node")]
    [InlineData(2, "2 nodes")]
    [InlineData(28838, "28838 nodes")]
    public void NodeCountsAgreeWithTheirNoun(int count, string expected) =>
        Assert.Equal(expected, NodeCountConverter.Format(count));
}
