using System.Runtime.InteropServices;

namespace Hades.Shell.ShellFacts;

/// <summary>Whether the machine is in a battery-saver state. Behind a seam for the same reason
/// <see cref="ILaunchAtLogin"/> is: a test must not depend on this machine's actual power state.</summary>
public interface IPowerStatusReader
{
    bool IsBatterySaverOn { get; }
}

/// <summary>
/// The second of the two OS facts only the shell can observe - the core is a separate, headless
/// process and cannot see the machine's power state at all. The Windows counterpart to the Mac's
/// <c>ResourceGuardReader</c>.
///
/// <b>DISPLAY-ONLY</b>, matching the Mac's treatment of Low Power Mode: it is shown in Settings and
/// nothing else consumes it. This is not a licence to compute - it hands back one raw OS value and
/// decides nothing about whether work "should" pause.
///
/// The Mac's thermal state is dropped: Windows has no analogue, and inventing one would be the shell
/// deciding something rather than reporting it.
/// </summary>
public sealed partial class WindowsPowerStatus : IPowerStatusReader
{
    /// <summary>
    /// <c>SYSTEM_POWER_STATUS.SystemStatusFlag</c>: 1 means battery saver is on.
    ///
    /// UNVERIFIED AGAINST WINDOWS 11 24H2's plugged-in "energy saver", which is a newer feature than
    /// this flag - Task 10's hand-run is what decides whether this row is honest enough to keep. If
    /// the flag does not reflect it, the row should be dropped rather than shown wrong: a settings
    /// screen that quietly reports the opposite of reality is worse than one that stays silent.
    /// </summary>
    public bool IsBatterySaverOn
    {
        get
        {
            try
            {
                return GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1;
            }
            catch (Exception)
            {
                // A power state we cannot read is reported as "not on" rather than crashing a
                // settings screen over a display-only row.
                return false;
            }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;

        /// <summary>Documented as "SystemStatusFlag" since Windows 10: 1 when battery saver is on.
        /// This field was "Reserved1" before that, which is why older references show it as unused.</summary>
        public byte SystemStatusFlag;

        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
