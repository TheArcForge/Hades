using Microsoft.Win32;

namespace Hades.Shell.ShellFacts;

/// <summary>
/// The two registry locations launch-at-login actually lives in, behind a seam so tests never touch
/// the real one. Registering a login item is a genuine side effect on the developer's machine, and a
/// suite that did it as a consequence of running would be changing what the OS does at boot.
/// </summary>
public interface IStartupRegistry
{
    string? GetRunValue(string name);
    void SetRunValue(string name, string commandLine);
    void DeleteRunValue(string name);

    /// <summary>The raw <c>StartupApproved\Run</c> bytes, or null when there is no entry.</summary>
    byte[]? GetStartupApproval(string name);

    void DeleteStartupApproval(string name);
}

/// <summary>Reads and writes launch-at-login. Fakeable so the view model can be tested.</summary>
public interface ILaunchAtLogin
{
    /// <summary>The OS's own current state - never a cached value, never the value last requested.</summary>
    bool IsEnabled { get; }

    /// <summary>Requests a change and returns what the OS says AFTERWARDS.</summary>
    bool SetEnabled(bool enabled);
}

/// <summary>
/// Whether Hades is registered to launch at login. The port of
/// <c>Mac/HadesApp/Sources/HadesApp/ShellFacts/LaunchAtLoginService.swift</c>, and it keeps that
/// file's non-negotiable discipline: <b>write, then re-read the OS, and report only what the re-read
/// says.</b> Success is never inferred from the absence of an error.
///
/// <b>WINDOWS STORES THE USER'S DECISION IN A SECOND PLACE, AND MISSING IT IS THE BUG THIS TYPE
/// EXISTS TO PREVENT.</b> Disabling a startup entry in Task Manager does NOT remove the
/// <c>Run</c> value - Windows records a veto under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run</c> instead. An
/// implementation that writes the Run value and re-reads only the Run value therefore reports "on"
/// forever while Windows never launches the app. So:
///
/// <list type="bullet">
/// <item>READ: enabled only when the Run value exists AND StartupApproved either has no entry or
/// holds an enabled state.</item>
/// <item>WRITE: enabling writes the Run value and DELETES any StartupApproved entry. It does not try
/// to author that entry's state bytes, whose format is undocumented - deleting returns the app to
/// the OS's own default-enabled path, which is the honest way to express "the user just re-enabled
/// this from inside the app".</item>
/// </list>
/// </summary>
public sealed class LaunchAtLogin(IStartupRegistry registry, string commandLine) : ILaunchAtLogin
{
    /// <summary>The Run value's name. Also what Task Manager shows in its Startup list.</summary>
    public const string ValueName = "Hades";

    public bool IsEnabled
    {
        get
        {
            try
            {
                return registry.GetRunValue(ValueName) is not null && !IsVetoed();
            }
            catch (Exception)
            {
                // An unreadable registry is not a crash: report the honest "cannot confirm it is on".
                return false;
            }
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                registry.SetRunValue(ValueName, commandLine);

                // Delete rather than author - see this type's own doc comment.
                registry.DeleteStartupApproval(ValueName);
            }
            else
            {
                registry.DeleteRunValue(ValueName);
            }
        }
        catch (Exception)
        {
            // Deliberately swallowed, and deliberately NOT treated as failure on its own: the
            // re-read below is the only thing that decides what gets reported.
        }

        return IsEnabled;
    }

    /// <summary>
    /// Whether StartupApproved vetoes the Run entry.
    ///
    /// The byte format is undocumented; what is observable is that the first byte's low bit is set
    /// for a disabled entry (0x03, 0x07) and clear for an enabled one (0x02, 0x06). That heuristic
    /// is used for READING only - and is precisely why the write path deletes the entry instead of
    /// trying to construct one.
    /// </summary>
    bool IsVetoed()
    {
        var approval = registry.GetStartupApproval(ValueName);

        // No entry at all is the OS's default-enabled state, not a veto.
        if (approval is null || approval.Length == 0) return false;

        return (approval[0] & 1) == 1;
    }
}

/// <summary>The real registry. Not unit tested: it is a pass-through to HKCU, and exercising it in a
/// suite would register a real login item.</summary>
public sealed class WindowsStartupRegistry : IStartupRegistry
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string? GetRunValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(name) as string;
    }

    public void SetRunValue(string name, string commandLine)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key?.SetValue(name, commandLine, RegistryValueKind.String);
    }

    public void DeleteRunValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public byte[]? GetStartupApproval(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
        return key?.GetValue(name) as byte[];
    }

    public void DeleteStartupApproval(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
