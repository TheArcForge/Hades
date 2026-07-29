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

        public static string ArcforgeDir
            => Path.Combine(PathSandbox.ProjectRoot, ArcforgeDirName);

        public static string HomeDir
            => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static string GlobalHubDirForMachine => GlobalHubDir(HomeDir);

        /// <summary>Live resolution for production callers. Reads env + this project's settings.</summary>
        public static string HubDir => ResolveHubDir(
            Environment.GetEnvironmentVariable(EnvHubDir),
            new HadesSettings().HubScope,
            PathSandbox.ProjectRoot,
            HomeDir);
    }
}
