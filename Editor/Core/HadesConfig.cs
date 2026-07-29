using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>
    /// Per-developer, project-local settings at &lt;projectRoot&gt;/.arcforge/config.local.yaml.
    ///
    /// Deliberately a flat `key: value` dialect — the same subset InferenceConfig already parsed.
    /// That keeps two constraints satisfiable at once: no YAML dependency in the Editor assembly,
    /// and the Node launcher can read the one key it needs (hub_scope) in ~15 lines without
    /// pulling a parser into its zero-dependency bundle.
    ///
    /// Every getter falls back silently. A missing file is the normal state of a fresh clone,
    /// not an error worth logging.
    /// </summary>
    public class HadesConfig
    {
        public const string FileName = "config.local.yaml";

        const string Header =
            "# Hades per-developer settings for this project. Machine-specific; gitignored.\n" +
            "# Edit via Unity: Project Settings > Hades (or the Hades/Settings... menu).\n";

        readonly Dictionary<string, string> _values;
        readonly string _filePath;

        HadesConfig(string filePath, Dictionary<string, string> values)
        {
            _filePath = filePath;
            _values = values;
        }

        public static HadesConfig Load(string arcforgeDir)
        {
            var filePath = Path.Combine(arcforgeDir, FileName);
            return new HadesConfig(filePath, Parse(ReadLines(filePath)));
        }

        public bool Exists => File.Exists(_filePath);

        public string FilePath => _filePath;

        static string[] ReadLines(string filePath)
        {
            try
            {
                return File.Exists(filePath) ? File.ReadAllLines(filePath) : new string[0];
            }
            catch
            {
                // Unreadable file is treated exactly like a missing one: fall back to defaults.
                return new string[0];
            }
        }

        /// <summary>
        /// Flat `key: value` parse. Blank lines, comment lines, and lines with no colon are
        /// skipped. Last occurrence of a duplicated key wins.
        /// </summary>
        internal static Dictionary<string, string> Parse(string[] lines)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;

                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx <= 0) continue;

                var key = trimmed.Substring(0, colonIdx).Trim();
                var value = trimmed.Substring(colonIdx + 1).Trim();
                if (key.Length == 0) continue;

                values[key] = value;
            }

            return values;
        }

        public string GetString(string key, string fallback)
            => _values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

        public bool GetBool(string key, bool fallback)
        {
            if (!_values.TryGetValue(key, out var v)) return fallback;
            if (bool.TryParse(v, out var parsed)) return parsed;
            return fallback;
        }

        public int GetInt(string key, int fallback)
        {
            if (!_values.TryGetValue(key, out var v)) return fallback;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return fallback;
        }

        public void Set(string key, string value) => _values[key] = value ?? "";

        public void Set(string key, bool value) => Set(key, value ? "true" : "false");

        public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// Writes every known key back, sorted for a stable diff. Keys this version does not
        /// recognise are preserved — a newer Hades must not lose settings when an older one saves.
        /// </summary>
        public void Save()
        {
            var keys = new List<string>(_values.Keys);
            keys.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.Append(Header);
            foreach (var key in keys)
                sb.Append(key).Append(": ").Append(_values[key]).Append('\n');

            AtomicFile.Write(_filePath, sb.ToString());
        }
    }
}
