using System;
using System.Collections.Generic;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.MCP;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>
    /// The single startup composition root. Replaces the per-subsystem [InitializeOnLoad]
    /// race with one ordered boot so the MCP server registers + arms its heartbeat BEFORE
    /// the blocking graph startup sync — the server stays reachable even while a rebuild
    /// pins the main thread. Ordering Charon→Asphodel also fixes #6 (InferenceEngine null).
    /// Teardown remains per-subsystem (each keeps its own beforeAssemblyReload/quitting).
    /// </summary>
    [InitializeOnLoad]
    public static class HadesBootstrap
    {
        /// <summary>Order in which boot steps ran this load — read by the ordering test.</summary>
        public static readonly List<string> BootTrace = new List<string>();

        static HadesBootstrap()
        {
            EditorApplication.delayCall += Boot;
        }

        static void Boot()
        {
            // Keep the main thread un-napped across boot + the deferred sync tick, so a
            // backgrounded editor can't starve the registration or the deferred scan.
            AppNapGuard.Acquire();
            try
            {
                BootTrace.Clear();
                // FIRST: Charon (next step) constructs HadesSettings, so the project-local
                // settings file must exist and any EditorPrefs import must be done before it.
                Step("Settings",       () => HadesSettings.EnsureMigrated());
                Step("Charon",         () => CharonInitializer.Initialize());
                Step("GraphDb",        () => Graph.GraphInitializer.EnsureDatabase());
                Step("Asphodel",       () => Asphodel.AsphodeInitializer.Initialize());
                Step("MCPServer",      () => MCPServer.StartFromBootstrap());
                Step("GraphEvents",    () => Graph.Updates.GraphUpdateHandler.InitializeFromBootstrap());
                Step("PackageWatcher", () => Graph.Updates.PackageChangeDetector.Initialize());

                // Defer the blocking startup sync to a later tick so the server is fully
                // live (first ProcessMainThreadQueue tick) before the scan blocks.
                BootTrace.Add("StartupSyncScheduled");
                EditorApplication.delayCall += RunStartupSyncOnce;
            }
            finally
            {
                AppNapGuard.Release();
            }
        }

        static void RunStartupSyncOnce()
        {
            AppNapGuard.Acquire();
            try { Graph.Updates.GraphUpdateHandler.RunStartupSync(); }
            finally { AppNapGuard.Release(); }

            // Deferred deliberately: this can show a modal dialog, and the boot path's priority
            // is getting the MCP server reachable first.
            Step("LegacyHubNotice", () => LegacyHubNotice.MaybeShow());
        }

        // Each subsystem init is isolated: a failure in one is logged and never prevents
        // the MCP server (or later steps) from starting — reachability is the priority.
        static void Step(string name, Action init)
        {
            BootTrace.Add(name);
            try { init(); }
            catch (Exception ex) { Debug.LogError($"[Hades] Bootstrap step '{name}' failed: {ex}"); }
        }
    }
}
