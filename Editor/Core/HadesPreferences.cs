using UnityEditor;
using UnityEngine;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>
    /// Project Settings > Hades. Every value here is stored per-project in
    /// .arcforge/config.local.yaml, so two Unity projects on one machine no longer share them.
    /// </summary>
    public static class HadesPreferences
    {
        const string SettingsPath = "Project/Hades";

        [MenuItem("Hades/Settings...", priority = 300)]
        public static void Open() => SettingsService.OpenProjectSettings(SettingsPath);

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "Hades",
                keywords = new[] { "hades", "hub", "mcp", "charon", "skills", "scope" },
                guiHandler = _ => Draw()
            };
        }

        static void Draw()
        {
            var settings = new HadesSettings();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Installation Scope", EditorStyles.boldLabel);

            // EnumPopup, not IntPopup: EditorGUILayout.IntPopup has no GUIContent-label overload,
            // so a tooltip is only reachable through the enum form. Costs the friendlier
            // "Local (this project)" wording — the tooltip carries that detail instead.
            var hubScope = (HadesScope)EditorGUILayout.EnumPopup(
                new GUIContent("Hub",
                    "Local keeps hub.json in this project's .arcforge/hades-hub. " +
                    "Global shares one hub across every project on this machine."),
                settings.HubScope);
            if (hubScope != settings.HubScope) settings.HubScope = hubScope;

            EditorGUILayout.HelpBox(
                "Changing the hub scope takes effect on the next Claude Code session — the " +
                "launcher reads this setting when it starts.", MessageType.Info);

            var skillsScope = (HadesScope)EditorGUILayout.EnumPopup(
                new GUIContent("Skills",
                    "Local installs into this project's .claude/skills (Claude Code reads it). " +
                    "Global installs into ~/.claude/skills, which Claude Desktop requires."),
                settings.SkillsScope);
            if (skillsScope != settings.SkillsScope) settings.SkillsScope = skillsScope;

            var desktop = EditorGUILayout.Toggle(
                new GUIContent("Claude Desktop Integration",
                    "Writes ~/Library/Application Support/Claude/claude_desktop_config.json. " +
                    "This file cannot be project-local. Turn off to keep every Hades write " +
                    "inside the workspace."),
                settings.DesktopIntegration);
            if (desktop != settings.DesktopIntegration) settings.DesktopIntegration = desktop;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("MCP Server", EditorStyles.boldLabel);

            var enabled = EditorGUILayout.Toggle("Enabled", settings.Enabled);
            if (enabled != settings.Enabled) settings.Enabled = enabled;

            var autoStart = EditorGUILayout.Toggle("Auto Start", settings.AutoStart);
            if (autoStart != settings.AutoStart) settings.AutoStart = autoStart;

            var port = EditorGUILayout.IntField(
                new GUIContent("Port", "0 lets the OS assign an ephemeral port."), settings.Port);
            if (port != settings.Port) settings.Port = Mathf.Clamp(port, 0, 65535);

            var logLevel = EditorGUILayout.IntSlider("Log Level", settings.LogLevel, 0, 3);
            if (logLevel != settings.LogLevel) settings.LogLevel = logLevel;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Domain Reload", EditorStyles.boldLabel);

            var strategy = (ReloadStrategy)EditorGUILayout.EnumPopup(
                "Strategy", settings.DomainReloadStrategy);
            if (strategy != settings.DomainReloadStrategy) settings.DomainReloadStrategy = strategy;

            var timeout = EditorGUILayout.IntField("Timeout (s)", settings.ReloadTimeoutSeconds);
            if (timeout != settings.ReloadTimeoutSeconds)
                settings.ReloadTimeoutSeconds = Mathf.Max(1, timeout);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Charon (Observability)", EditorStyles.boldLabel);

            var charon = EditorGUILayout.Toggle("Enabled", settings.CharonEnabled);
            if (charon != settings.CharonEnabled) settings.CharonEnabled = charon;

            var retention = EditorGUILayout.IntField("Retention (days)", settings.CharonRetentionDays);
            if (retention != settings.CharonRetentionDays)
                settings.CharonRetentionDays = Mathf.Max(0, retention);

            var maxSize = EditorGUILayout.IntField(
                new GUIContent("Max Size (MB)", "Hard cap on traces.db. 0 disables the cap."),
                settings.CharonMaxSizeMb);
            if (maxSize != settings.CharonMaxSizeMb)
                settings.CharonMaxSizeMb = Mathf.Max(0, maxSize);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Storage", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(
                System.IO.Path.Combine(HadesPaths.ArcforgeDir, HadesConfig.FileName),
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.SelectableLabel(HadesPaths.HubDir, EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
        }
    }
}
