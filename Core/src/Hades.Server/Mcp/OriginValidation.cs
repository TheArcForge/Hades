namespace Hades.Server.Mcp;

/// <summary>
/// MCP transport spec: "Servers MUST validate the Origin header on all incoming connections
/// to prevent DNS rebinding attacks. If the Origin header is present and invalid, servers
/// MUST respond with HTTP 403 Forbidden."
///
/// The MCP SDK does not do this — a request carrying Origin: https://evil.example.com is
/// answered 200 without this middleware (verified against ModelContextProtocol.AspNetCore
/// 2.0.0). Since Hades binds loopback and holds a graph of the user's source code, a web page
/// silently driving it via DNS rebinding is the threat this closes.
///
/// An ABSENT Origin is allowed: the spec conditions rejection on the header being "present and
/// invalid", and non-browser clients such as Claude Code do not send one.
/// </summary>
public static class OriginValidation
{
    public static bool IsAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    public static IApplicationBuilder UseMcpOriginValidation(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!IsAllowed(context.Request.Headers.Origin.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    error = new { code = -32600, message = "Origin not allowed" },
                });
                return;
            }

            await next();
        });
}
