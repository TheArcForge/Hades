// Editor/Asphodel/MemoryFileWatcher.cs
using System;
using System.IO;
using System.Threading;
using ArcForge.Hades.Editor.Charon;
using UnityEngine;

namespace ArcForge.Hades.Editor.Asphodel
{
    public class MemoryFileWatcher : IDisposable
    {
        readonly string _watchDir;
        readonly Action<string> _onFileChanged;
        FileSystemWatcher _watcher;
        Timer _debounceTimer;
        string _pendingFile;
        readonly object _lock = new object();
        const int DebounceMs = 500;

        public MemoryFileWatcher(string watchDir, Action<string> onFileChanged)
        {
            _watchDir = watchDir;
            _onFileChanged = onFileChanged;
        }

        public void Start()
        {
            if (!Directory.Exists(_watchDir)) return;

            _watcher = new FileSystemWatcher(_watchDir, "*.md");
            _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnDeleted;
            _watcher.EnableRaisingEvents = true;
        }

        void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath.Contains(Path.DirectorySeparatorChar + "proposals" + Path.DirectorySeparatorChar))
                return;

            lock (_lock)
            {
                _pendingFile = Path.GetFileNameWithoutExtension(e.Name);
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(FireDebounced, null, DebounceMs, Timeout.Infinite);
            }
        }

        void OnDeleted(object sender, FileSystemEventArgs e)
        {
            Debug.LogWarning($"[Hades Asphodel] Memory file deleted: {e.Name}");
        }

        void FireDebounced(object state)
        {
            string file;
            lock (_lock)
            {
                file = _pendingFile;
                _pendingFile = null;
            }

            if (file == null) return;

            using (var span = CharonEmitter.StartSpan("memory.write.tier1.direct", SpanKind.Internal))
            {
                span.SetAttribute("file_path", file + ".md");
            }

            _onFileChanged?.Invoke(file);
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _debounceTimer?.Dispose();
        }
    }
}
