using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ArcForge.Hades.Editor.MCP
{
    public class HubInfo
    {
        public int Port { get; set; }
        public int Pid { get; set; }
    }

    public static class HubClient
    {
        static readonly string HubDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".arcforge", "hades-hub");

        static string HubJsonPath => Path.Combine(HubDir, "hub.json");
        static string PendingDir => Path.Combine(HubDir, "pending");

        public static HubInfo ReadHubInfo(string path = null)
        {
            var filePath = path ?? HubJsonPath;
            if (!File.Exists(filePath)) return null;

            try
            {
                var json = File.ReadAllText(filePath);
                var obj = JObject.Parse(json);
                return new HubInfo
                {
                    Port = obj["port"].Value<int>(),
                    Pid = obj["pid"].Value<int>()
                };
            }
            catch
            {
                return null;
            }
        }

        public static bool IsHubRunning()
        {
            var info = ReadHubInfo();
            if (info == null) return false;

            try
            {
                Process.GetProcessById(info.Pid);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool Register(string projectName, string projectPath, int port, int pid,
            string[] manifestPackages = null)
        {
            var info = ReadHubInfo();
            if (info == null || !IsHubRunning())
            {
                WriteBreadcrumb(PendingDir, projectName, projectPath, port, pid);
                return false;
            }

            var body = new JObject
            {
                ["projectName"] = projectName,
                ["projectPath"] = projectPath,
                ["port"] = port,
                ["pid"] = pid
            };

            if (manifestPackages != null && manifestPackages.Length > 0)
                body["manifestPackages"] = new JArray(manifestPackages);

            return PostToHub(info.Port, "/api/register", body.ToString(Formatting.None));
        }

        public static string[] ReadManifestPackages(string projectRoot)
        {
            try
            {
                var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
                if (!File.Exists(manifestPath)) return null;

                var root = JObject.Parse(File.ReadAllText(manifestPath));
                var deps = root["dependencies"] as JObject;
                if (deps == null) return null;

                var paths = new System.Collections.Generic.List<string>();
                foreach (var prop in deps.Properties())
                {
                    var val = prop.Value.ToString();
                    if (!val.StartsWith("file:")) continue;

                    var raw = val.Substring(5);
                    var resolved = Path.IsPathRooted(raw)
                        ? raw
                        : Path.GetFullPath(Path.Combine(projectRoot, "Packages", raw));
                    paths.Add(resolved);
                }

                return paths.Count > 0 ? paths.ToArray() : null;
            }
            catch
            {
                return null;
            }
        }

        public static bool Deregister(string projectPath, bool transient)
        {
            var info = ReadHubInfo();
            if (info == null) return false;

            var body = new JObject
            {
                ["projectPath"] = projectPath,
                ["transient"] = transient
            };

            return PostToHub(info.Port, "/api/deregister", body.ToString(Formatting.None));
        }

        public static bool Heartbeat(string projectPath, int port, int pid)
        {
            var info = ReadHubInfo();
            if (info == null) return false;

            var body = new JObject
            {
                ["projectPath"] = projectPath,
                ["port"] = port,
                ["pid"] = pid
            };

            return PostToHub(info.Port, "/api/heartbeat", body.ToString(Formatting.None));
        }

        public static void WriteBreadcrumb(string pendingDir, string projectName,
            string projectPath, int port, int pid)
        {
            try
            {
                if (!Directory.Exists(pendingDir))
                    Directory.CreateDirectory(pendingDir);

                var entry = new JObject
                {
                    ["projectName"] = projectName,
                    ["projectPath"] = projectPath,
                    ["port"] = port,
                    ["pid"] = pid
                };

                var hash = projectPath.GetHashCode().ToString("x8");
                var filePath = Path.Combine(pendingDir, $"{hash}.json");
                File.WriteAllText(filePath, entry.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to write breadcrumb: {ex.Message}");
            }
        }

        static bool PostToHub(int hubPort, string path, string body)
        {
            try
            {
                var request = WebRequest.CreateHttp($"http://127.0.0.1:{hubPort}{path}");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 5000;

                var bytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bytes.Length;
                using (var stream = request.GetRequestStream())
                    stream.Write(bytes, 0, bytes.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                    return response.StatusCode == HttpStatusCode.OK;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Hub request failed ({path}): {ex.Message}");
                return false;
            }
        }
    }
}
