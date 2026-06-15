// Editor/Charon/CharonInitializer.cs
using System.IO;
using ArcForge.Hades.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Charon
{
    // NOTE: no [InitializeOnLoad] — HadesBootstrap calls Initialize() first in the boot order
    // so CharonEmitter.Database is set before Asphodel reads it (fixes #6).
    public static class CharonInitializer
    {
        static CharonDatabase _database;

        internal static void Initialize()
        {
            if (_database != null) return; // idempotent
            var settings = new HadesSettings();
            if (!settings.CharonEnabled) return;

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var dbPath = Path.Combine(projectRoot, ".arcforge", "traces.db");

            _database = new CharonDatabase(dbPath);
            CharonEmitter.Initialize(_database);

            using (var span = CharonEmitter.StartSpan("lifecycle.startup", SpanKind.Internal))
            {
                var pruned = _database.PruneOlderThan(settings.CharonRetentionDays);
                if (pruned > 0)
                {
                    span.SetAttribute("traces.pruned", (long)pruned);
                    Debug.Log($"[Hades Charon] Pruned {pruned} traces older than {settings.CharonRetentionDays} days");
                }
                span.SetAttribute("retention.days", (long)settings.CharonRetentionDays);

                // Backstop: cap the trace COUNT (not on-disk size) so startup never blocks on a
                // synchronous VACUUM of a multi-GB traces.db (the felt-performance startup freeze).
                // ~4 KB/trace estimate against the size budget, floored at 5000 traces. Freed pages
                // are reused by later inserts, so the file plateaus; the B1 micro-span removal keeps
                // growth slow enough that a count cap holds size flat in practice.
                var maxTraces = System.Math.Max(5000, settings.CharonMaxSizeMb * 256);
                var trimmed = _database.PruneToTraceCap(maxTraces);
                if (trimmed > 0)
                {
                    span.SetAttribute("traces.size_trimmed", (long)trimmed);
                    Debug.Log($"[Hades Charon] Pruned {trimmed} oldest traces to cap traces.db at ~{settings.CharonMaxSizeMb} MB (~{maxTraces} traces)");
                }
            }
            CharonEmitter.Flush();

            EditorApplication.update += CharonEmitter.TickFlush;
            EditorApplication.quitting += Shutdown;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;

            Debug.Log("[Hades Charon] Trace collection initialized");
        }

        static void OnBeforeReload()
        {
            using (var span = CharonEmitter.StartSpan("lifecycle.domain_reload", SpanKind.Internal))
            {
                span.AddEvent("assembly_reload_starting");
            }
            CharonEmitter.Flush();
        }

        static void Shutdown()
        {
            EditorApplication.update -= CharonEmitter.TickFlush;
            EditorApplication.quitting -= Shutdown;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;

            CharonEmitter.Shutdown();
            _database?.Dispose();
            _database = null;
        }
    }
}
