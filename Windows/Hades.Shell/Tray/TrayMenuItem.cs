namespace Hades.Shell.Tray;

/// <summary>
/// One line of the tray menu, as plain data. The menu is modelled this way - rather than as
/// WinForms controls - so the thing worth testing (what the menu SAYS, in which order) is testable
/// headlessly, and so <see cref="TrayMenuBuilder"/> never touches a UI type.
/// <see cref="TrayMenuAdapter"/> is the only place these become a ContextMenuStrip.
/// </summary>
public sealed record TrayMenuItem
{
    /// <summary>
    /// The text shown. Where this carries a value the core authored - a headline, a project name, a
    /// project's status - it is that value VERBATIM. The shell never re-words or reformats it, and
    /// never joins two core-authored fields into one new string.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>Clickable. Informational lines are disabled - they are labels, not commands.</summary>
    public bool Enabled { get; init; }

    /// <summary>What clicking does, when anything.</summary>
    public Action? Action { get; init; }

    public bool IsSeparator { get; init; }

    /// <summary>
    /// A subordinate line belonging to the item above it (a project's status under its name). The
    /// adapter indents these. This exists so a project's name and status can each be printed
    /// verbatim on their own line, exactly as ProjectRowView stacks two Text views, instead of the
    /// shell concatenating them into a string neither the core nor the user ever authored.
    /// </summary>
    public bool IsDetail { get; init; }

    /// <summary>
    /// The Segoe glyph for this line's state, from <see cref="StatusGlyph"/>, or null where the line
    /// has no state of its own. Carried as data rather than resolved by the adapter so that what the
    /// menu shows stays decided in one testable place.
    /// </summary>
    public string? Glyph { get; init; }

    public static TrayMenuItem Separator() => new() { Text = string.Empty, IsSeparator = true };

    /// <summary>An informational line: shown, never clickable.</summary>
    public static TrayMenuItem Label(string text, string? glyph = null, bool isDetail = false) =>
        new() { Text = text, Glyph = glyph, IsDetail = isDetail };

    public static TrayMenuItem Command(string text, Action? action, bool enabled = true) =>
        new() { Text = text, Action = action, Enabled = enabled };
}
