using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using Hades.Control.Client.Dtos;

namespace Hades.Shell.ViewModels;

/// <summary>A <see cref="ControlSeverity"/> as its Segoe glyph - the same vocabulary the tray uses,
/// resolved in the same one place.</summary>
public sealed class SeverityGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ControlSeverity severity ? StatusGlyph.For(severity) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Collapses an element when its bound value is "nothing to show": null, blank text, an empty
/// collection, or false.
///
/// The false case is not a nicety. Bound to <c>SequencesTruncated</c>, a converter that fell through
/// to Visible for a bool showed "Showing a truncated list" over a list that was not truncated -
/// telling the user data was being hidden from them when none was.
/// </summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => Visibility.Collapsed,
        bool flag => flag ? Visibility.Visible : Visibility.Collapsed,
        string s => string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible,
        System.Collections.ICollection c => c.Count == 0 ? Visibility.Collapsed : Visibility.Visible,
        _ => Visibility.Visible,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A bool as "On"/"Off". Shell chrome for a shell-observed OS fact - there is no
/// server-authored string for it, because the core cannot see this machine's power state at all.</summary>
public sealed class OnOffConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "On" : "Off";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Looks a project's rebuild progress out of <see cref="ProjectsViewModel.RebuildProgress"/> and
/// renders it as text. A MultiBinding because the dictionary is keyed by productGuid and WPF can
/// only bind an indexer with a literal key.
///
/// Every string it produces comes from the core: a tracked operation shows the core's own
/// <c>Progress</c> text (or its result message once finished), and a pruned one shows the server's
/// own explanation. The only words added here are the elapsed-seconds unit, which the core reports
/// as a bare number.
/// </summary>
public sealed class RebuildProgressConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [string productGuid, IReadOnlyDictionary<string, OperationProgress> progress]) return string.Empty;
        if (!progress.TryGetValue(productGuid, out var entry)) return string.Empty;

        if (entry.Kind == OperationProgressKind.Pruned) return entry.Message ?? string.Empty;

        var result = entry.Result;
        if (result is null) return string.Empty;

        // Progress while running; once finished, the operation's own result message if it carried
        // one, else its error. Never re-derived from the timestamps: ElapsedSeconds is already whole
        // seconds the core computed.
        var detail = result.State == OperationState.Running
            ? result.Progress
            : result.Error ?? ResultMessage(result);

        return string.IsNullOrWhiteSpace(detail)
            ? $"{result.ElapsedSeconds}s"
            : $"{detail} Â· {result.ElapsedSeconds}s";
    }

    /// <summary>
    /// A finished rebuild's own resolved message. <c>OperationResult.Result</c> is typed
    /// <c>object?</c>, so System.Text.Json hands it back as a <see cref="JsonElement"/> rather than
    /// a dictionary - it has to be read as one.
    /// </summary>
    static string? ResultMessage(OperationResult result)
    {
        if (result.Result is not JsonElement { ValueKind: JsonValueKind.Object } payload) return null;

        return payload.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : null;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A node count as "1 node" / "28838 nodes".
///
/// <para>Written because the onboarding Projects step rendered <c>&lt;Run Text="{Binding NodeCount}"
/// /&gt;&lt;Run Text="nodes" /&gt;</c>, which says "1 nodes" for a project with one node â€” measured
/// on a freshly added single-script project during the Task 12 Step 8 walk. Exactly the defect
/// <see cref="CallCountConverter"/> was written for on the Charon side, in the one other place the
/// shell renders a count; a static <c>Format</c> so a test can assert the boundary without a
/// converter instance or a <c>Window</c>.</para>
/// </summary>
public sealed class NodeCountConverter : IValueConverter
{
    public static string Format(int count) => count == 1 ? "1 node" : $"{count} nodes";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count ? Format(count) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
