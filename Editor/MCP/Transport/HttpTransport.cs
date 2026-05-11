using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ArcForge.Hades.Editor.MCP
{
    public class HttpTransport : IMCPTransport
    {
        HttpListener _listener;
        CancellationTokenSource _cts;
        Func<string, Task<string>> _requestHandler;
        bool _running;
        int _port;
        int _connectedClients;

        public bool IsRunning => _running;
        public string Endpoint => _running ? $"http://127.0.0.1:{_port}/" : null;
        public int Port => _running ? _port : 0;
        public int ConnectedClients => _connectedClients;

        public void SetRequestHandler(Func<string, Task<string>> handler)
        {
            _requestHandler = handler;
        }

        public void Start(int port = 0)
        {
            if (_running) return;
            if (_requestHandler == null)
                throw new InvalidOperationException("Request handler must be set before starting transport.");

            _cts = new CancellationTokenSource();

            _port = port == 0 ? FindAvailablePort() : port;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
            }
            catch (HttpListenerException)
            {
                _port = FindAvailablePort();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
            }

            _running = true;
            Task.Run(() => ListenLoop(_cts.Token));
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _cts?.Cancel();

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }

        static int FindAvailablePort()
        {
            var tempListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tempListener.Start();
            var port = ((IPEndPoint)tempListener.LocalEndpoint).Port;
            tempListener.Stop();
            return port;
        }

        async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _running)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleContext(context, ct));
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex)
                {
                    if (_running)
                        Debug.LogError($"[Hades MCP] Listen error: {ex.Message}");
                }
            }
        }

        async Task HandleContext(HttpListenerContext context, CancellationToken ct)
        {
            var path = context.Request.Url.AbsolutePath;

            try
            {
                if (context.Request.HttpMethod == "POST" && path == "/rpc")
                    await HandleRPC(context);
                else if (context.Request.HttpMethod == "GET" && (path == "/sse" || path == "/rpc"))
                    await HandleSSE(context, ct);
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            catch (Exception)
            {
                try { context.Response.Close(); }
                catch { }
            }
        }

        async Task HandleRPC(HttpListenerContext context)
        {
            string json;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                json = await reader.ReadToEndAsync();

            var responseTask = _requestHandler(json);

            if (await Task.WhenAny(responseTask, Task.Delay(30000)) != responseTask)
            {
                WriteResponse(context.Response,
                    @"{""jsonrpc"":""2.0"",""id"":null,""error"":{""code"":-32000,""message"":""Timeout""}}");
                context.Response.Close();
                return;
            }

            var response = await responseTask;
            if (response != null)
                WriteResponse(context.Response, response);
            else
                context.Response.StatusCode = 204;

            context.Response.Close();
        }

        async Task HandleSSE(HttpListenerContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _connectedClients);

            try
            {
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.Add("Cache-Control", "no-cache");
                context.Response.Headers.Add("Connection", "keep-alive");
                context.Response.SendChunked = true;

                var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false));
                writer.AutoFlush = true;

                var rpcUrl = $"http://127.0.0.1:{_port}/rpc";
                await writer.WriteAsync($"event: endpoint\ndata: {rpcUrl}\n\n");
                await writer.FlushAsync();

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(30000, ct);
                    await writer.WriteAsync(":keepalive\n\n");
                    await writer.FlushAsync();
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally
            {
                Interlocked.Decrement(ref _connectedClients);
                try { context.Response.Close(); }
                catch { }
            }
        }

        static void WriteResponse(HttpListenerResponse response, string body)
        {
            response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
    }
}
