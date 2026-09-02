using System.Drawing;

namespace Hades.Shell.Tests;

/// <summary>
/// Guards the PRODUCT icon - the mark Windows shows in the Start menu, the taskbar, Alt+Tab,
/// Explorer and both windows' title bars.
///
/// This exists because its absence shipped. ApplicationIcon was simply missing from
/// Hades.Shell.csproj, so the executable carried .NET's generic default and every one of those
/// surfaces showed a blank page with a blue square - through a fully green suite, because nothing
/// in it could ask whether the app had an icon. It was found by looking at a Start menu search
/// result. That is a poor last line of defence for something a build file can silently drop.
///
/// The tray icons next door are checked as WPF resources; this one cannot be, and deliberately so.
/// app.ico is excluded from the &lt;Resource&gt; glob because ApplicationIcon already embeds it as a
/// Win32 resource, and a second copy would be 372 KB nothing reads. So this reads it back out of
/// the compiled executable, which is the only place it actually has to be correct.
/// </summary>
public class ProductIconTests
{
    [Fact]
    public void TheExecutableCarriesTheProductIcon()
    {
        // The shell is a ProjectReference, so its apphost is copied beside these tests.
        var exe = Path.Combine(AppContext.BaseDirectory, "Hades.Shell.exe");
        Assert.True(File.Exists(exe), $"the shell executable is not beside the tests at {exe}");

        // ExtractAssociatedIcon reads the executable's own Win32 icon resource - which is exactly
        // what the shell reads for a window with no Icon set, and what Explorer draws.
        using var embedded = Icon.ExtractAssociatedIcon(exe)
            ?? throw new InvalidOperationException($"no icon could be read from {exe}");
        using var actual = embedded.ToBitmap();

        using var stream = typeof(ProductIconTests).Assembly.GetManifestResourceStream("app.ico")
            ?? throw new InvalidOperationException("app.ico is not embedded in the test assembly");
        using var reference = new Icon(stream, actual.Width, actual.Height).ToBitmap();

        // Compared pixel by pixel rather than by size or byte length: a stale or wrong icon is
        // still an icon of the right dimensions, and "some icon is present" is not the claim.
        var differing = 0;
        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                if (actual.GetPixel(x, y).ToArgb() != reference.GetPixel(x, y).ToArgb())
                {
                    differing++;
                }
            }
        }

        Assert.True(differing == 0,
            $"the executable's icon differs from Icons/app.ico in {differing} of "
            + $"{actual.Width * actual.Height} pixels at {actual.Width}x{actual.Height} - "
            + "ApplicationIcon is missing, points elsewhere, or the committed icon is stale");
    }
}
