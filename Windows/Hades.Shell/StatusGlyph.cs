using Hades.Control.Client.Dtos;
using Hades.Shell.Tray;

namespace Hades.Shell;

/// <summary>
/// The one place a <see cref="ControlIconState"/> / <see cref="ControlSeverity"/> /
/// <see cref="OperationState"/> / <see cref="TraceOutcome"/> becomes a Segoe icon glyph. The port of
/// <c>Mac/HadesApp/Sources/HadesApp/StatusIcon.swift</c>, and it obeys that file's contract exactly.
///
/// THESE ARE PICTURES, NOT WORDS. Like the Mac's StatusIcon, this picks a glyph and nothing else. It
/// never maps a state to display TEXT - the core authors every string the user reads, and a shell
/// that invented "Indexing…" or "Attached" here would be re-deriving behaviour the core already owns.
///
/// Every mapping is one-to-one and fixed at compile time. No mapping compares two pieces of data:
/// precedence (which project's state wins, whether an error outranks an in-progress index) already
/// happened server-side to produce the single value being switched on. That is the "no precedence
/// logic in the shell" rule from Spec #3, and it applies here for the same reason it applies to Swift.
///
/// CODEPOINTS ARE MEASURED, NOT ASSUMED. Every glyph below was verified present in BOTH
/// Segoe Fluent Icons (SegoeIcons.ttf, Windows 11) and Segoe MDL2 Assets (segmdl2.ttf, the Windows 10
/// fallback), and then RENDERED AND LOOKED AT at 16px and 32px before being chosen - the documented
/// MDL2 names are misleading on their own. E91F is named "CircleRing" and draws a FILLED dot; E739 is
/// named "RadioBtnOff" and draws a square. Two mappings are deliberate approximations because neither
/// font has the shape the Mac uses, and both are noted at their use site.
/// </summary>
public static class StatusGlyph
{
    // Named so the switches below read as intent rather than as hex.
    // Written as \u escapes, not as literal characters: these are Private Use Area codepoints that
    // render as blank boxes or nothing at all in most editors and diffs, so the literal form hides
    // exactly the detail a reader needs to check.
    const string OutlineCircle    = "\uECCA"; // ring: the "nothing is happening" mark
    const string Sync             = "\uE895"; // two circular arrows, closest to arrow.triangle.2.circlepath
    const string CheckCircleSolid = "\uEC61";
    const string Padlock          = "\uE72E";
    const string WarningTriangle  = "\uE7BA";
    const string QuestionCircle   = "\uE9CE";
    const string FilledDot        = "\uE91F";
    const string CrossCircleSolid = "\uEB90";
    const string DottedCircle     = "\uF16A"; // supervision-only: there is nothing there to ask

    /// <summary>
    /// <see cref="ControlIconState"/> -> glyph. Covers Unknown too: UnknownFallbackConverter decodes
    /// any unrecognised wire value to it, so a NEWER core adding an iconState case cannot crash an
    /// OLDER shell - it shows the neutral "this build does not recognise that" glyph instead.
    /// </summary>
    public static string For(ControlIconState state) => state switch
    {
        ControlIconState.Idle => OutlineCircle,
        ControlIconState.Indexing => Sync,
        ControlIconState.Attached => CheckCircleSolid,
        // The Mac uses lock.circle.fill. Neither Segoe font has a filled lock-in-a-circle, so this is
        // the bare padlock - the only shape either font offers for "held".
        ControlIconState.LeaseHeld => Padlock,
        ControlIconState.Error => WarningTriangle,
        _ => QuestionCircle,
    };

    /// <summary>
    /// <see cref="ControlSeverity"/> -> glyph, for the small accent drawn next to a per-project row.
    /// </summary>
    public static string For(ControlSeverity severity) => severity switch
    {
        ControlSeverity.Ok => FilledDot,
        ControlSeverity.Warning => WarningTriangle,
        // The Mac uses xmark.octagon.fill. Neither Segoe font has a filled octagon, so this is the
        // filled cross-in-circle - the same "stop, this failed" reading at 16px.
        ControlSeverity.Error => CrossCircleSolid,
        _ => QuestionCircle,
    };

    /// <summary>
    /// <see cref="OperationState"/> -> glyph, for a polled rebuild's own state. An icon is the only
    /// display this type invents, and deliberately so: unlike a project row, which pairs its state
    /// with a sibling server-authored status string, OperationResult carries no status text to print.
    /// </summary>
    public static string For(OperationState state) => state switch
    {
        OperationState.Running => Sync,
        OperationState.Done => CheckCircleSolid,
        OperationState.Failed => CrossCircleSolid,
        _ => QuestionCircle,
    };

    /// <summary>
    /// <see cref="TraceOutcome"/> -> glyph. The core already resolved Error for a sequence the moment
    /// any one of its calls failed; this only picks a picture for that already-resolved value.
    /// </summary>
    public static string For(TraceOutcome outcome) => outcome switch
    {
        TraceOutcome.Ok => CheckCircleSolid,
        TraceOutcome.Error => CrossCircleSolid,
        _ => QuestionCircle,
    };

    /// <summary>
    /// <see cref="MenuContent"/> -> glyph for the tray itself, including the three supervision-only
    /// cases <see cref="ControlIconState"/> cannot express because there is no core to ask.
    ///
    /// This is the one seam where the shell necessarily picks an icon on its own - not a precedence
    /// decision among API data, but the unavoidable consequence of there being no API response to
    /// read at all when nothing is running. The Running branch delegates entirely to the API's own
    /// resolved IconState; it never inspects rows or lease to decide anything itself.
    /// </summary>
    public static string For(MenuContent content) => content.Kind switch
    {
        MenuContentKind.NotRunning => DottedCircle,
        MenuContentKind.Restarting => Sync,
        MenuContentKind.Failed => CrossCircleSolid,
        MenuContentKind.Running => For(content.Summary!.IconState),
        _ => QuestionCircle,
    };
}
