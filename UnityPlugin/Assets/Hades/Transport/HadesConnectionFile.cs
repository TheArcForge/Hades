// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.IO;
using System.Runtime.InteropServices;
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
    /// On macOS this deliberately does NOT mirror the app's own path computation
    /// (<see cref="Environment.SpecialFolder.ApplicationData"/>): verified empirically that
    /// Unity's Mono resolves that to <c>~/.config</c> while the app's .NET runtime resolves it to
    /// <c>~/Library/Application Support</c>, on the same machine, for the same enum value - a
    /// cross-runtime XDG-vs-native split, not a bug in either runtime. So the macOS arm anchors on
    /// <see cref="Environment.SpecialFolder.UserProfile"/> - which both runtimes agree resolves to
    /// the user's home directory - plus a hard-coded macOS-native suffix, rather than an API whose
    /// meaning differs by runtime.
    ///
    /// <b>WINDOWS IS THE OPPOSITE CASE, AND IT WAS MEASURED RATHER THAN ASSUMED.</b> Probed
    /// 2026-08-29 on Windows 11 build 26200, Unity 6000.3.2f1 (Mono, Environment.Version
    /// 4.0.30319.42000) against .NET 10.0.11:
    ///
    /// <code>
    ///                          Unity Mono                      .NET 10
    ///   ApplicationData        C:\Users\u\AppData\Roaming      (roaming, unused here)
    ///   LocalApplicationData   C:\Users\u\AppData\Local        C:\Users\u\AppData\Local
    ///   UserProfile            C:\Users\u                      C:\Users\u
    /// </code>
    ///
    /// The two runtimes AGREE here, unlike on macOS, so the Windows arm uses
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> directly rather than
    /// hard-coding <c>UserProfile\AppData\Local</c>. That is a deliberate difference from the macOS
    /// arm, not an inconsistency: hard-coding is a workaround for runtimes that disagree, and where
    /// they agree the API is strictly better - Windows folder redirection (roaming profiles, managed
    /// desktops) moves AppData, and the API follows it while a hard-coded path silently would not.
    ///
    /// Local, not Roaming, matching <c>AppPaths.DefaultRoot</c> on the app side: everything under
    /// the root is either derived or names a port meaningless on another machine, so roaming it
    /// would sync a large cache that is wrong at the far end.
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
        /// On macOS, deliberately NOT SpecialFolder.ApplicationData: measured there, .NET resolves
        /// it to ~/Library/Application Support while Unity's Mono resolves the same enum to
        /// ~/.config. UserProfile agrees across both runtimes; the rest is hard-coded to what the
        /// app's runtime actually produces. On Windows the two runtimes agree, so the enum is used
        /// directly - see this type's own doc comment for the measurement and why the two arms
        /// differ on purpose.</summary>
        public static string DefaultPath
        {
            get
            {
                var overrideRoot = Environment.GetEnvironmentVariable("HADES_HOME");
                if (!string.IsNullOrEmpty(overrideRoot))
                {
                    return Path.Combine(overrideRoot, "editor.token");
                }

                if (IsWindows())
                {
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    return string.IsNullOrEmpty(localAppData)
                        ? null
                        : Path.Combine(localAppData, "Hades", "editor.token");
                }

                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(home)
                    ? null
                    : Path.Combine(home, "Library", "Application Support", "Hades", "editor.token");
            }
        }

        /// <summary>
        /// Whether this Editor is running on Windows.
        ///
        /// <c>RuntimeInformation.IsOSPlatform</c> rather than <c>Environment.OSVersion.Platform</c>:
        /// both were probed under Unity 6000.3.2f1's Mono on 2026-08-29 and both answered correctly
        /// (<c>Win32NT</c> and <c>True</c> respectively), so this is a choice between two working
        /// options rather than a workaround. The RuntimeInformation form wins because
        /// <c>PlatformID</c> is the API that historically lied - Mono reports <c>Unix</c> for macOS,
        /// which is exactly the kind of answer this file exists to stop trusting.
        ///
        /// Deliberately not UnityEngine.Application.platform: this file is Transport code and must
        /// not depend on UnityEngine.
        /// </summary>
        private static bool IsWindows()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
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
