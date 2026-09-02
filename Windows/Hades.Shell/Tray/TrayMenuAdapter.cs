using System.Drawing;
using WinForms = System.Windows.Forms;

namespace Hades.Shell.Tray;

/// <summary>
/// The only place <see cref="TrayMenuItem"/> data becomes WinForms controls. Kept deliberately thin
/// and decision-free: it lays out what the builder already decided and adds nothing of its own, so
/// that everything worth testing about the menu stays in <see cref="TrayMenuBuilder"/>, where it can
/// be tested without a UI thread.
/// </summary>
internal static class TrayMenuAdapter
{
    public static void Apply(WinForms.ContextMenuStrip strip, IReadOnlyList<TrayMenuItem> items)
    {
        // Snapshot, then Clear, THEN dispose - in that order, and none of the three is optional.
        //
        // Clear() alone leaks: it removes items without disposing them, and this runs on every poll
        // in a process designed to sit in the tray for days. But disposing while enumerating
        // strip.Items throws "Collection was modified", because ToolStripItem.Dispose() REMOVES the
        // item from its owner's collection as part of disposing - so the obvious dispose-then-clear
        // loop mutates the very collection it is walking. It crashed the shell on the second
        // Update, which is to say on the first poll tick after launch.
        var previous = strip.Items.Cast<WinForms.ToolStripItem>().ToArray();
        strip.Items.Clear();

        foreach (var item in previous)
        {
            item.Dispose();
        }

        foreach (var item in items)
        {
            strip.Items.Add(item.IsSeparator ? new WinForms.ToolStripSeparator() : Build(item));
        }
    }

    static WinForms.ToolStripItem Build(TrayMenuItem item)
    {
        var control = new WinForms.ToolStripMenuItem(item.Text)
        {
            // An informational line is a label, not a command: showing it enabled invites a click
            // that does nothing.
            Enabled = item.Enabled,
        };

        if (item.Action is { } action)
        {
            control.Click += (_, _) => action();
        }

        if (item.IsDetail)
        {
            // Indented with padding rather than by prefixing spaces to Text. The text on this line
            // is the core's own status string and must reach the screen byte for byte; padding is
            // layout, a prefix would make the shell the author of it.
            control.Padding = new WinForms.Padding(16, 0, 0, 0);
            control.ForeColor = SystemColors.GrayText;
        }

        return control;
    }
}
