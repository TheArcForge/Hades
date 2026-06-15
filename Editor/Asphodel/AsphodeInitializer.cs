// Editor/Asphodel/AsphodeInitializer.cs
using System.IO;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.Graph;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Asphodel
{
    // NOTE: no [InitializeOnLoad] — HadesBootstrap calls Initialize() AFTER Charon, so
    // CharonEmitter.Database is non-null here and InferenceEngine is created (fixes #6).
    public static class AsphodeInitializer
    {
        static MemoryManager _manager;
        static MemoryValidator _validator;
        static MemoryFileWatcher _watcher;

        public static MemoryManager Manager => _manager;
        public static MemoryValidator Validator => _validator;
        public static Inference.PatternInferenceEngine InferenceEngine { get; private set; }

        /// <summary>
        /// Lazy-init the validator if it wasn't created at startup (e.g. GraphDatabase wasn't ready).
        /// </summary>
        public static void InitValidator(MemoryManager manager, GraphDatabase db)
        {
            if (_validator != null) return;
            _validator = new MemoryValidator(manager, db);
        }

        internal static void Initialize()
        {
            if (_manager != null) return; // idempotent
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var memoryDir = Path.Combine(projectRoot, ".arcforge", "memory");
            _manager = new MemoryManager(memoryDir);

            using (var span = CharonEmitter.StartSpan("lifecycle.asphodel_init", SpanKind.Internal))
            {
                _manager.EnsureDirectory();
                _manager.EnsureDefaults();
                _manager.PruneExpiredProposals();

                var db = GraphDatabase.Instance;
                if (db != null)
                {
                    _validator = new MemoryValidator(_manager, db);
                    EditorApplication.delayCall += RunStartupValidation;
                }

                var arcforgeDir = Path.Combine(projectRoot, ".arcforge");
                var inferenceConfig = Inference.InferenceConfig.LoadFromDirectory(arcforgeDir);
                var charonDb = Charon.CharonEmitter.Database;
                if (charonDb != null)
                {
                    InferenceEngine = new Inference.PatternInferenceEngine(Manager, charonDb, inferenceConfig);
                }

                GraphBuilder.OnRebuildComplete -= OnGraphRebuild;
                GraphBuilder.OnRebuildComplete += OnGraphRebuild;

                _watcher = new MemoryFileWatcher(memoryDir, OnMemoryFileChanged);
                _watcher.Start();

                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
                EditorApplication.quitting += OnQuit;

                var files = _manager.ListFiles();
                span.SetAttribute("memory.file_count", (long)files.Count);
                Debug.Log($"[Hades Asphodel] Memory initialized: {files.Count} files in .arcforge/memory/");
            }
        }

        static void RunStartupValidation()
        {
            if (_validator == null) return;
            _validator.ValidateAll();
        }

        static void OnMemoryFileChanged(string filename)
        {
            if (_validator == null) return;
            EditorApplication.delayCall += () =>
            {
                _watcher?.Suppress();
                _validator.ValidateFile(filename);
                _watcher?.Resume();
            };
        }

        public static void OnGraphRebuildComplete()
        {
            OnGraphRebuild();
        }

        // Inference is throttled so it does NOT run on every rebuild — it loads traces and runs
        // analyzers, a heavy post-rebuild main-thread cost. The design intent is periodic.
        const double InferenceThrottleMinutes = 30.0;

        static void OnGraphRebuild()
        {
            UnityEditor.EditorApplication.delayCall += () =>
            {
                bool barShown = false;
                try
                {
                    _watcher?.Suppress();

                    // Post-rebuild work runs on the main thread AFTER the rebuild's own progress
                    // bar has cleared. Show our own bar so it is never a silent freeze.
                    barShown = true;
                    UnityEditor.EditorUtility.DisplayProgressBar("Hades", "Validating memory…", 0.3f);
                    Validator?.ValidateAll();

                    if (ShouldRunInference())
                    {
                        UnityEditor.EditorUtility.DisplayProgressBar("Hades", "Analyzing patterns…", 0.7f);
                        InferenceEngine?.RunInference();
                        UnityEditor.SessionState.SetString("Hades_LastInferenceTicks",
                            System.DateTime.UtcNow.Ticks.ToString());
                    }
                }
                catch (System.Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[Hades Asphodel] Post-rebuild processing failed: {ex}");
                }
                finally
                {
                    if (barShown) UnityEditor.EditorUtility.ClearProgressBar();
                    _watcher?.Resume();
                }
            };
        }

        static bool ShouldRunInference()
        {
            var last = UnityEditor.SessionState.GetString("Hades_LastInferenceTicks", "");
            if (long.TryParse(last, out var ticks))
            {
                var elapsed = System.DateTime.UtcNow - new System.DateTime(ticks, System.DateTimeKind.Utc);
                if (elapsed.TotalMinutes < InferenceThrottleMinutes) return false;
            }
            return true;
        }

        static void OnBeforeReload()
        {
            _watcher?.Dispose();
        }

        static void OnQuit()
        {
            _watcher?.Dispose();
            GraphBuilder.OnRebuildComplete -= OnGraphRebuild;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            EditorApplication.quitting -= OnQuit;
        }
    }
}
