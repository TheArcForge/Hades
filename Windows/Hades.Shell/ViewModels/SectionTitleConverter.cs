using System.Globalization;
using System.Windows.Data;

namespace Hades.Shell.ViewModels;

/// <summary>
/// Renders a <see cref="Section"/> as its title for binding. A converter rather than a parallel list
/// of view-model wrappers, so the sidebar can bind SelectedItem straight to
/// <see cref="MainWindowViewModel.SelectedSection"/> and there is still exactly one place a
/// section's title is decided - <see cref="SectionExtensions.Title"/>.
/// </summary>
public sealed class SectionTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Section section ? section.Title() : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Section titles are display-only; the sidebar binds the Section itself.");
}
