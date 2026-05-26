// Editor/Graph/Updates/PackageChangeDetector.cs
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Graph.Updates
{
    /// <summary>
    /// Watches for changes to Packages/manifest.json and packages-lock.json.
    /// Triggers a package rescan when dependencies change.
    /// </summary>
    [InitializeOnLoad]
    public static class PackageChangeDetector
    {
        static FileSystemWatcher _watcher;
        static bool _packageChangeDetected;

        static PackageChangeDetector()
        {
            EditorApplication.delayCall += Initialize;
        }

        static void Initialize()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var packagesDir = Path.Combine(projectRoot, "Packages");

            if (!Directory.Exists(packagesDir)) return;

            _watcher = new FileSystemWatcher(packagesDir)
            {
                Filter = "*.json",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnPackageFileChanged;
            EditorApplication.update += PollForChanges;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
        }

        static void OnPackageFileChanged(object sender, FileSystemEventArgs e)
        {
            var fileName = Path.GetFileName(e.FullPath);
            if (fileName == "manifest.json" || fileName == "packages-lock.json")
            {
                _packageChangeDetected = true;
            }
        }

        static void PollForChanges()
        {
            if (!_packageChangeDetected) return;
            _packageChangeDetected = false;

            // Debounce: wait until after Unity finishes its own package resolution
            EditorApplication.delayCall += TriggerPackageRescan;
        }

        static void TriggerPackageRescan()
        {
            var handler = GraphUpdateHandler.Instance;
            if (handler == null) return;

            var builder = handler.GetBuilder();
            if (builder == null) return;

            if (builder.GetStatus() != BuildStatus.Idle) return;

            Debug.Log("[Hades] Package manifest changed — checking if rescan needed");
            builder.ScanPackagesIfNeeded();
        }

        static void OnBeforeReload()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }
}
