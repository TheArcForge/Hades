namespace Hades.Shell.ViewModels;

/// <summary>
/// The sidebar destinations inside the main window - Spec #3 §3.2-§3.5. The port of
/// <c>Mac/HadesApp/Sources/HadesApp/MainWindow/Section.swift</c>.
///
/// <b>Charon</b> is the observability surface and <b>Asphodel</b> the project-memory surface. The
/// enum members stay <see cref="Traces"/>/<see cref="Memory"/> - they name what the section IS in
/// the control API, which still speaks traces and memory; the titles are the product names users
/// read. Those product names are deliberate and are not to be renamed to generic labels.
///
/// <b><see cref="Settings"/> IS a destination here, where the Mac's Section deliberately omits it.</b>
/// That divergence is the platform, not a decision: on macOS, Settings is a standard Settings scene
/// reachable by Cmd-comma, like every other Mac app, so putting it in the sidebar would be wrong
/// there. Windows has no such convention - a Windows app's settings live inside the app, at the
/// bottom of its navigation pane. Same destination, different idiom, which is the rule this port
/// follows throughout: what each surface shows does not change, how it is reached does.
/// </summary>
public enum Section
{
    Projects,
    Traces,
    Memory,
    Settings,
}

public static class SectionExtensions
{
    /// <summary>
    /// Fixed sidebar chrome, not control-API data - the same allowance the ownership footer already
    /// uses. A sidebar destination's name is UI navigation, not a rendered DTO field, so literal
    /// copy here does not breach "every string the core can author comes from the core".
    /// </summary>
    public static string Title(this Section section) => section switch
    {
        Section.Projects => "Projects",
        Section.Traces => "Charon",
        Section.Memory => "Asphodel",
        Section.Settings => "Settings",

        // Unreachable while the switch is exhaustive, and present so that adding a member without
        // a title fails visibly in the UI rather than throwing somewhere far from the cause.
        _ => section.ToString(),
    };
}
