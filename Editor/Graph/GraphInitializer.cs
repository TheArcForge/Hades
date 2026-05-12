using System.IO;
using ArcForge.Hades.Editor.Charon;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph
{
    [InitializeOnLoad]
    public static class GraphInitializer
    {
        static GraphInitializer()
        {
            EditorApplication.delayCall += Initialize;
        }

        static void Initialize()
        {
            if (GraphDatabase.Instance != null) return;

            using (var span = CharonEmitter.StartSpan("lifecycle.graph_init", SpanKind.Internal))
            {
                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                var dbPath = Path.Combine(projectRoot, ".arcforge", "graph.db");

                var db = new GraphDatabase(dbPath);
                var builder = new GraphBuilder(db);

                builder.CheckStartupSync();

                span.SetAttribute("nodes.count", db.GetNodeCount());
                span.SetAttribute("edges.count", db.GetEdgeCount());

                Debug.Log($"[Hades] Graph initialized: {db.GetNodeCount()} nodes, {db.GetEdgeCount()} edges");
            }
        }
    }
}
