using System.IO;
using ArcForge.Hades.Editor.Charon;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph
{
    /// <summary>
    /// Ensures the GraphDatabase singleton exists. Must run before GraphUpdateHandler.
    /// Does NOT create a GraphBuilder — that's owned by GraphUpdateHandler.
    /// </summary>
    [InitializeOnLoad]
    public static class GraphInitializer
    {
        static GraphInitializer()
        {
            EditorApplication.delayCall += Initialize;
        }

        internal static void EnsureDatabase()
        {
            if (GraphDatabase.Instance != null) return;

            using (var span = CharonEmitter.StartSpan("lifecycle.graph_init", SpanKind.Internal))
            {
                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                var dbPath = Path.Combine(projectRoot, ".arcforge", "graph.db");
                new GraphDatabase(dbPath); // sets GraphDatabase.Instance
            }
        }

        static void Initialize()
        {
            EnsureDatabase();
        }
    }
}
