using System.Net;
using System.Text;
using System.Text.Json;

// FakeCore - a minimal stand-in for Hades.Server, used only by Hades.Supervision.Tests. It speaks
// just enough of the control API (token-checked `GET /control/ping`) to exercise
// CoreSupervisor's adopt/spawn/restart logic against a REAL child process and a REAL discovery
// file, without depending on a full ASP.NET host being installed or fast to cold-start in whatever
// environment runs `dotnet test`. It is not part of the product's build output - internal
// plumbing for tests only, the same role Mac/HadesSupervision/Sources/FakeCore/main.swift plays
// for the Swift supervisor's own tests.
//
// It must never reference Hades.Core, Hades.Server, or Hades.Control.Client (see
// Windows/Directory.Build.props' EnsureShellIsAClient target, which fails the build if it does):
// this is a black-box stand-in for the real core, not a client of it, so it can only drift from
// the real product in ways a test would actually catch.

var root = args.Length > 0 ? args[0] : throw new ArgumentException("usage: FakeCore <app-data-root> [flags]");
Directory.CreateDirectory(root);

var dieAfterMs = ReadIntFlag(args, "--die-after-ms");
var neverAnswer = args.Contains("--never-answer");
var exitCode = ReadIntFlag(args, "--exit-code");

if (exitCode is { } code)
{
    Environment.Exit(code);
}

var token = Guid.NewGuid().ToString("N");

var listener = new HttpListener();
// Port 0 asks the OS for a free port; HttpListener does not support that directly, so probe with
// a Socket first the same way ASP.NET's Kestrel does under the hood.
var port = GetFreeTcpPort();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();

var tokenFilePath = Path.Combine(root, "control.token");
var tokenJson = JsonSerializer.Serialize(new { port, token });
File.WriteAllText(tokenFilePath, tokenJson);

Console.WriteLine(port);

if (dieAfterMs is { } delay)
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(delay);
        Environment.Exit(0);
    });
}

while (true)
{
    var context = listener.GetContext();
    if (neverAnswer)
    {
        // Accept the connection but never write a response, so the caller hangs until it times
        // out on its own - exercising ping-timeout handling instead of a real hang.
        continue;
    }

    HandleRequest(context, token);
}

static void HandleRequest(HttpListenerContext context, string token)
{
    var request = context.Request;
    var response = context.Response;

    var authHeader = request.Headers["Authorization"];
    var authorized = authHeader == $"Bearer {token}";

    string body;
    if (!authorized)
    {
        response.StatusCode = (int)HttpStatusCode.Unauthorized;
        body = """{"error":"Missing or invalid token"}""";
    }
    else if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/control/ping")
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        body = """{"version":"fakecore-1.0","uptimeSeconds":0}""";
    }
    else
    {
        response.StatusCode = (int)HttpStatusCode.NotFound;
        body = """{"error":"not found"}""";
    }

    var bytes = Encoding.UTF8.GetBytes(body);
    response.ContentType = "application/json";
    response.ContentLength64 = bytes.Length;
    response.OutputStream.Write(bytes, 0, bytes.Length);
    response.OutputStream.Close();
}

static int? ReadIntFlag(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0 || index + 1 >= args.Length) return null;
    return int.Parse(args[index + 1]);
}

static int GetFreeTcpPort()
{
    var socket = new System.Net.Sockets.Socket(
        System.Net.Sockets.AddressFamily.InterNetwork,
        System.Net.Sockets.SocketType.Stream,
        System.Net.Sockets.ProtocolType.Tcp);
    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
    socket.Close();
    return port;
}
