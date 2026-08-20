// C# 9 only in this file - see the file banner in Wire/MiniJson.cs for why (Hades.Contract is
// compiled a second time inside Unity's Editor, which caps out at C# 9). Block-scoped namespace
// and ordinary mutable properties are deliberate here, not an oversight.
using System;

namespace Hades.Contract.Wire
{
    /// <summary>
    /// What the app writes to its editor-token file on every listener <c>Start()</c>, and what a
    /// Unity Editor plugin reads on connect (and on every reconnect attempt, in case the app
    /// restarted with a fresh port and token) to learn where to dial in and what to present.
    ///
    /// Not a wire message - this is never sent over the socket. It is local-disk rendezvous data:
    /// the listener binds an OS-assigned ephemeral port, so nothing else tells the plugin which
    /// port to use, and the file both sides already agree on for the token is the obvious place
    /// to also carry it. Same codec as the wire messages (<see cref="MiniJson"/>), because a
    /// second hand-rolled text format for the same two pieces of data would be the exact kind of
    /// duplication this shared contract exists to avoid.
    /// </summary>
    public sealed class EditorConnectionInfo
    {
        public int Port { get; set; }
        public string? Token { get; set; }

        public JsonValue ToJson()
        {
            var obj = JsonValue.NewObject();
            obj.SetProperty("port", JsonValue.Integer(Port));
            obj.SetProperty("token", JsonValue.String(Token ?? string.Empty));
            return obj;
        }

        public static bool TryParse(JsonValue? json, out EditorConnectionInfo? info, out string? error)
        {
            info = null;
            error = null;
            try
            {
                if (json is null || json.Kind != JsonValueKind.Object)
                {
                    error = "Editor connection info must be a JSON object.";
                    return false;
                }

                if (!json.TryGetProperty("port", out var portValue) || portValue!.Kind != JsonValueKind.Integer)
                {
                    error = "Editor connection info must have an integer 'port'.";
                    return false;
                }

                // A TCP port is only ever valid in [1, 65535]. AsInteger() returns the full JSON
                // integer range as a long, and a plain (int) cast truncates rather than
                // range-checks once a value falls outside int's own range - silently wrapping a
                // corrupt file or a hostile/skewed peer's out-of-range port into some other,
                // unrelated port number instead of failing to parse.
                var port = portValue.AsInteger();
                if (port < 1 || port > 65535)
                {
                    error = $"Editor connection info 'port' must be between 1 and 65535, got {port}.";
                    return false;
                }

                if (!json.TryGetProperty("token", out var tokenValue) || tokenValue!.Kind != JsonValueKind.String)
                {
                    error = "Editor connection info must have a string 'token'.";
                    return false;
                }

                info = new EditorConnectionInfo { Port = (int)port, Token = tokenValue.AsString() };
                return true;
            }
            catch (Exception e)
            {
                error = "Failed to parse editor connection info: " + e.Message;
                info = null;
                return false;
            }
        }

        public static bool TryParse(string? text, out EditorConnectionInfo? info, out string? error)
        {
            if (!MiniJson.TryParse(text, out var json, out error))
            {
                info = null;
                return false;
            }
            return TryParse(json, out info, out error);
        }
    }
}
