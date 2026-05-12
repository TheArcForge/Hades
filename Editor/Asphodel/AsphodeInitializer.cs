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
            EditorApplication.delayCall += () => _validator.ValidateFile(filename);
        }

        public static void OnGraphRebuildComplete()
        {
            if (_validator == null) return;
            EditorApplication.delayCall += () => _validator.ValidateAll();
        }

        static void OnBeforeReload()
        {
            _watcher?.Dispose();
        }

        static void OnQuit()
        {
            _watcher?.Dispose();
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            EditorApplication.quitting -= OnQuit;
        }
    }
}
