using ArcForge.Hades.Editor.Graph.Updates;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph
{
    public static class GraphMenuItems
    {
        [MenuItem("Hades/Rebuild Graph", priority = 100)]
        public static void RebuildGraph()
        {
            var handler = GraphUpdateHandler.Instance;
            if (handler == null)
            {
                Debug.LogError("[Hades] Graph update handler not initialized");
                return;
            }

            var builder = handler.GetBuilder();
            if (builder == null)
            {
                Debug.LogError("[Hades] Graph builder not available");
                return;
            }

            builder.RebuildAll();
            Debug.Log($"[Hades] Graph rebuild complete: {GraphDatabase.Instance.GetNodeCount()} nodes, {GraphDatabase.Instance.GetEdgeCount()} edges");
        }

        [MenuItem("Hades/Graph Status", priority = 101)]
        public static void ShowGraphStatus()
        {
            var db = GraphDatabase.Instance;
            if (db == null)
            {
                Debug.Log("[Hades] Graph database not initialized");
                return;
            }

            var counts = db.GetTypeCounts();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[Hades] Graph Status:");
            sb.AppendLine($"  Total nodes: {db.GetNodeCount()}");
            sb.AppendLine($"  Total edges: {db.GetEdgeCount()}");
            sb.AppendLine($"  Rebuilding: {db.IsRebuildInProgress()}");
            sb.AppendLine($"  Last rebuild: {db.GetMetadata("last_full_rebuild_at") ?? "never"}");
            sb.AppendLine("  Node counts by type:");
            foreach (var kv in counts)
                sb.AppendLine($"    {kv.Key}: {kv.Value}");
            Debug.Log(sb.ToString());
        }
    }
}
