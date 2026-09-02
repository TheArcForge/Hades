using Hades.Control.Client.Dtos;
using Hades.Shell.Tray;
using Hades.Supervision;
using WinForms = System.Windows.Forms;

namespace Hades.Shell.Tests;

/// <summary>
/// The adapter is UI code and was originally left untested on the grounds that it is thin and
/// decision-free. It is thin, and it still crashed the shell: ToolStripItem.Dispose() REMOVES the
/// item from its owner's collection, so disposing while enumerating strip.Items threw "Collection
/// was modified" on the second Apply - which is the first poll tick after launch, i.e. every run.
/// "Too simple to break" is not a property that survives contact with WinForms.
/// </summary>
public class TrayMenuAdapterTests
{
    [Fact]
    public void ApplyingTwice_ReplacesEveryItem_AndDoesNotThrow()
    {
        using var strip = new WinForms.ContextMenuStrip();

        TrayMenuAdapter.Apply(strip, TrayMenuBuilder.Build(SupervisorState.NotStarted, summary: null));

        var running = TrayMenuBuilder.Build(
            SupervisorState.Running(Ownership.Spawned),
            SummaryFixture.WithProject("MyGame", "Indexed, 1204 nodes"));
        TrayMenuAdapter.Apply(strip, running);

        Assert.Equal(running.Count, strip.Items.Count);
        Assert.Contains(strip.Items.Cast<WinForms.ToolStripItem>(), i => i.Text == "Indexed, 1204 nodes");

        // No leftovers from the first menu.
        Assert.DoesNotContain(strip.Items.Cast<WinForms.ToolStripItem>(), i => i.Text == "Hades is not running");
    }

    [Fact]
    public void SeparatorsBecomeSeparators_AndCommandsStayEnabled()
    {
        using var strip = new WinForms.ContextMenuStrip();

        var items = TrayMenuBuilder.Build(SupervisorState.NotStarted, summary: null);
        TrayMenuAdapter.Apply(strip, items);

        Assert.Contains(strip.Items.Cast<WinForms.ToolStripItem>(), i => i is WinForms.ToolStripSeparator);

        var quit = strip.Items.Cast<WinForms.ToolStripItem>().Single(i => i.Text == "Quit Hades");
        Assert.True(quit.Enabled);
    }

    /// <summary>
    /// An informational line is a label, not a command. Rendering one enabled invites a click that
    /// does nothing, which reads as a broken menu.
    /// </summary>
    [Fact]
    public void InformationalLinesAreDisabled()
    {
        using var strip = new WinForms.ContextMenuStrip();

        TrayMenuAdapter.Apply(strip, TrayMenuBuilder.Build(SupervisorState.NotStarted, summary: null));

        var label = strip.Items.Cast<WinForms.ToolStripItem>().Single(i => i.Text == "Hades is not running");
        Assert.False(label.Enabled);
    }

    /// <summary>
    /// The status line is indented with padding, never by prefixing spaces: its text is the core's
    /// own string and has to reach the screen byte for byte.
    /// </summary>
    [Fact]
    public void DetailLinesAreIndentedByPadding_NotByAlteringTheText()
    {
        using var strip = new WinForms.ContextMenuStrip();

        TrayMenuAdapter.Apply(strip, TrayMenuBuilder.Build(
            SupervisorState.Running(Ownership.Spawned),
            SummaryFixture.WithProject("MyGame", "Indexed, 1204 nodes")));

        var status = strip.Items.Cast<WinForms.ToolStripItem>().Single(i => i.Text == "Indexed, 1204 nodes");

        Assert.Equal("Indexed, 1204 nodes", status.Text);
        Assert.True(status.Padding.Left > 0, "the status line should be indented");
    }

    [Fact]
    public void ClickingACommandInvokesItsAction()
    {
        using var strip = new WinForms.ContextMenuStrip();
        var quit = 0;

        TrayMenuAdapter.Apply(strip, TrayMenuBuilder.Build(
            SupervisorState.NotStarted,
            summary: null,
            new TrayMenuActions { OnQuit = () => quit++ }));

        strip.Items.Cast<WinForms.ToolStripItem>().Single(i => i.Text == "Quit Hades").PerformClick();

        Assert.Equal(1, quit);
    }
}
