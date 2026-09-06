using System.Diagnostics;
using System.Text;

namespace Hades.Server.Tests;

/// <summary>
/// Opt-in instrumentation that root-caused the "Directory not empty" teardown flake, kept as a
/// tripwire in case it ever comes back. Wraps the exact same
/// <c>Directory.Delete(dir, recursive: true)</c> every affected class' own <c>Dispose()</c>
/// already performs - see e.g. <see cref="EditorToolTestBase"/>'s own Dispose comment for the
/// history - and on failure captures what is actually still in the directory, plus (best-effort)
/// which process holds it open via <c>lsof</c>, before rethrowing UNCHANGED so pass/fail behaviour
/// is identical either way. Capture only fires when <c>HADES_TEARDOWN_DIAG_LOG</c> is set (a run
/// not investigating this pays one extra env var read per delete).
///
/// <para><b>What it found.</b> The flake rotated across
/// MaterialApplyTests/QueryToolsTests/AnimationApplyTests/ToolCallTests/MemoryToolsTests at ~1 in
/// 2-4 full <c>Hades.Server.Tests</c> runs, which is what made it look like an unlucky test rather
/// than a defect. The cause was <c>ObservationService.Dispose()</c> not waiting for a sweep already
/// in flight, so a sweep could recreate an entry underneath a directory mid-delete. It is fixed
/// there, and <see cref="HostTeardown"/> carries the full account of why the same bug presented as
/// a rare flake on macOS and a hard failure on Windows. 14 consecutive full-suite runs after the
/// fix produced none.</para>
///
/// This was never itself a fix - it only made the reproduction self-explaining instead of a bare
/// exception, which is exactly what it did.
/// </summary>
internal static class TeardownDiagnostics
{
    public static void Delete(params string[] dirs)
    {
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException ex)
            {
                Capture(dir, ex);
                throw;
            }
        }
    }

    static void Capture(string dir, IOException ex)
    {
        var logPath = Environment.GetEnvironmentVariable("HADES_TEARDOWN_DIAG_LOG");
        if (string.IsNullOrEmpty(logPath)) return;

        var report = new StringBuilder();
        report.AppendLine($"---- {DateTime.UtcNow:O} pid={Environment.ProcessId} tid={Environment.CurrentManagedThreadId} ----");
        report.AppendLine($"dir: {dir}");
        report.AppendLine($"exception: {ex}");

        try
        {
            var survivors = Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).ToList();
            report.AppendLine($"survivors ({survivors.Count}):");
            foreach (var entry in survivors)
            {
                var kind = Directory.Exists(entry) ? "dir " : "file";
                long size = -1;
                try { if (kind == "file") size = new FileInfo(entry).Length; } catch { /* best effort */ }
                report.AppendLine($"  {kind} {entry} (size={size})");
            }
        }
        catch (Exception listEx)
        {
            report.AppendLine($"  (post-failure enumeration itself threw: {listEx})");
        }

        report.AppendLine("lsof +D:");
        report.AppendLine(RunLsof(dir));

        try
        {
            File.AppendAllText(logPath, report.ToString());
        }
        catch
        {
            // Best effort - never let diagnostic capture itself fail the test differently.
        }
    }

    static string RunLsof(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo("lsof", $"+D \"{dir}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "  (lsof did not start)";

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            return (string.IsNullOrWhiteSpace(stdout) ? "  (no lsof matches - nothing has it open right now)\n" : stdout)
                + (string.IsNullOrWhiteSpace(stderr) ? "" : $"  stderr: {stderr}\n");
        }
        catch (Exception lsofEx)
        {
            return $"  (lsof invocation failed: {lsofEx})";
        }
    }
}
