using System.Diagnostics;
using System.Text;

namespace Hades.Server.Tests;

/// <summary>
/// TEMPORARY instrumentation for root-causing the "Directory not empty" teardown flake (rotates
/// across MaterialApplyTests/QueryToolsTests/AnimationApplyTests/ToolCallTests/MemoryToolsTests,
/// ~1 in 2-4 full <c>Hades.Server.Tests</c> runs). Wraps the exact same
/// <c>Directory.Delete(dir, recursive: true)</c> every one of those classes' own <c>Dispose()</c>
/// already performs - see e.g. <see cref="EditorToolTestBase"/>'s own Dispose comment for the
/// history - and on failure captures what is actually still in the directory, plus (best-effort)
/// which process holds it open via <c>lsof</c>, before rethrowing UNCHANGED so pass/fail behaviour
/// is otherwise identical to today. Diagnostic capture only fires when
/// <c>HADES_TEARDOWN_DIAG_LOG</c> is set (a run not investigating this flake pays zero cost beyond
/// one extra env var read per delete).
///
/// Not a fix - this only makes the next reproduction self-explaining instead of a bare exception.
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
