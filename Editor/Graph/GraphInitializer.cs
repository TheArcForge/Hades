using System.IO;
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

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var dbPath = Path.Combine(projectRoot, ".arcforge", "graph.db");

            var db = new GraphDatabase(dbPath);
            var builder = new GraphBuilder(db);

            builder.CheckStartupSync();

            Debug.Log($"[Hades] Graph initialized: {db.GetNodeCount()} nodes, {db.GetEdgeCount()} edges");
        }
    }
}
