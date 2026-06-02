using System.IO;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    /// <summary>
    /// Tracks Unity test runs across the domain reload that EditMode runs can trigger.
    ///
    /// A run's final result is only available after <c>RunFinished</c>, which for EditMode fires
    /// on a later editor tick (past a domain reload) — so <c>project_run_tests</c> cannot return
    /// results directly. The Unity Test Framework writes the completed run's NUnit3 results to
    /// <c>persistentDataPath/TestResults.xml</c>. We record that file's modification time at run
    /// start (in <see cref="SessionState"/>, which survives the reload); the run is considered
    /// complete once the file is newer than that baseline. <c>project_get_test_results</c> reads
    /// it on poll.
    /// </summary>
    public static class TestRunResultStore
    {
        const string StartedKey = "Hades.TestRun.Started";
        const string BaselineKey = "Hades.TestRun.BaselineTicks";

        public static string ResultsPath =>
            Path.Combine(Application.persistentDataPath, "TestResults.xml");

        public static bool HasStarted => SessionState.GetString(StartedKey, "") == "1";

        /// <summary>Records a baseline so a stale TestResults.xml from a prior run is not mistaken
        /// for this run's output. Call immediately before executing the run.</summary>
        public static void MarkStarted()
        {
            long baseline = File.Exists(ResultsPath)
                ? File.GetLastWriteTimeUtc(ResultsPath).Ticks
                : 0L;
            SessionState.SetString(BaselineKey, baseline.ToString());
            SessionState.SetString(StartedKey, "1");
        }

        /// <summary>True once TestResults.xml has been (re)written since the last MarkStarted.</summary>
        public static bool IsComplete()
        {
            if (!HasStarted || !File.Exists(ResultsPath)) return false;
            long baseline = long.TryParse(SessionState.GetString(BaselineKey, "0"), out var b) ? b : 0L;
            return File.GetLastWriteTimeUtc(ResultsPath).Ticks > baseline;
        }
    }
}
