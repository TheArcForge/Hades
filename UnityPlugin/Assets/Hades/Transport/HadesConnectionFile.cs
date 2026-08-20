// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.IO;
using Hades.Contract.Wire;

namespace Hades.Transport
{
    /// <summary>
    /// Locates and reads the connection file the app writes on every listener <c>Start()</c> -
    /// see <c>Hades.Core.Editors.EditorListener</c> and <c>AppPaths.EditorTokenFile</c> on the
    /// app side. It carries the port to dial together with the token to present, bundled into
    /// one file because the app binds an OS-assigned ephemeral port, so nothing else tells the
    /// plugin which one to use.
    ///
    /// Deliberately does NOT mirror the app's own path computation
    /// (<see cref="Environment.SpecialFolder.ApplicationData"/>): verified empirically that
    /// Unity's Mono resolves that to <c>~/.config</c> while the app's .NET runtime resolves it to
    /// <c>~/Library/Application Support</c>, on the same machine, for the same enum value - a
    /// cross-runtime XDG-vs-native split, not a bug in either runtime. Hades targets macOS (see
    /// EditorListener's class doc comment), so this anchors on <see cref="Environment.SpecialFolder.UserProfile"/> -
    /// which both runtimes agree resolves to the user's home directory - plus a hard-coded
    /// macOS-native suffix, rather than an API whose meaning differs by runtime.
    /// </summary>
    public static class HadesConnectionFile
    {
        /// <summary>Where the app writes port + token: the macOS-native Application Support
        /// directory, matching what AppPaths.Root + AppPaths.EditorTokenFile resolve to under the
        /// app's .NET runtime on this platform. Null if the user's home directory cannot be
        /// determined.
        ///
        /// HADES_HOME overrides it, mirroring the app's own override of the same name. This has to
        /// be symmetric: pointing only the app elsewhere would leave the plugin dialling the
        /// default path and never finding the isolated app, which looks exactly like a plugin that
        /// does not work. Two app instances on one machine, or an isolated end-to-end run, need
        /// both halves to agree on the root.
        ///
        /// Deliberately NOT SpecialFolder.ApplicationData: measured on this platform, .NET 10
        /// resolves that to ~/Library/Application Support while Unity's Mono resolves the same
        /// enum to ~/.config. UserProfile agrees across both runtimes; the rest is hard-coded to
        /// what the app's runtime actually produces on macOS.</summary>
        public static string DefaultPath
        {
            get
            {
                var overrideRoot = Environment.GetEnvironmentVariable("HADES_HOME");
                if (!string.IsNullOrEmpty(overrideRoot))
                {
                    return Path.Combine(overrideRoot, "editor.token");
                }

                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(home)
                    ? null
                    : Path.Combine(home, "Library", "Application Support", "Hades", "editor.token");
            }
        }

        /// <summary>
        /// Reads and parses the connection file at <paramref name="path"/>. Returns null on any
        /// failure - missing file, unreadable, or malformed content (including a file caught
        /// mid-write by the app) - and never throws: this reads a file written by another
        /// process, on its own schedule, so "not ready yet" is an expected outcome to be retried,
        /// not a fault to surface.
        /// </summary>
        public static EditorConnectionInfo TryRead(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string text;
            try
            {
                if (!File.Exists(path)) return null;
                text = File.ReadAllText(path);
            }
            catch
            {
                return null;
            }

            return EditorConnectionInfo.TryParse(text, out var info, out _) ? info : null;
        }
    }
}
