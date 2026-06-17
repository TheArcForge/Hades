using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph.Updates
{
    public class GraphUpdateHandler
    {
        static GraphUpdateHandler _instance;
        public static GraphUpdateHandler Instance => _instance;

        GraphBuilder _builder;
        UpdateDebouncer _debouncer;

        // NOTE: no [InitializeOnLoad] — HadesBootstrap calls InitializeFromBootstrap() (fast,
        // registers event hooks) early, then RunStartupSync() on a deferred tick.
        internal static void InitializeFromBootstrap()
        {
            if (_instance != null) return;

            // Ensure database exists (handles ordering if GraphInitializer hasn't run yet)
            GraphInitializer.EnsureDatabase();
            if (GraphDatabase.Instance == null) return;

            _instance = new GraphUpdateHandler();
            _instance.SetupEvents();
        }

        void SetupEvents()
        {
            _builder = new GraphBuilder(GraphDatabase.Instance);
            // Interactive incrementals run the .cs scan off the main thread (deferCsScan: true) so a
            // script save doesn't freeze the editor. The startup catch-up path keeps the synchronous
            // default. PumpCsScan (below) drives the off-thread scan's continuation.
            _debouncer = new UpdateDebouncer(guids => _builder.UpdateAssets(guids, deferCsScan: true));

            EditorApplication.update += OnEditorUpdate;
            EditorApplication.projectChanged += OnProjectChanged;
            EditorSceneManager.sceneSaved += OnSceneSaved;

            #if UNITY_2021_1_OR_NEWER
            PrefabStage.prefabSaved += OnPrefabSaved;
            #endif
        }

        /// <summary>The blocking startup work (package scan + stale-asset catch-up / firstBoot
        /// rebuild). Deferred by HadesBootstrap to a later tick so the MCP server is fully live
        /// first. GraphBuilder.IsStartupInProgress is set for its duration (busy-gate).</summary>
        internal static void RunStartupSync()
        {
            var h = _instance;
            if (h?._builder == null) return;
            h._builder.CheckStartupSync();
            var db = GraphDatabase.Instance;
            Debug.Log($"[Hades] Graph initialized: {db.GetNodeCount()} nodes, {db.GetEdgeCount()} edges");
        }

        void OnEditorUpdate()
        {
            // Advance any in-flight off-thread .cs scan first. While one is running, hold the
            // debouncer flush so a second save queues (in the debouncer) instead of spawning an
            // overlapping scan; it flushes on the tick the scan completes.
            _builder?.PumpCsScan();
            if (GraphBuilder.IsCsScanInFlight) return;
            _debouncer?.Tick();
        }

        void OnProjectChanged()
        {
        }

        void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            if (string.IsNullOrEmpty(scene.path)) return;
            var guid = AssetDatabase.AssetPathToGUID(scene.path);
            if (!string.IsNullOrEmpty(guid))
                _debouncer.Enqueue(new[] { guid });
        }

        void OnPrefabSaved(GameObject prefabRoot)
        {
            var path = AssetDatabase.GetAssetPath(prefabRoot);
            if (string.IsNullOrEmpty(path)) return;
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (!string.IsNullOrEmpty(guid))
                _debouncer.Enqueue(new[] { guid });
        }

        public bool IsBusy => _builder != null && _builder.GetStatus() != BuildStatus.Idle;

        public void OnAssetsChanged(string[] guids)
        {
            if (IsBusy) return;
            _debouncer?.Enqueue(guids);
        }

        public void OnAssetsDeleted(string[] paths)
        {
            _builder?.HandleDeletedAssets(paths);
        }

        public void OnAssetsMoved(string[] fromPaths, string[] toPaths)
        {
            _builder?.HandleMovedAssets(fromPaths, toPaths);
        }

        public GraphBuilder GetBuilder() => _builder;
    }
}
