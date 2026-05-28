using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ArcForge.Hades.Editor.MCP
{
    [InitializeOnLoad]
    public class MCPServer : IDisposable
    {
        static MCPServer _instance;

        IMCPTransport _transport;
        MCPDispatcher _dispatcher;
        IDomainReloadStrategy _reloadStrategy;
        HadesSettings _settings;
        ConcurrentQueue<WorkItem> _workQueue;
        bool _disposed;

        public static MCPServer Instance => _instance;
        public bool IsRunning => _transport?.IsRunning ?? false;
        public string Endpoint => _transport?.Endpoint;
        public int Port => (_transport as HttpTransport)?.Port ?? 0;
        public IDomainReloadStrategy ActiveReloadStrategy => _reloadStrategy;

        public event Action<string, string, MCPToolResult> OnToolExecuted;

        const double HeartbeatIntervalSeconds = 30.0;
        double _lastHeartbeat;

        static MCPServer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;

            EditorApplication.delayCall += () =>
            {
                if (_instance != null && _instance.IsRunning) return;

                var settings = new HadesSettings();
                if (!settings.Enabled || !settings.AutoStart) return;

                var server = new MCPServer();
                var savedPort = SessionState.GetInt("Hades_MCP_Port", 0);
                if (savedPort > 0) settings.Port = savedPort;
                server.Start(settings);
            };
        }

        static void OnBeforeReload()
        {
            if (_instance != null && _instance.IsRunning)
            {
                SessionState.SetBool("Hades_MCP_WasRunning", true);
                if (_instance._transport?.Endpoint != null)
                {
                    var uri = new Uri(_instance._transport.Endpoint);
                    SessionState.SetInt("Hades_MCP_Port", uri.Port);
                }
                HubClient.Deregister(Core.PathSandbox.ProjectRoot, true);
            }
            else
            {
                SessionState.SetBool("Hades_MCP_WasRunning", false);
            }
        }

        public void Start(HadesSettings settings)
        {
            if (IsRunning) return;

            _settings = settings;
            _dispatcher = new MCPDispatcher();
            _workQueue = new ConcurrentQueue<WorkItem>();

            _reloadStrategy = new AutoReloadStrategy(settings.ReloadTimeoutSeconds);

            _transport = new HttpTransport();
            (_transport as HttpTransport)?.SetTraceAwareRequestHandler(EnqueueAndWait);
            _transport.Start(settings.Port);

            var port = (_transport as HttpTransport)?.Port ?? 0;
            var pid = Process.GetCurrentProcess().Id;
            var projectName = System.IO.Path.GetFileName(Core.PathSandbox.ProjectRoot);

            var manifestPackages = HubClient.ReadManifestPackages(Core.PathSandbox.ProjectRoot);
            HubClient.Register(projectName, Core.PathSandbox.ProjectRoot, port, pid, manifestPackages);
            Core.MCPClientConfig.OnServerStart(port);

            EditorApplication.update += ProcessMainThreadQueue;
            EditorApplication.update += HeartbeatTick;
            EditorApplication.quitting += Stop;

            _instance = this;
            _lastHeartbeat = EditorApplication.timeSinceStartup;

            if (_settings.LogLevel >= 1)
                Debug.Log($"[Hades MCP] Server running on {_transport.Endpoint}");
        }

        public void Stop()
        {
            if (!IsRunning && _transport == null) return;

            EditorApplication.update -= ProcessMainThreadQueue;
            EditorApplication.update -= HeartbeatTick;
            EditorApplication.quitting -= Stop;

            _transport?.Stop();
            _reloadStrategy?.Dispose();
            _reloadStrategy = null;

            HubClient.Deregister(Core.PathSandbox.ProjectRoot, false);

            while (_workQueue != null && _workQueue.TryDequeue(out var item))
                item.Completion.TrySetCanceled();

            if (_settings?.LogLevel >= 1)
                Debug.Log("[Hades MCP] Server stopped.");
        }

        public void NotifyTurnComplete()
        {
            _reloadStrategy?.OnTurnComplete();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _transport?.Dispose();
            if (_instance == this) _instance = null;
        }

        void HeartbeatTick()
        {
            if (!IsRunning) return;
            if (EditorApplication.timeSinceStartup - _lastHeartbeat < HeartbeatIntervalSeconds) return;

            _lastHeartbeat = EditorApplication.timeSinceStartup;
            var port = (_transport as HttpTransport)?.Port ?? 0;
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;

            // Check if Hub has restarted (new PID or port in hub.json)
            var changed = HubClient.DetectHubChange();
            if (changed != null)
            {
                Debug.Log($"[Hades MCP] Hub restart detected (port {changed.Port}, pid {changed.Pid}), re-registering…");
                var projectName = System.IO.Path.GetFileName(Core.PathSandbox.ProjectRoot);
                var manifestPackages = HubClient.ReadManifestPackages(Core.PathSandbox.ProjectRoot);
                HubClient.Register(projectName, Core.PathSandbox.ProjectRoot, port, pid, manifestPackages);
                return;
            }

            HubClient.Heartbeat(Core.PathSandbox.ProjectRoot, port, pid);
        }

        Task<string> EnqueueAndWait(string json, string traceId)
        {
            var tcs = new TaskCompletionSource<string>();
            _workQueue.Enqueue(new WorkItem(json, tcs, traceId));
            return tcs.Task;
        }

        string CreateJsonRpcSuccess(JToken id, JObject result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = result
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        void ProcessMainThreadQueue()
        {
            CharonEmitter.TickFlush();

            while (_workQueue.TryDequeue(out var item))
            {
                try
                {
                    var toolName = ExtractToolName(item.Json);
                    if (toolName != null)
                        _reloadStrategy?.OnToolCallStart(toolName);

                    string response;
                    if (toolName != null)
                    {
                        var request = JObject.Parse(item.Json);
                        var id = request["id"];
                        var arguments = request["params"]?["arguments"] as JObject ?? new JObject();

                        var toolResult = _dispatcher.CallToolWithTracing(toolName, arguments, item.TraceId);
                        response = CreateJsonRpcSuccess(id, toolResult.ToMCPResponse());
                    }
                    else
                    {
                        response = _dispatcher.HandleRequest(item.Json);
                    }

                    if (toolName != null)
                    {
                        _reloadStrategy?.OnToolCallEnd(toolName);
                        var result = ExtractToolResult(response);
                        if (result != null)
                            OnToolExecuted?.Invoke(toolName, item.Json, result);
                    }

                    item.Completion.TrySetResult(response);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
            }
        }

        static string ExtractToolName(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                if (obj["method"]?.ToString() == "tools/call")
                    return obj["params"]?["name"]?.ToString();
            }
            catch { }
            return null;
        }

        static MCPToolResult ExtractToolResult(string responseJson)
        {
            if (string.IsNullOrEmpty(responseJson)) return null;
            try
            {
                var obj = JObject.Parse(responseJson);
                var text = obj["result"]?["content"]?[0]?["text"]?.ToString();
                var isError = obj["result"]?["isError"]?.Value<bool>() ?? false;
                if (text != null)
                    return isError ? MCPToolResult.Error(text) : MCPToolResult.Success(text);
            }
            catch { }
            return null;
        }

        class WorkItem
        {
            public string Json { get; }
            public string TraceId { get; }
            public TaskCompletionSource<string> Completion { get; }

            public WorkItem(string json, TaskCompletionSource<string> completion, string traceId = null)
            {
                Json = json;
                Completion = completion;
                TraceId = traceId;
            }
        }
    }
}
