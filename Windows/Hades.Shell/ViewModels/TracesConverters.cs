using System.Globalization;
using System.Windows.Data;
using Hades.Control.Client.Dtos;

namespace Hades.Shell.ViewModels;

/// <summary>A <see cref="TraceOutcome"/> as its Segoe glyph. A failing call has to be visibly
/// distinguishable from a successful one at a glance - that is the whole point of the column.</summary>
public sealed class OutcomeGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TraceOutcome outcome ? StatusGlyph.For(outcome) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A UTC-milliseconds timestamp as local time.
///
/// The core reports instants as epoch milliseconds, not as display strings, so unlike a status
/// sentence there is no server-authored text to render verbatim here - formatting an instant for the
/// viewer's own locale is the shell's job, and doing it in one converter keeps it from being done
/// five different ways across the view.
/// </summary>
public sealed class UtcMillisecondsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss", culture)
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Joins a sequence's tool list for display. The names are the core's; only the separator
/// is ours.</summary>
public sealed class ToolListConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> tools ? string.Join(" → ", tools) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the trace fetch state matches the <c>ConverterParameter</c> - lets the
/// detail pane show exactly one of "nothing selected", the spans, or the server's error.</summary>
public sealed class DetailKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TraceDetailFetchState state
        && parameter is string expected
        && string.Equals(state.Kind.ToString(), expected, StringComparison.OrdinalIgnoreCase)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>The same, for a memory document's fetch state.</summary>
public sealed class MemoryDocumentKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MemoryDocumentFetchState state
        && parameter is string expected
        && string.Equals(state.Kind.ToString(), expected, StringComparison.OrdinalIgnoreCase)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A duration in milliseconds, written the way a person reads one.
///
/// <para>Everything in this view used to print raw <c>{n}ms</c>, which is exact and nearly unreadable
/// past a second or two: a sequence showed "18181ms" where "18.2 s" is the same fact and instantly
/// comparable against its neighbours. The thresholds are chosen so the number keeps three
/// significant figures at most and never grows a long tail of digits nobody reads.</para>
///
/// <para>Sub-millisecond work reports as <c>0 ms</c> rather than being rounded up to 1: a call that
/// took no measurable time should not be made to look like it took some.</para>
/// </summary>
public sealed class DurationConverter : IValueConverter
{
    public static string Format(long milliseconds)
    {
        if (milliseconds < 0) return "-";
        if (milliseconds < 1_000) return $"{milliseconds} ms";
        if (milliseconds < 10_000) return $"{milliseconds / 1000.0:0.0} s";
        if (milliseconds < 60_000) return $"{milliseconds / 1000.0:0} s";

        var total = TimeSpan.FromMilliseconds(milliseconds);
        return total.TotalHours >= 1
            ? $"{(int)total.TotalHours}h {total.Minutes:00}m"
            : $"{(int)total.TotalMinutes}m {total.Seconds:00}s";
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            long ms => Format(ms),
            int ms => Format(ms),
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// How far into its sequence a call started, as a signed offset - <c>+0 ms</c>, <c>+1.2 s</c>.
///
/// <para>The leading <c>+</c> is not decoration: without it a column of offsets is indistinguishable
/// from a column of durations, and the two sit next to each other. This is the one number in the
/// view that answers "when", where every other answers "how long".</para>
/// </summary>
public sealed class OffsetConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long ms ? $"+{DurationConverter.Format(ms)}" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the sequence-calls fetch state matches the <c>ConverterParameter</c> - the
/// same one-of-N pattern <see cref="DetailKindConverter"/> uses for the call pane below it.</summary>
public sealed class SequenceCallsKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SequenceCallsFetchState state
        && parameter is string expected
        && string.Equals(state.Kind.ToString(), expected, StringComparison.OrdinalIgnoreCase)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A call count with its noun already agreed - "1 call", "7 calls".
///
/// <para>The count and the word used to be separate Runs in the template, so every single-call
/// sequence read "1 calls". Harmless-looking on screen and worse aloud, since the accessible name
/// built from the same value says it too. Pluralising in one place means the visible row and the
/// spoken name cannot disagree.</para>
/// </summary>
public sealed class CallCountConverter : IValueConverter
{
    public static string Format(int count) => count == 1 ? "1 call" : $"{count} calls";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count ? Format(count) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
