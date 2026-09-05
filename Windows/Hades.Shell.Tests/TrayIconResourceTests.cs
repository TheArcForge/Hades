using System.Windows;
using Hades.Control.Client.Dtos;
using Hades.Shell.Tray;
using Hades.Supervision;

namespace Hades.Shell.Tests;

/// <summary>
/// Guards the six tray .ico files as SHIPPED RESOURCES, not as pictures - whether they look right is
/// a hand-run's job (Task 3 Step 7). These assertions exist because both failure modes below have
/// already happened once and neither is visible in a diff of a binary file.
/// </summary>
public class TrayIconResourceTests
{
    // Matches Icons/generate-icons.ps1 and StatusGlyph.cs. A state whose icon is missing shows up
    // here rather than as a blank notification area.
    public static TheoryData<string> IconNames() => new()
    {
        "idle", "indexing", "attached", "leaseHeld", "error", "unknown", "notRunning",
    };

    /// <summary>
    /// Every state the tray can be in must name an icon that actually ships. These two mappings are
    /// the only way an icon gets chosen, so walking every enum member here means no reachable state
    /// can point at a missing file - which would surface as an exception on a state transition the
    /// user happened to hit, long after anyone was looking.
    /// </summary>
    [Fact]
    public void EveryIconStateMapsToAShippedIcon()
    {
        foreach (var state in Enum.GetValues<ControlIconState>())
        {
            Assert.NotEmpty(ReadIcon(TrayIcon.IconNameFor(state)));
        }
    }

    [Fact]
    public void EveryMenuContentKindMapsToAShippedIcon()
    {
        var contents = new[]
        {
            MenuContent.NotRunning,
            MenuContent.Restarting(1),
            MenuContent.Failed(3),
            MenuContent.Running(Ownership.Spawned, SummaryFixture.Idle()),
        };

        foreach (var content in contents)
        {
            Assert.NotEmpty(ReadIcon(TrayIcon.IconNameFor(content)));
        }
    }

    /// <summary>
    /// notRunning must not collapse onto idle. "No core is running" and "a core is running with
    /// nothing to do" are different facts, and the ownership footer exists because confusing them
    /// has consequences.
    /// </summary>
    [Fact]
    public void NotRunningIsVisuallyDistinctFromIdle()
    {
        Assert.NotEqual(
            TrayIcon.IconNameFor(MenuContent.NotRunning),
            TrayIcon.IconNameFor(ControlIconState.Idle));
    }

    // Pack URIs resolve against the entry assembly, which under a test host is testhost.exe. Setting
    // ResourceAssembly points them at the shell instead. It is write-once - assigning it a second
    // time throws InvalidOperationException - so this belongs in a static constructor, which the
    // runtime guarantees runs exactly once, rather than in the per-test helper below.
    static TrayIconResourceTests()
    {
        if (Application.ResourceAssembly is null)
        {
            Application.ResourceAssembly = typeof(TrayIcon).Assembly;
        }
    }

    static byte[] ReadIcon(string name)
    {
        var uri = new Uri($"pack://application:,,,/Hades.Shell;component/Icons/{name}.ico");
        using var stream = Application.GetResourceStream(uri).Stream;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Theory]
    [MemberData(nameof(IconNames))]
    public void EveryStateShipsAnIconResource(string name)
    {
        Assert.NotEmpty(ReadIcon(name));
    }

    /// <summary>
    /// The notification area picks an entry by DPI. A single-size .ico is visibly resampled on any
    /// scaled display, so the plan requires at least 16 and 32 to be present.
    /// </summary>
    [Theory]
    [MemberData(nameof(IconNames))]
    public void EveryIconCarriesAtLeastThe16And32PixelSizes(string name)
    {
        var sizes = DirectoryEntries(ReadIcon(name)).Select(e => e.Width).ToList();

        Assert.Contains(16, sizes);
        Assert.Contains(32, sizes);
    }

    /// <summary>
    /// Every image inside the .ico must be a BMP/DIB, never PNG-compressed.
    ///
    /// Windows Explorer accepts PNG inside .ico and has since Vista, so a PNG-compressed file looks
    /// perfectly valid everywhere except the one place that matters: GDI+ reads the PNG bytes as
    /// though they were a DIB and renders noise, and NotifyIcon.Icon is a System.Drawing.Icon. The
    /// first generated set was PNG and put coloured static in the tray. Nothing about the file is
    /// obviously wrong when that happens, which is why this is pinned rather than left to review.
    /// </summary>
    [Theory]
    [MemberData(nameof(IconNames))]
    public void NoImageIsPngCompressed(string name)
    {
        var bytes = ReadIcon(name);
        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        foreach (var entry in DirectoryEntries(bytes))
        {
            var start = bytes.AsSpan(entry.Offset, pngSignature.Length);
            Assert.False(start.SequenceEqual(pngSignature),
                $"{name}.ico's {entry.Width}px image is PNG-compressed; GDI+ renders those as noise in the tray");
        }
    }

    static List<(int Width, int Offset)> DirectoryEntries(byte[] ico)
    {
        // ICONDIR: reserved(2) type(2) count(2), then count * ICONDIRENTRY(16).
        Assert.True(ico.Length > 6, "not an .ico: shorter than its own header");
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2)); // type 1 = icon

        var count = BitConverter.ToUInt16(ico, 4);
        var entries = new List<(int, int)>(count);
        for (var i = 0; i < count; i++)
        {
            var e = 6 + (16 * i);
            // Width is one byte, where 0 means 256. Every size shipped here is smaller than that,
            // but decode it properly rather than relying on that staying true.
            var width = ico[e] == 0 ? 256 : ico[e];
            entries.Add((width, (int)BitConverter.ToUInt32(ico, e + 12)));
        }
        return entries;
    }
}
