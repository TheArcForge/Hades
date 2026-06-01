// Editor/Charon/CharonInitializer.cs
using System.IO;
using ArcForge.Hades.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Charon
{
    [InitializeOnLoad]
    public static class CharonInitializer
    {
        static CharonDatabase _database;

        static CharonInitializer()
        {
            EditorApplication.delayCall += Initialize;
        }

        static void Initialize()
        {
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

                // Backstop: enforce a hard size cap even within the retention window.
                var capBytes = (long)settings.CharonMaxSizeMb * 1024 * 1024;
                var trimmed = _database.EnforceSizeLimit(dbPath, capBytes);
                if (trimmed > 0)
                {
                    span.SetAttribute("traces.size_trimmed", (long)trimmed);
                    Debug.Log($"[Hades Charon] Trimmed {trimmed} oldest traces to keep traces.db under {settings.CharonMaxSizeMb} MB");
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
