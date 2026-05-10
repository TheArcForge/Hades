using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ArcForge.Hades.Editor.Core
{
    public class DiscoveryData
    {
        public int Port { get; set; }
        public string Endpoint { get; set; }
        public int Pid { get; set; }
    }

    public static class DiscoveryFile
    {
        public static string DefaultPath =>
            Path.Combine(PathSandbox.ProjectRoot, ".arcforge", "server.json");

        public static void Write(string filePath, int port, int pid)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var obj = new JObject
            {
                ["port"] = port,
                ["endpoint"] = $"http://127.0.0.1:{port}/rpc",
                ["pid"] = pid,
                ["startedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            var tmpPath = filePath + ".tmp";
            File.WriteAllText(tmpPath, obj.ToString(Formatting.Indented));
            if (File.Exists(filePath))
                File.Delete(filePath);
            File.Move(tmpPath, filePath);
        }

        public static DiscoveryData Read(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                var json = File.ReadAllText(filePath);
                var obj = JObject.Parse(json);
                return new DiscoveryData
                {
                    Port = obj["port"].Value<int>(),
                    Endpoint = obj["endpoint"].ToString(),
                    Pid = obj["pid"].Value<int>()
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to read discovery file: {ex.Message}");
                return null;
            }
        }

        public static void Delete(string filePath)
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }
}
