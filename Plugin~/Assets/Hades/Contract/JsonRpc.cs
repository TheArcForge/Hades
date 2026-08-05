// C# 9 only in this file - see the file banner in Wire/MiniJson.cs for why (Hades.Contract is
// compiled a second time inside Unity's Editor, which caps out at C# 9). Block-scoped namespace
// and ordinary mutable properties are deliberate here, not an oversight.
using System;

namespace Hades.Contract.Wire
{
    /// <summary>
    /// A JSON-RPC 2.0 request: expects a <see cref="JsonRpcResponse"/> correlated by <see cref="Id"/>.
    /// <see cref="Id"/> is whatever <see cref="JsonValue"/> the wire sent (JSON-RPC allows a string or
    /// a number) - callers that mint their own ids should prefer <see cref="JsonValue.Integer"/>.
    /// </summary>
    public sealed class JsonRpcRequest
    {
        public JsonValue? Id { get; set; }
        public string? Method { get; set; }
        public JsonValue? Params { get; set; }

        public JsonValue ToJson()
        {
            var obj = JsonValue.NewObject();
            obj.SetProperty("jsonrpc", JsonValue.String("2.0"));
            obj.SetProperty("id", Id ?? JsonValue.Null);
            obj.SetProperty("method", JsonValue.String(Method ?? string.Empty));
            if (Params is not null) obj.SetProperty("params", Params);
            return obj;
        }

        public static bool TryParse(JsonValue? json, out JsonRpcRequest? request, out string? error)
        {
            request = null;
            error = null;
            try
            {
                if (json is null || json.Kind != JsonValueKind.Object)
                {
                    error = "A request must be a JSON object.";
                    return false;
                }
                if (!json.TryGetProperty("method", out var methodValue) || methodValue!.Kind != JsonValueKind.String)
                {
                    error = "A request must have a string 'method'.";
                    return false;
                }
                if (!json.TryGetProperty("id", out var idValue))
                {
                    error = "A request must have an 'id' (use JsonRpcNotification if there is none).";
                    return false;
                }

                json.TryGetProperty("params", out var paramsValue); // optional

                request = new JsonRpcRequest { Id = idValue, Method = methodValue.AsString(), Params = paramsValue };
                return true;
            }
            catch (Exception e)
            {
                error = "Failed to parse request: " + e.Message;
                request = null;
                return false;
            }
        }

        public static bool TryParse(string? text, out JsonRpcRequest? request, out string? error)
        {
            if (!MiniJson.TryParse(text, out var json, out error))
            {
                request = null;
                return false;
            }
            return TryParse(json, out request, out error);
        }
    }

    /// <summary>A JSON-RPC 2.0 notification: identical to a request, minus the id, and no response is expected.</summary>
    public sealed class JsonRpcNotification
    {
        public string? Method { get; set; }
        public JsonValue? Params { get; set; }

        public JsonValue ToJson()
        {
            var obj = JsonValue.NewObject();
            obj.SetProperty("jsonrpc", JsonValue.String("2.0"));
            obj.SetProperty("method", JsonValue.String(Method ?? string.Empty));
            if (Params is not null) obj.SetProperty("params", Params);
            return obj;
        }

        public static bool TryParse(JsonValue? json, out JsonRpcNotification? notification, out string? error)
        {
            notification = null;
            error = null;
            try
            {
                if (json is null || json.Kind != JsonValueKind.Object)
                {
                    error = "A notification must be a JSON object.";
                    return false;
                }
                if (json.TryGetProperty("id", out _))
                {
                    error = "A notification must not have an 'id' (that shape is a request).";
                    return false;
                }
                if (!json.TryGetProperty("method", out var methodValue) || methodValue!.Kind != JsonValueKind.String)
                {
                    error = "A notification must have a string 'method'.";
                    return false;
                }

                json.TryGetProperty("params", out var paramsValue); // optional

                notification = new JsonRpcNotification { Method = methodValue.AsString(), Params = paramsValue };
                return true;
            }
            catch (Exception e)
            {
                error = "Failed to parse notification: " + e.Message;
                notification = null;
                return false;
            }
        }

        public static bool TryParse(string? text, out JsonRpcNotification? notification, out string? error)
        {
            if (!MiniJson.TryParse(text, out var json, out error))
            {
                notification = null;
                return false;
            }
            return TryParse(json, out notification, out error);
        }
    }

    /// <summary>The body of a JSON-RPC 2.0 error response's "error" member.</summary>
    public sealed class JsonRpcErrorInfo
    {
        public long Code { get; set; }
        public string? Message { get; set; }
        public JsonValue? Data { get; set; }
    }

    /// <summary>
    /// A JSON-RPC 2.0 response. On the wire this is one JSON object that carries either "result"
    /// or "error", never both - modeled here as one type with <see cref="IsError"/> as the
    /// discriminator, rather than two separate DTOs, so a caller handling "the reply to id X"
    /// only has one type to deal with.
    /// </summary>
    public sealed class JsonRpcResponse
    {
        public JsonValue? Id { get; set; }
        public JsonValue? Result { get; set; }
        public JsonRpcErrorInfo? Error { get; set; }

        public bool IsError => Error is not null;

        public static JsonRpcResponse Success(JsonValue id, JsonValue result) =>
            new JsonRpcResponse { Id = id, Result = result };

        public static JsonRpcResponse Failure(JsonValue id, long code, string message, JsonValue? data = null) =>
            new JsonRpcResponse { Id = id, Error = new JsonRpcErrorInfo { Code = code, Message = message, Data = data } };

        public JsonValue ToJson()
        {
            var obj = JsonValue.NewObject();
            obj.SetProperty("jsonrpc", JsonValue.String("2.0"));
            obj.SetProperty("id", Id ?? JsonValue.Null);

            if (IsError)
            {
                var err = JsonValue.NewObject();
                err.SetProperty("code", JsonValue.Integer(Error!.Code));
                err.SetProperty("message", JsonValue.String(Error.Message ?? string.Empty));
                if (Error.Data is not null) err.SetProperty("data", Error.Data);
                obj.SetProperty("error", err);
            }
            else
            {
                obj.SetProperty("result", Result ?? JsonValue.Null);
            }

            return obj;
        }

        public static bool TryParse(JsonValue? json, out JsonRpcResponse? response, out string? error)
        {
            response = null;
            error = null;
            try
            {
                if (json is null || json.Kind != JsonValueKind.Object)
                {
                    error = "A response must be a JSON object.";
                    return false;
                }
                if (!json.TryGetProperty("id", out var idValue))
                {
                    error = "A response must have an 'id'.";
                    return false;
                }

                var hasError = json.TryGetProperty("error", out var errorValue);
                if (hasError)
                {
                    if (errorValue!.Kind != JsonValueKind.Object)
                    {
                        error = "'error' must be an object.";
                        return false;
                    }
                    if (!errorValue.TryGetProperty("code", out var codeValue) || codeValue!.Kind != JsonValueKind.Integer)
                    {
                        error = "'error.code' must be an integer.";
                        return false;
                    }
                    if (!errorValue.TryGetProperty("message", out var messageValue) || messageValue!.Kind != JsonValueKind.String)
                    {
                        error = "'error.message' must be a string.";
                        return false;
                    }
                    errorValue.TryGetProperty("data", out var dataValue); // optional

                    response = new JsonRpcResponse
                    {
                        Id = idValue,
                        Error = new JsonRpcErrorInfo { Code = codeValue.AsInteger(), Message = messageValue.AsString(), Data = dataValue }
                    };
                    return true;
                }

                if (json.TryGetProperty("result", out var resultValue))
                {
                    response = new JsonRpcResponse { Id = idValue, Result = resultValue };
                    return true;
                }

                error = "A response must have either 'result' or 'error'.";
                return false;
            }
            catch (Exception e)
            {
                error = "Failed to parse response: " + e.Message;
                response = null;
                return false;
            }
        }

        public static bool TryParse(string? text, out JsonRpcResponse? response, out string? error)
        {
            if (!MiniJson.TryParse(text, out var json, out error))
            {
                response = null;
                return false;
            }
            return TryParse(json, out response, out error);
        }
    }
}
