using System.Diagnostics;

namespace Hades.Cli;

/// <summary>
/// <c>hades serve</c> - runs the core in the foreground of the calling terminal and exits with it.
///
/// <b>There is deliberately no supervised <c>hades start</c>.</b> Supervision is the shell's job; a
/// CLI that spawned a detached, unsupervised core would leave exactly the hanging state this project
/// refuses to create - a process nothing owns, nothing restarts, and nothing will ever stop.
///
/// This composes with the shell rather than competing with it: a core started here writes the same
/// discovery file as any other, so a shell launched afterwards simply ADOPTS it, and its ownership
/// footer then reads "quitting Hades leaves it running" - which is exactly true, because this
/// terminal owns it.
///
/// <b>It runs the core, it does not host it.</b> <c>Hades.Cli</c> is a control-API client and is
/// barred from referencing <c>Hades.Server</c> by the same three-layer guard the shell is, so
/// serving in-process is not merely undesirable here, it will not compile.
/// </summary>
public static class Serve
{
    /// <summary>
    /// Starts the core and waits for it, returning its exit code.
    ///
    /// stdout and stderr are INHERITED, not redirected: this is a foreground command, and the point
    /// is that the core's own logging goes to the terminal the user is looking at. Inheriting the
    /// console also means Ctrl+C reaches the core directly as part of the same console control
    /// group, so there is no signal forwarding to write - and nothing here that could get it wrong.
    /// </summary>
    public static async Task<int> RunAsync(TextWriter output, string[] extraArguments)
    {
        var launch = CoreLocator.Resolve();

        if (launch is null)
        {
            output.WriteLine(
                "error: could not find the Hades core. Looked for it beside this executable and for a "
                + "source checkout to fall back on. If you are running from a source tree, build "
                + "Core/src/Hades.Server first.");

            return 1;
        }

        var arguments = string.Join(' ', new[] { launch.Arguments }.Concat(extraArguments)
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        output.WriteLine($"Starting the Hades core: {launch.Executable} {arguments}".TrimEnd());

        var startInfo = new ProcessStartInfo
        {
            FileName = launch.Executable,
            Arguments = arguments,
            // False so the child shares this console rather than getting its own window, which is
            // what makes "foreground" mean anything.
            UseShellExecute = false,
        };

        if (launch.WorkingDirectory is { } workingDirectory)
        {
            // The core reads appsettings.json from its CURRENT directory, so launching it from
            // anywhere else silently drops its own configuration - a mistake already made once in
            // the Windows shell and fixed there for the same reason.
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            output.WriteLine($"error: could not start {launch.Executable}.");
            return 1;
        }

        await process.WaitForExitAsync();

        return process.ExitCode;
    }
}
