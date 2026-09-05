using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Hades.Control.Client.Dtos;

namespace Hades.Shell.Onboarding;

/// <summary>
/// One project on the Unity-plugin step, plus whether its plugin has been installed during this
/// onboarding run.
///
/// <para>A wrapper rather than binding <see cref="ProjectRow"/> directly, because "installed just
/// now" is view state that no server row carries: <c>POST installPlugin</c> answers with a message,
/// and nothing in <c>GET /control/projects</c> reports plugin presence. Without somewhere to keep it,
/// a row that had just been installed looked exactly like one that had not — the user pressed the
/// button and the button stayed a button.</para>
///
/// <para>Visibility is exposed directly instead of a bool plus a converter. Two properties beat a
/// converter and its inverse for a template this small, and it keeps the XAML free of resources that
/// exist only to negate something.</para>
/// </summary>
public sealed class PluginRow(ProjectRow project) : INotifyPropertyChanged
{
    bool _installed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; } = project.Name;
    public string Path { get; } = project.Path;
    public string ProductGuid { get; } = project.ProductGuid;

    /// <summary>True once this row's own install has succeeded in this session.</summary>
    public bool Installed
    {
        get => _installed;
        set
        {
            if (_installed == value) return;
            _installed = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(ButtonVisibility));
            OnPropertyChanged(nameof(DoneVisibility));
        }
    }

    public Visibility ButtonVisibility => _installed ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DoneVisibility => _installed ? Visibility.Visible : Visibility.Collapsed;

    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
