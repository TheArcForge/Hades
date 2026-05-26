// Editor/Graph/Pipeline/GraphBuildLog.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ArcForge.Hades.Editor.Graph.Pipeline
{
    /// <summary>
    /// Records timing and results for each step of a graph build.
    /// Overwrites on each build — keeps only the last rebuild's log.
    /// Saved to .arcforge/graph_build.log alongside graph.db.
    /// </summary>
    public class GraphBuildLog
    {
        readonly StringBuilder _sb = new StringBuilder();
        readonly Stopwatch _total = Stopwatch.StartNew();
        readonly string _filePath;
        Stopwatch _step;
        int _stepNum;

        public GraphBuildLog(string trigger)
        {
            var projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
            _filePath = Path.Combine(projectRoot, ".arcforge", "graph_build.log");

            _sb.AppendLine("=== Hades Graph Build ===");
            _sb.AppendLine($"Date:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _sb.AppendLine($"Trigger: {trigger}");
            _sb.AppendLine($"Cores:   {Environment.ProcessorCount}");
            _sb.AppendLine();
        }

        public void BeginStep(string name)
        {
            _stepNum++;
            _sb.AppendLine($"[Step {_stepNum}] {name}");
            _step = Stopwatch.StartNew();
        }

        public void Detail(string key, object value)
        {
            _sb.AppendLine($"  {key}: {value}");
        }

        public void EndStep()
        {
            if (_step != null)
            {
                _step.Stop();
                _sb.AppendLine($"  Duration: {FormatDuration(_step.ElapsedMilliseconds)}");
                _sb.AppendLine();
            }
        }

        public void Flush(long totalNodes, long totalEdges)
        {
            _total.Stop();
            _sb.AppendLine("=== Summary ===");
            _sb.AppendLine($"Total duration: {FormatDuration(_total.ElapsedMilliseconds)}");
            _sb.AppendLine($"Total nodes:    {totalNodes:N0}");
            _sb.AppendLine($"Total edges:    {totalEdges:N0}");

            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, _sb.ToString());
            }
            catch
            {
                // Silently ignore write failures — log is non-critical
            }
        }

        static string FormatDuration(long ms)
        {
            if (ms < 1000) return $"{ms}ms";
            if (ms < 60000) return $"{ms / 1000.0:F1}s";
            return $"{ms / 60000}m {(ms % 60000) / 1000}s";
        }
    }
}
