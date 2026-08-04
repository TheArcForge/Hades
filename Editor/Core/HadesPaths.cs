using System;
using System.IO;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>Where Hades keeps a given kind of state: inside the project, or under $HOME.</summary>
    public enum HadesScope
    {
        Local = 0,
        Global = 1
    }

    /// <summary>
    /// Resolves the hub rendezvous directory.
    ///
    /// The hub needs a location that Unity, the launcher, and the hub itself can each compute with
    /// zero configuration, because that is where hub.json (port + pid) is published. $HOME was the
    /// original choice for exactly that reason. The Unity project root satisfies the same property
    /// — Unity knows it directly, and the launcher finds it by walking up for
    /// ProjectSettings/ProjectVersion.txt — so it works equally well while staying in the workspace.
    ///
    /// Chain (spec §4.1):
    ///   1. HADES_HUB_DIR env var          — explicit override, and the seam that makes this testable
    ///   2. &lt;projectRoot&gt;/.arcforge/hades-hub — the default
    ///   3. $HOME/.arcforge/hades-hub      — legacy, and the fallback when projectRoot is unknown
    ///
    /// Rung 3 is not vestigial: a launcher whose cwd is a `file:`-referenced package repo OUTSIDE
    /// the Unity project cannot see the project's hub dir, and must fall through to the shared hub
    /// so Registry.findByProjectPath's manifestPackages match still routes it.
    /// </summary>
    public static class HadesPaths
    {
        public const string EnvHubDir = "HADES_HUB_DIR";
        public const string ArcforgeDirName = ".arcforge";
        public const string HubDirName = "hades-hub";

        /// <summary>Pure resolver — no environment reads, no filesystem probing. See class docs.</summary>
        public static string ResolveHubDir(string envOverride, HadesScope scope,
            string projectRoot, string homeDir)
        {
            if (!string.IsNullOrEmpty(envOverride))
            {
                var trimmed = envOverride.Trim();
                if (trimmed.Length > 0) return trimmed;
            }

            if (scope == HadesScope.Local && !string.IsNullOrEmpty(projectRoot))
                return Path.Combine(projectRoot, ArcforgeDirName, HubDirName);

            return GlobalHubDir(homeDir);
        }

        public static string GlobalHubDir(string homeDir)
            => Path.Combine(homeDir ?? "", ArcforgeDirName, HubDirName);

        // Cached on the main thread by Prime(). MCPServer's heartbeat runs on a
        // System.Threading.Timer and documents the invariant that "the timer only touches pure
        // I/O" — it reaches HubDir via HubClient.DetectHubChange. Resolving live there would call
        // PathSandbox.ProjectRoot -> Application.dataPath, which Unity permits only on the main
        // thread, and would also re-read config.local.yaml on every heartbeat. So resolve once and
        // let background threads read a plain field.
        static string _cachedProjectRoot;
        static string _cachedHubDir;

        static string ProjectRootCached => _cachedProjectRoot ?? PathSandbox.ProjectRoot;

        public static string ArcforgeDir
            => Path.Combine(ProjectRootCached, ArcforgeDirName);

        /// <summary>
        /// Where the version-stable launcher copy is installed — always
        /// &lt;projectRoot&gt;/.arcforge/hades-hub, independent of hub scope and of HADES_HUB_DIR.
        ///
        /// Deliberately NOT HubDir, though it shares the path under the default local scope. The two
        /// answer different questions. HubDir is the rendezvous directory where hub.json is
        /// published, and Unity, the launcher, and the hub must each resolve it to the same place at
        /// runtime. This is merely a stable destination for a copied file, needed only because the
        /// launcher's real package location moves with every version bump
        /// (Library/PackageCache/com.arcforge.hades@&lt;hash&gt;).
        ///
        /// Fusing the two made .mcp.json's args[0] track hub scope, which wrote one developer's
        /// $HOME into a file the whole team shares — for no gain, because the launcher never derives
        /// its hub from its own location. It resolves the hub at startup from HADES_HUB_DIR, a cwd
        /// walk-up, and hub_scope in config.local.yaml (see resolveHubDir in
        /// Bridge~/launcher/src/hub-dir.ts). Keeping the copy project-local therefore leaves global
        /// hub scope working exactly as before, while making args[0] the same on every machine.
        /// </summary>
        public static string LauncherDir
            => Path.Combine(ProjectRootCached, ArcforgeDirName, HubDirName);

        public static string HomeDir
            => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static string GlobalHubDirForMachine => GlobalHubDir(HomeDir);

        /// <summary>
        /// Resolves and caches the project root and hub dir. MUST be called from the main thread,
        /// early in boot, before anything registers with the hub. Re-callable; also the cache
        /// invalidation path when the hub scope setting changes.
        /// </summary>
        public static void Prime()
        {
            _cachedProjectRoot = PathSandbox.ProjectRoot;
            _cachedHubDir = ResolveHubDir(
                Environment.GetEnvironmentVariable(EnvHubDir),
                new HadesSettings().HubScope,
                _cachedProjectRoot,
                HomeDir);
        }

        /// <summary>Call after changing the hub scope so the next read reflects it.</summary>
        public static void InvalidateHubDir() => Prime();

        /// <summary>
        /// The resolved hub directory. Safe to read from any thread once Prime() has run on the
        /// main thread; falls back to a live resolve only if boot has not primed it yet.
        /// </summary>
        public static string HubDir
        {
            get
            {
                var cached = _cachedHubDir;
                if (cached != null) return cached;

                Prime();
                return _cachedHubDir;
            }
        }

        internal static void ResetCacheForTests()
        {
            _cachedProjectRoot = null;
            _cachedHubDir = null;
        }
    }
}
