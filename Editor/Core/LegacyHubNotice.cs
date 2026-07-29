using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>
    /// Tells the user once that ~/.arcforge/hades-hub/ is no longer used by this project.
    ///
    /// Nothing is moved or deleted, deliberately. launcher.js and hub-path.json are regenerated
    /// on every startup, and hub.json / hub.lock / pending/ are the live runtime state of a hub
    /// process that may be serving a different project — moving them would break its discovery.
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
            bool alreadyShown, bool globalDirExists)
        {
            if (alreadyShown) return false;
            if (!globalDirExists) return false;
            if (string.IsNullOrEmpty(resolvedHubDir) || string.IsNullOrEmpty(globalHubDir))
                return false;

            // Still using the global dir — there is nothing to tell them.
            return !string.Equals(Normalize(resolvedHubDir), Normalize(globalHubDir),
                PathComparison);
        }

        static string Normalize(string path)
            => path.Replace('\\', '/').TrimEnd('/');

        public static void MaybeShow()
        {
            if (Application.isBatchMode) return;

            try
            {
                var globalHubDir = HadesPaths.GlobalHubDirForMachine;

                if (!ShouldShow(HadesPaths.HubDir, globalHubDir,
                        EditorPrefs.GetBool(ShownKey, false),
                        Directory.Exists(globalHubDir)))
                    return;

                var ok = EditorUtility.DisplayDialog(
                    "Hades — Hub Moved Into This Project",
                    "Hades now keeps its hub inside this project:\n\n" +
                    "    .arcforge/hades-hub/\n\n" +
                    "The old shared folder is no longer used by this project:\n\n" +
                    $"    {globalHubDir}\n\n" +
                    "Other Unity projects may still be using it. It is safe to delete only " +
                    "once every project has been updated.",
                    "OK", "Open Folder");

                // Recorded either way — the notice is informational and must fire only once.
                EditorPrefs.SetBool(ShownKey, true);

                if (!ok) EditorUtility.RevealInFinder(globalHubDir);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Legacy hub notice failed: {ex.Message}");
            }
        }
    }
}
