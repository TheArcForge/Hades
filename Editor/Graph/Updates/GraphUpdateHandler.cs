using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph.Updates
{
    [InitializeOnLoad]
    public class GraphUpdateHandler
    {
        static GraphUpdateHandler _instance;
        public static GraphUpdateHandler Instance => _instance;

        GraphBuilder _builder;
        UpdateDebouncer _debouncer;

        static GraphUpdateHandler()
        {
            EditorApplication.delayCall += Initialize;
        }

        static void Initialize()
        {
            if (_instance != null) return;

            // Ensure database exists (handles ordering if GraphInitializer hasn't run yet)
            GraphInitializer.EnsureDatabase();
            if (GraphDatabase.Instance == null) return;

            _instance = new GraphUpdateHandler();
            _instance.Setup();
        }

        void Setup()
        {
            _builder = new GraphBuilder(GraphDatabase.Instance);
            _debouncer = new UpdateDebouncer(guids => _builder.UpdateAssets(guids));

            EditorApplication.update += OnEditorUpdate;
            EditorApplication.projectChanged += OnProjectChanged;
            EditorSceneManager.sceneSaved += OnSceneSaved;

            #if UNITY_2021_1_OR_NEWER
            PrefabStage.prefabSaved += OnPrefabSaved;
            #endif

            // Run startup sync (package scan + stale asset check) with progress bar
            _builder.CheckStartupSync();

            var db = GraphDatabase.Instance;
            Debug.Log($"[Hades] Graph initialized: {db.GetNodeCount()} nodes, {db.GetEdgeCount()} edges");
        }

        void OnEditorUpdate()
        {
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
