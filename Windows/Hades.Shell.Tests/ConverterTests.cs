using System.Globalization;
using System.Windows;
using Hades.Shell.ViewModels;

namespace Hades.Shell.Tests;

/// <summary>
/// Converters are view code, but they DECIDE things - what is shown and what is hidden - so the
/// decisions belong in tests. The truncation case below was found by looking at the running window,
/// not by any test, which is exactly why it is pinned now.
/// </summary>
public class EmptyToCollapsedConverterTests
{
    static Visibility Convert(object? value) =>
        (Visibility)new EmptyToCollapsedConverter().Convert(value, typeof(Visibility), null, CultureInfo.InvariantCulture);

    [Fact]
    public void NullIsCollapsed() => Assert.Equal(Visibility.Collapsed, Convert(null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTextIsCollapsed(string value) => Assert.Equal(Visibility.Collapsed, Convert(value));

    [Fact]
    public void TextIsVisible() => Assert.Equal(Visibility.Visible, Convert("something the core said"));

    [Fact]
    public void AnEmptyCollectionIsCollapsed() => Assert.Equal(Visibility.Collapsed, Convert(Array.Empty<string>()));

    [Fact]
    public void ANonEmptyCollectionIsVisible() => Assert.Equal(Visibility.Visible, Convert(new[] { "a" }));

    /// <summary>
    /// Bound to SequencesTruncated. Falling through to Visible for a bool put "Showing a truncated
    /// list - narrow the filters to see the rest" above a list that was not truncated, which tells
    /// the user data is being withheld when none is.
    /// </summary>
    [Fact]
    public void FalseIsCollapsed() => Assert.Equal(Visibility.Collapsed, Convert(false));

    [Fact]
    public void TrueIsVisible() => Assert.Equal(Visibility.Visible, Convert(true));
}
