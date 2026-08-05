using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace ArcForge.Hades.Editor.Asphodel.Conventions
{
    /// <summary>
    /// Per-detector lifecycle state persisted at inferred/.conventions-state.json (dot-prefixed so the
    /// dashboard's *.md listing ignores it). Remembers dismissals so the inferrer never re-nags, and
    /// promotions so it does not re-propose a confirmed convention.
    /// </summary>
    public sealed class ConventionLedger
    {
        public sealed class Entry { public string Status = "none"; public double Confidence; }

        const string FileName = ".conventions-state.json";
        readonly Dictionary<string, Entry> _map;

        ConventionLedger(Dictionary<string, Entry> map) { _map = map ?? new Dictionary<string, Entry>(); }

        public static ConventionLedger Load(string inferredDir)
        {
            try
            {
                var path = Path.Combine(inferredDir, FileName);
                if (File.Exists(path))
                {
                    var map = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(path));
                    if (map != null) return new ConventionLedger(map);
                }
            }
            catch (System.Exception ex) { Debug.LogWarning($"[Hades] convention ledger unreadable, starting fresh: {ex.Message}"); }
            return new ConventionLedger(new Dictionary<string, Entry>());
        }

        public void Save(string inferredDir)
        {
            Directory.CreateDirectory(inferredDir);
            var path = Path.Combine(inferredDir, FileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(_map, Formatting.Indented));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public string Status(string key) => _map.TryGetValue(key, out var e) ? e.Status : "none";
        public double Confidence(string key) => _map.TryGetValue(key, out var e) ? e.Confidence : 0.0;
        public void Set(string key, string status, double confidence) => _map[key] = new Entry { Status = status, Confidence = confidence };
    }
}
