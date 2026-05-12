// Editor/Charon/CharonDashboard.cs
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ArcForge.Hades.Editor.Core;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ArcForge.Hades.Editor.Charon
{
    public static class CharonDashboard
    {
        const string PidKey = "Hades_Dashboard_PID";
        const string PortKey = "Hades_Dashboard_Port";

        static Process _dashboardProcess;

        static CharonDashboard()
        {
            ReattachProcess();
        }

        static void ReattachProcess()
        {
            var pid = SessionState.GetInt(PidKey, -1);
            if (pid <= 0) return;

            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    _dashboardProcess = proc;
                    EditorApplication.quitting += StopDashboard;
                    return;
                }
            }
            catch
            {
                // Process no longer exists
            }

            SessionState.EraseInt(PidKey);
            SessionState.EraseInt(PortKey);
        }

        [MenuItem("Hades/Open Charon Dashboard")]
        public static async void OpenDashboard()
        {
            if (_dashboardProcess != null && !_dashboardProcess.HasExited)
            {
                var existingPort = SessionState.GetInt(PortKey, 7878);
                Application.OpenURL($"http://127.0.0.1:{existingPort}");
                return;
            }

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var dbPath = Path.Combine(projectRoot, ".arcforge", "traces.db");

            if (!File.Exists(dbPath))
            {
                Debug.LogWarning("[Hades] No traces.db found. Run some MCP tool calls first to generate trace data.");
                return;
            }

            var nodePath = ProcessResolver.FindExecutable("node");
            if (nodePath == null)
            {
                EditorUtility.DisplayDialog("Hades Dashboard",
                    "Node.js is required but was not found on this system.\nInstall Node.js 20+ and try again.", "OK");
                return;
            }

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CharonDashboard).Assembly);
            var packageRoot = packageInfo.resolvedPath;
            var dashboardDir = Path.Combine(packageRoot, "Dashboard~");
            var serverScript = Path.Combine(dashboardDir, "dist", "server.js");

            if (!EnsureDashboardBuilt(dashboardDir, serverScript))
                return;

            var portFile = Path.Combine(Path.GetTempPath(), $"hades_dashboard_port_{System.Guid.NewGuid()}.tmp");

            var memoryDir = Path.Combine(projectRoot, ".arcforge", "memory");

            var startInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = $"\"{serverScript}\" --db \"{dbPath}\" --memory \"{memoryDir}\"",
                WorkingDirectory = dashboardDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.EnvironmentVariables["HADES_PORT_FILE"] = portFile;

            try
            {
                _dashboardProcess = Process.Start(startInfo);
                SessionState.SetInt(PidKey, _dashboardProcess.Id);

                EditorApplication.quitting += StopDashboard;

                Debug.Log("[Hades] Dashboard starting...");

                var port = await WaitForPort(portFile);
                if (port > 0)
                {
                    SessionState.SetInt(PortKey, port);
                    Application.OpenURL($"http://127.0.0.1:{port}");
                    Debug.Log($"[Hades Dashboard] Running at http://127.0.0.1:{port}");
                }
                else
                {
                    Debug.LogWarning("[Hades Dashboard] Started but could not determine port. Check console for errors.");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Hades] Failed to start dashboard: {ex.Message}");
            }
        }

        [MenuItem("Hades/Stop Charon Dashboard")]
        public static void StopDashboard()
        {
            EditorApplication.quitting -= StopDashboard;

            if (_dashboardProcess == null || _dashboardProcess.HasExited)
            {
                _dashboardProcess = null;
                SessionState.EraseInt(PidKey);
                return;
            }

            try
            {
                _dashboardProcess.Kill();
                Debug.Log("[Hades] Dashboard stopped.");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to stop dashboard: {ex.Message}");
            }

            _dashboardProcess = null;
            SessionState.EraseInt(PidKey);
        }

        [MenuItem("Hades/Stop Charon Dashboard", true)]
        public static bool ValidateStopDashboard()
        {
            return _dashboardProcess != null && !_dashboardProcess.HasExited;
        }

        static async Task<int> WaitForPort(string portFile)
        {
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(200);

                if (_dashboardProcess == null || _dashboardProcess.HasExited)
                    return -1;

                if (File.Exists(portFile))
                {
                    var content = File.ReadAllText(portFile).Trim();
                    if (int.TryParse(content, out var port))
                    {
                        File.Delete(portFile);
                        return port;
                    }
                }
            }
            return -1;
        }

        static bool EnsureDashboardBuilt(string dashboardDir, string serverScript)
        {
            var nodeModules = Path.Combine(dashboardDir, "node_modules");
            bool needsInstall = !Directory.Exists(nodeModules);
            bool needsBuild = !File.Exists(serverScript);

            if (!needsInstall && !needsBuild)
                return true;

            if (ProcessResolver.FindExecutable("npm") == null)
            {
                EditorUtility.DisplayDialog("Hades Dashboard",
                    "npm is required but was not found on this system.\nInstall Node.js 20+ and try again.", "OK");
                return false;
            }

            if (needsInstall)
            {
                EditorUtility.DisplayProgressBar("Hades Dashboard", "Installing dependencies (first time only)...", 0.3f);
                var result = ProcessResolver.Run("npm", "install", dashboardDir);
                if (!result.Success)
                {
                    EditorUtility.ClearProgressBar();
                    Debug.LogError($"[Hades] npm install failed: {result.Error}");
                    EditorUtility.DisplayDialog("Hades Dashboard",
                        $"Failed to install dashboard dependencies.\n\n{result.Error}", "OK");
                    return false;
                }
            }

            EditorUtility.DisplayProgressBar("Hades Dashboard", "Building dashboard (first time only)...", 0.7f);
            var buildResult = ProcessResolver.Run("npm", "run build", dashboardDir);
            EditorUtility.ClearProgressBar();

            if (!buildResult.Success)
            {
                Debug.LogError($"[Hades] npm run build failed: {buildResult.Error}");
                EditorUtility.DisplayDialog("Hades Dashboard",
                    $"Failed to build dashboard.\n\n{buildResult.Error}", "OK");
                return false;
            }

            Debug.Log("[Hades] Dashboard setup complete.");
            return true;
        }
    }
}
