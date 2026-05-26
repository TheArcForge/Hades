// Editor/Asphodel/AsphodeInitializer.cs
using System.IO;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.Graph;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Asphodel
{
    [InitializeOnLoad]
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

        static AsphodeInitializer()
        {
            EditorApplication.delayCall += Initialize;
        }

        static void Initialize()
        {
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

        static void OnGraphRebuild()
        {
            UnityEditor.EditorApplication.delayCall += () =>
            {
                _watcher?.Suppress();
                Validator?.ValidateAll();
                InferenceEngine?.RunInference();
                _watcher?.Resume();
            };
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
