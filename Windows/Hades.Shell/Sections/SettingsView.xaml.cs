using System.Windows.Controls;

namespace Hades.Shell.Sections;

/// <summary>
/// The Settings section. Deliberately has NO handlers: the one control that does anything - the
/// launch-at-login checkbox - binds TwoWay straight to the view model, which owns both the write and
/// the re-read.
///
/// <para>It used to hold an <c>OnToggleLaunchAtLogin</c> handler whose comment argued that a OneWay
/// binding was safer, because "a two-way binding would let the box show the value the user clicked
/// rather than the value the OS ended up in". The reasoning was sound and the implementation
/// defeated it: the handler assigned <c>IsChecked</c> to snap the box back, and assigning a
/// dependency property that carries a binding replaces that binding with a local value - so after
/// one click the box stopped tracking the view model at all, which is a stronger version of the
/// failure the comment was guarding against. It also put the logic on <c>Click</c>, which
/// <c>TogglePattern.Toggle()</c> never raises, leaving the control inoperable by assistive
/// technology. Both were measured on a real machine; see SettingsViewModel.LaunchAtLoginEnabled.</para>
///
/// <para>The guarantee the old comment wanted is kept, and now actually holds: the view model's
/// setter writes, stores what the OS reports back, and always raises PropertyChanged, so a request
/// the OS refuses pulls the checkbox back to the truth.</para>
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
