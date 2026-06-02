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
        System.Threading.Timer _heartbeatTimer;
        volatile bool _heartbeatActive;
        // Cached at Start() on the main thread so the background heartbeat timer never touches
        // a main-thread-only Unity API (PathSandbox.ProjectRoot reads Application.dataPath).
        string _projectRoot;
        string _projectName;
        int _pid;
        int _heartbeatPort;

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

            // Tear down the background heartbeat timer before the domain reloads.
            if (_instance != null)
            {
                _instance._heartbeatActive = false;
                _instance._heartbeatTimer?.Dispose();
                _instance._heartbeatTimer = null;
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
            EditorApplication.quitting += Stop;

            _instance = this;

            // Heartbeat runs on a background timer (not EditorApplication.update) so registration
            // survives a stalled/napped main thread — the cause of "No Unity instance found" after
            // idle. Values are cached above on the main thread; the timer only touches pure I/O.
            _projectRoot = Core.PathSandbox.ProjectRoot;
            _projectName = projectName;
            _pid = pid;
            _heartbeatPort = port;
            _heartbeatActive = true;
            var heartbeatMs = (int)(HeartbeatIntervalSeconds * 1000);
            _heartbeatTimer = new System.Threading.Timer(HeartbeatTick, null, heartbeatMs, heartbeatMs);

            if (_settings.LogLevel >= 1)
                Debug.Log($"[Hades MCP] Server running on {_transport.Endpoint}");
        }

        public void Stop()
        {
            if (!IsRunning && _transport == null) return;

            EditorApplication.update -= ProcessMainThreadQueue;
            EditorApplication.quitting -= Stop;

            _heartbeatActive = false;
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;

            _transport?.Stop();
            _reloadStrategy?.Dispose();
            _reloadStrategy = null;

            HubClient.Deregister(Core.PathSandbox.ProjectRoot, false);

            while (_workQueue != null && _workQueue.TryDequeue(out var item))
            {
                item.Completion.TrySetCanceled();
                AppNapGuard.Release();
            }

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

        // Runs on a background timer thread — must only touch cached values and HubClient's pure
        // file-I/O + HTTP helpers, never a Unity main-thread API.
        void HeartbeatTick(object state)
        {
            if (!_heartbeatActive) return;
            try
            {
                // Hub restarted (new PID/port in hub.json) — re-register against the new hub.
                var changed = HubClient.DetectHubChange();
                if (changed != null)
                {
                    var manifestPackages = HubClient.ReadManifestPackages(_projectRoot);
                    HubClient.Register(_projectName, _projectRoot, _heartbeatPort, _pid, manifestPackages);
                    return;
                }

                var ok = HubClient.Heartbeat(_projectRoot, _heartbeatPort, _pid);
                if (!ok && HubClient.IsHubRunning())
                {
                    // Hub is up but no longer knows us — we were evicted while the main thread was
                    // stalled or the machine asleep. Re-register so clients can find us again.
                    var manifestPackages = HubClient.ReadManifestPackages(_projectRoot);
                    HubClient.Register(_projectName, _projectRoot, _heartbeatPort, _pid, manifestPackages);
                }
            }
            catch { /* heartbeat is best-effort; never let it crash the timer thread */ }
        }

        Task<string> EnqueueAndWait(string json, string traceId)
        {
            // A long synchronous rebuild (or package scan) blocks the main thread, freezing
            // the queue processor below. Enqueuing here would just stall until the transport's
            // 30s timeout fires ("No Unity instance found"). Instead, answer immediately from
            // this background thread with an honest "busy" status. The volatile flag is the
            // only state we touch — no SQLite, which the blocked main thread may be mid-write.
            //
            // Gate only on a genuine long op — NOT the fast transient incremental Updating
            // state (Phase 9.6 Workstream A made incrementals O(changed), so they finish in
            // well under a frame). Gating Updating would return a spurious "busy" and open an
            // at-least-once retry window on non-idempotent writes that already applied.
            if (Graph.GraphBuilder.IsInLongOperation)
                return Task.FromResult(CreateBusyResponse(json));

            var tcs = new TaskCompletionSource<string>();
            // Hold a macOS App Nap opt-out for the lifetime of this in-flight request, so a
            // backgrounded editor's main-thread queue keeps draining. Released per item below.
            AppNapGuard.Acquire();
            _workQueue.Enqueue(new WorkItem(json, tcs, traceId));
            return tcs.Task;
        }

        string CreateBusyResponse(string json)
        {
            JToken id = null;
            try { id = JObject.Parse(json)["id"]; }
            catch { }

            var payload = new JObject
            {
                ["status"] = "busy",
                ["reason"] = "rebuild_in_progress",
                ["message"] = "Hades graph rebuild in progress; retry shortly."
            };
            var result = new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = payload.ToString(Newtonsoft.Json.Formatting.None)
                    }
                }
            };
            return CreateJsonRpcSuccess(id, result);
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
                finally
                {
                    AppNapGuard.Release();
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
