using UnityEditor;

namespace ArcForge.Hades.Editor.Core
{
    public enum ReloadStrategy
    {
        Auto = 0,
        Manual = 1
    }

    public class HadesSettings
    {
        const string Prefix = "Hades_MCP_";

        public int Port
        {
            get => EditorPrefs.GetInt(Prefix + "Port", 0);
            set => EditorPrefs.SetInt(Prefix + "Port", value);
        }

        public bool Enabled
        {
            get => EditorPrefs.GetBool(Prefix + "Enabled", true);
            set => EditorPrefs.SetBool(Prefix + "Enabled", value);
        }

        public bool AutoStart
        {
            get => EditorPrefs.GetBool(Prefix + "AutoStart", true);
            set => EditorPrefs.SetBool(Prefix + "AutoStart", value);
        }

        public int LogLevel
        {
            get => EditorPrefs.GetInt(Prefix + "LogLevel", 1);
            set => EditorPrefs.SetInt(Prefix + "LogLevel", value);
        }

        public ReloadStrategy DomainReloadStrategy
        {
            get => (ReloadStrategy)EditorPrefs.GetInt(Prefix + "ReloadStrategy", 0);
            set => EditorPrefs.SetInt(Prefix + "ReloadStrategy", (int)value);
        }

        public int ReloadTimeoutSeconds
        {
            get => EditorPrefs.GetInt(Prefix + "ReloadTimeout", 120);
            set => EditorPrefs.SetInt(Prefix + "ReloadTimeout", value);
        }

        public bool CharonEnabled
        {
            get => EditorPrefs.GetBool(Prefix + "CharonEnabled", true);
            set => EditorPrefs.SetBool(Prefix + "CharonEnabled", value);
        }

        public int CharonRetentionDays
        {
            get => EditorPrefs.GetInt(Prefix + "CharonRetentionDays", 30);
            set => EditorPrefs.SetInt(Prefix + "CharonRetentionDays", value);
        }

        // Hard size cap for traces.db. Time-based retention alone let the trace DB
        // grow into the multi-GB range on a large, heavily-used project; this is the
        // backstop. 0 disables the cap.
        public int CharonMaxSizeMb
        {
            get => EditorPrefs.GetInt(Prefix + "CharonMaxSizeMb", 500);
            set => EditorPrefs.SetInt(Prefix + "CharonMaxSizeMb", value);
        }
    }
}
