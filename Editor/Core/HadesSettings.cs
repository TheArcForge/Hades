using System;
using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Core
{
    public enum ReloadStrategy
    {
        Auto = 0,
        Manual = 1
    }

    /// <summary>
    /// Hades settings for THIS project. Storage is .arcforge/config.local.yaml, not EditorPrefs.
    ///
    /// EditorPrefs is global per Unity install, so two projects on one machine previously shared
    /// port, log level, and Charon retention. The public surface here is unchanged so existing
    /// callers (MCPServer, CharonInitializer) need no edit; only the backing store moved.
    /// </summary>
    public class HadesSettings
    {
        // Legacy EditorPrefs keys, read once during migration and never again.
        const string LegacyPrefix = "Hades_MCP_";
        static readonly string[] LegacyKeys =
        {
            LegacyPrefix + "Port",
            LegacyPrefix + "Enabled",
            LegacyPrefix + "AutoStart",
            LegacyPrefix + "LogLevel",
            LegacyPrefix + "ReloadStrategy",
            LegacyPrefix + "ReloadTimeout",
            LegacyPrefix + "CharonEnabled",
            LegacyPrefix + "CharonRetentionDays",
            LegacyPrefix + "CharonMaxSizeMb"
        };

        const string KeyHubScope = "hub_scope";
        const string KeySkillsScope = "skills_scope";
        const string KeyDesktopIntegration = "desktop_integration";
        const string KeyPort = "mcp_port";
        const string KeyEnabled = "mcp_enabled";
        const string KeyAutoStart = "mcp_auto_start";
        const string KeyLogLevel = "mcp_log_level";
        const string KeyReloadStrategy = "domain_reload_strategy";
        const string KeyReloadTimeout = "reload_timeout_seconds";
        const string KeyCharonEnabled = "charon_enabled";
        const string KeyCharonRetentionDays = "charon_retention_days";
        const string KeyCharonMaxSizeMb = "charon_max_size_mb";

        readonly HadesConfig _config;

        public HadesSettings() : this(HadesConfig.Load(HadesPaths.ArcforgeDir)) { }

        public HadesSettings(HadesConfig config)
        {
            _config = config;
        }

        public HadesScope HubScope
        {
            get => ParseScope(_config.GetString(KeyHubScope, "local"));
            set => SetAndSave(KeyHubScope, ScopeToString(value));
        }

        public HadesScope SkillsScope
        {
            get => ParseScope(_config.GetString(KeySkillsScope, "local"));
            set => SetAndSave(KeySkillsScope, ScopeToString(value));
        }

        public bool DesktopIntegration
        {
            get => _config.GetBool(KeyDesktopIntegration, true);
            set => SetAndSave(KeyDesktopIntegration, value);
        }

        public int Port
        {
            get => _config.GetInt(KeyPort, 0);
            set => SetAndSave(KeyPort, value);
        }

        public bool Enabled
        {
            get => _config.GetBool(KeyEnabled, true);
            set => SetAndSave(KeyEnabled, value);
        }

        public bool AutoStart
        {
            get => _config.GetBool(KeyAutoStart, true);
            set => SetAndSave(KeyAutoStart, value);
        }

        public int LogLevel
        {
            get => _config.GetInt(KeyLogLevel, 1);
            set => SetAndSave(KeyLogLevel, value);
        }

        public ReloadStrategy DomainReloadStrategy
        {
            get => string.Equals(_config.GetString(KeyReloadStrategy, "auto"), "manual",
                       StringComparison.OrdinalIgnoreCase)
                ? ReloadStrategy.Manual
                : ReloadStrategy.Auto;
            set => SetAndSave(KeyReloadStrategy,
                value == ReloadStrategy.Manual ? "manual" : "auto");
        }

        public int ReloadTimeoutSeconds
        {
            get => _config.GetInt(KeyReloadTimeout, 120);
            set => SetAndSave(KeyReloadTimeout, value);
        }

        public bool CharonEnabled
        {
            get => _config.GetBool(KeyCharonEnabled, true);
            set => SetAndSave(KeyCharonEnabled, value);
        }

        public int CharonRetentionDays
        {
            get => _config.GetInt(KeyCharonRetentionDays, 30);
            set => SetAndSave(KeyCharonRetentionDays, value);
        }

        // Hard size cap for traces.db. Time-based retention alone let the trace DB grow into
        // the multi-GB range on a large, heavily-used project; this is the backstop. 0 disables.
        public int CharonMaxSizeMb
        {
            get => _config.GetInt(KeyCharonMaxSizeMb, 500);
            set => SetAndSave(KeyCharonMaxSizeMb, value);
        }

        void SetAndSave(string key, string value) { _config.Set(key, value); _config.Save(); }
        void SetAndSave(string key, bool value) { _config.Set(key, value); _config.Save(); }
        void SetAndSave(string key, int value) { _config.Set(key, value); _config.Save(); }

        static HadesScope ParseScope(string raw)
            => string.Equals(raw, "global", StringComparison.OrdinalIgnoreCase)
                ? HadesScope.Global
                : HadesScope.Local;

        static string ScopeToString(HadesScope scope)
            => scope == HadesScope.Global ? "global" : "local";

        // ---- Migration (spec §4.7) ----

        public static bool HasLegacyEditorPrefs()
        {
            foreach (var key in LegacyKeys)
                if (EditorPrefs.HasKey(key)) return true;
            return false;
        }

        /// <summary>
        /// Copies legacy EditorPrefs values into <paramref name="config"/>. Does not save —
        /// the caller decides when to write. EditorPrefs keys are left in place, because another
        /// project on this machine may not have migrated yet.
        /// </summary>
        public static void ImportFromEditorPrefs(HadesConfig config)
        {
            config.Set(KeyPort, EditorPrefs.GetInt(LegacyPrefix + "Port", 0));
            config.Set(KeyEnabled, EditorPrefs.GetBool(LegacyPrefix + "Enabled", true));
            config.Set(KeyAutoStart, EditorPrefs.GetBool(LegacyPrefix + "AutoStart", true));
            config.Set(KeyLogLevel, EditorPrefs.GetInt(LegacyPrefix + "LogLevel", 1));
            config.Set(KeyReloadStrategy,
                EditorPrefs.GetInt(LegacyPrefix + "ReloadStrategy", 0) == 1 ? "manual" : "auto");
            config.Set(KeyReloadTimeout, EditorPrefs.GetInt(LegacyPrefix + "ReloadTimeout", 120));
            config.Set(KeyCharonEnabled, EditorPrefs.GetBool(LegacyPrefix + "CharonEnabled", true));
            config.Set(KeyCharonRetentionDays,
                EditorPrefs.GetInt(LegacyPrefix + "CharonRetentionDays", 30));
            config.Set(KeyCharonMaxSizeMb, EditorPrefs.GetInt(LegacyPrefix + "CharonMaxSizeMb", 500));
        }

        /// <summary>
        /// Creates .arcforge/config.local.yaml if absent, offering a one-time import of legacy
        /// EditorPrefs values. The file is always created so the prompt never repeats. Silent
        /// (defaults only) in batch mode — nothing may block a headless editor.
        /// </summary>
        public static void EnsureMigrated()
        {
            var config = HadesConfig.Load(HadesPaths.ArcforgeDir);
            if (config.Exists) return;

            var import = HasLegacyEditorPrefs()
                && !Application.isBatchMode
                && EditorUtility.DisplayDialog(
                    "Hades — Import Settings?",
                    "Hades found existing settings stored globally in this Unity install.\n\n" +
                    "Import them into this project?\n\n" +
                    "Settings are now stored per-project in .arcforge/config.local.yaml.",
                    "Import", "Use Defaults");

            if (import) ImportFromEditorPrefs(config);

            config.Save();
        }
    }
}
