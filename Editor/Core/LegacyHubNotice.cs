using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>
    /// Tells the user once that ~/.arcforge/hades-hub/ is no longer used by this project.
    ///
    /// Nothing is moved or deleted, deliberately. hub-path.json is regenerated on every startup,
    /// launcher.js is no longer written here at all (HadesPaths.LauncherDir is always project-local),
    /// and hub.json / hub.lock / pending/ are the live runtime state of a hub process that may be
    /// serving a different project — moving them would break its discovery.
    /// Hades also cannot tell whether another project on this machine still depends on the folder,
    /// so deleting it is not Hades' call.
    /// </summary>
    public static class LegacyHubNotice
    {
        // Machine-global on purpose: the fact recorded is machine-global. Stored per project,
        // this notice would fire once per project for one shared folder.
        internal const string ShownKey = "Hades_LegacyHubNoticeShown";

        static readonly StringComparison PathComparison =
            Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        /// <summary>Pure decision function — no dialogs, no filesystem, no prefs.</summary>
        internal static bool ShouldShow(string resolvedHubDir, string globalHubDir,
            bool alreadyShown, bool globalDirExists, bool desktopEntryStale)
        {
            if (alreadyShown) return false;
            if (string.IsNullOrEmpty(resolvedHubDir) || string.IsNullOrEmpty(globalHubDir))
                return false;

            return HubMoved(resolvedHubDir, globalHubDir, globalDirExists) || desktopEntryStale;
        }

        // Still using the global dir — there is nothing to tell them about the hub.
        static bool HubMoved(string resolvedHubDir, string globalHubDir, bool globalDirExists)
            => globalDirExists && !string.Equals(Normalize(resolvedHubDir), Normalize(globalHubDir),
                PathComparison);

        /// <summary>
        /// True when Claude Desktop's config still names the launcher at its pre-migration home
        /// (&lt;globalHubDir&gt;/launcher.js). launcher.js is no longer written there in either hub
        /// scope, so an entry pointing there is permanently stale — Desktop will fail to start
        /// Hades and nothing will ever rewrite the entry, since desktop_integration now defaults off.
        /// </summary>
        internal static bool DesktopEntryIsStale(string desktopLauncherArg, string globalHubDir)
        {
            if (string.IsNullOrEmpty(desktopLauncherArg) || string.IsNullOrEmpty(globalHubDir))
                return false;

            var staleArg = Path.Combine(globalHubDir, "launcher.js");
            return string.Equals(Normalize(desktopLauncherArg), Normalize(staleArg), PathComparison);
        }

        static string Normalize(string path)
            => path.Replace('\\', '/').TrimEnd('/');

        public static void MaybeShow()
        {
            if (Application.isBatchMode) return;

            try
            {
                var globalHubDir = HadesPaths.GlobalHubDirForMachine;
                var globalDirExists = Directory.Exists(globalHubDir);
                var hubMoved = HubMoved(HadesPaths.HubDir, globalHubDir, globalDirExists);
                var desktopStale = DesktopEntryIsStale(
                    MCPClientConfig.ReadDesktopHadesLauncherArg(), globalHubDir);

                if (!ShouldShow(HadesPaths.HubDir, globalHubDir,
                        EditorPrefs.GetBool(ShownKey, false), globalDirExists, desktopStale))
                    return;

                var message = "Hades now keeps its hub inside this project:\n\n" +
                    "    .arcforge/hades-hub/\n\n";

                if (hubMoved)
                    message += "The old shared folder is no longer used by this project:\n\n" +
                        $"    {globalHubDir}\n\n" +
                        "Other Unity projects may still be using it. It is safe to delete only " +
                        "once every project has been updated.\n\n";

                if (desktopStale)
                    message += "Claude Desktop's config still points at the old launcher location " +
                        "and will fail to start Hades. Re-enable Desktop integration in " +
                        "Project Settings > Hades to rewrite it to the new location.";

                var ok = EditorUtility.DisplayDialog(
                    "Hades — Hub Moved Into This Project", message.TrimEnd('\n'), "OK", "Open Folder");

                // Recorded either way — the notice is informational and must fire only once.
                EditorPrefs.SetBool(ShownKey, true);

                if (!ok && globalDirExists) EditorUtility.RevealInFinder(globalHubDir);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Legacy hub notice failed: {ex.Message}");
            }
        }
    }
}
