using System;
using System.Text;
using Hades.Contract.Wire;

namespace Hades.Contract.Tests;

// ---------------------------------------------------------------------------------------
// Batch A: the JsonValue DOM itself - construction, primitive round trips, and exact
// wire formatting. Later batches in this file cover malformed input, long-vs-double
// precision, nesting depth, and the JsonRpc/Hello DTOs built on top of this codec.
// ---------------------------------------------------------------------------------------
public class MiniJsonTests
{
    [Fact]
    public void Null_RoundTrips()
    {
        Assert.Equal("null", MiniJson.Write(JsonValue.Null));

        Assert.True(MiniJson.TryParse("null", out var value, out var error));
        Assert.Null(error);
        Assert.Equal(JsonValueKind.Null, value!.Kind);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void Boolean_RoundTrips(bool input, string expectedWire)
    {
        var written = MiniJson.Write(JsonValue.Bool(input));
        Assert.Equal(expectedWire, written);

        Assert.True(MiniJson.TryParse(written, out var value, out _));
        Assert.Equal(JsonValueKind.Boolean, value!.Kind);
        Assert.Equal(input, value.AsBoolean());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(42L)]
    [InlineData(-17L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Integer_RoundTrips(long input)
    {
        var written = MiniJson.Write(JsonValue.Integer(input));

        Assert.True(MiniJson.TryParse(written, out var value, out var error));
        Assert.Null(error);
        Assert.Equal(JsonValueKind.Integer, value!.Kind);
        Assert.Equal(input, value.AsInteger());
    }

    [Theory]
    [InlineData(3.14)]
    [InlineData(-0.5)]
    [InlineData(1.0)]
    [InlineData(100.0)]
    public void Float_RoundTrips(double input)
    {
        var written = MiniJson.Write(JsonValue.Float(input));

        Assert.True(MiniJson.TryParse(written, out var value, out var error));
        Assert.Null(error);
        Assert.Equal(input, value!.AsDouble());
    }

    [Fact]
    public void Float_AlwaysWritesWithADecimalPointOrExponent_SoItNeverReparsesAsInteger()
    {
        // 100.0 has no fractional digits once formatted ("100"), but it must still come back
        // as Kind.Float on the far side - otherwise a component value that happens to be a
        // whole number silently changes JSON type across the wire.
        var written = MiniJson.Write(JsonValue.Float(100.0));

        Assert.True(written.Contains('.') || written.Contains('e') || written.Contains('E'),
            $"expected a float-looking token, got '{written}'");

        Assert.True(MiniJson.TryParse(written, out var value, out _));
        Assert.Equal(JsonValueKind.Float, value!.Kind);
        Assert.Equal(100.0, value.AsDouble());
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("has \"quotes\"")]
    [InlineData(@"has \backslashes\")]
    [InlineData("line1\nline2")]
    [InlineData("tab\there")]
    [InlineData("carriage\rreturn")]
    [InlineData("bell\x07form\x0Cfeed")]
    [InlineData("nul\u0000byte")]
    [InlineData("unit\u0001separator")]
    [InlineData("Unity project path: C:\\Users\\mike\\My Game (2024)")]
    [InlineData("non-ascii: héllo wörld, 日本語, ключ")]
    [InlineData("emoji: \U0001F600")]
    [InlineData("  leading and trailing spaces  ")]
    public void String_RoundTripsExactly(string input)
    {
        var written = MiniJson.Write(JsonValue.String(input));

        Assert.True(MiniJson.TryParse(written, out var value, out var error));
        Assert.Null(error);
        Assert.Equal(JsonValueKind.String, value!.Kind);
        Assert.Equal(input, value.AsString());
    }

    [Fact]
    public void String_ControlCharactersAreEscapedOnTheWire()
    {
        // Not just "it round-trips" - the raw control byte must never appear literally in the
        // wire text, because the wire framing is newline-delimited. A literal 0x0A in the
        // output would split one message into two lines.
        var written = MiniJson.Write(JsonValue.String("a\nb"));

        Assert.DoesNotContain('\n', written);
        Assert.Contains("\\n", written);
    }

    [Fact]
    public void Array_RoundTrips()
    {
        var array = JsonValue.NewArray()
            .Add(JsonValue.Integer(1))
            .Add(JsonValue.String("two"))
            .Add(JsonValue.Bool(true))
            .Add(JsonValue.Null);

        var written = MiniJson.Write(array);
        Assert.Equal("[1,\"two\",true,null]", written);

        Assert.True(MiniJson.TryParse(written, out var value, out _));
        Assert.Equal(JsonValueKind.Array, value!.Kind);
        Assert.Equal(4, value.Items.Count);
        Assert.Equal(1L, value.Items[0].AsInteger());
        Assert.Equal("two", value.Items[1].AsString());
        Assert.True(value.Items[2].AsBoolean());
        Assert.Equal(JsonValueKind.Null, value.Items[3].Kind);
    }

    [Fact]
    public void Object_RoundTrips_AndPreservesMemberOrder()
    {
        var obj = JsonValue.NewObject()
            .SetProperty("z", JsonValue.Integer(1))
            .SetProperty("a", JsonValue.Integer(2));

        var written = MiniJson.Write(obj);
        Assert.Equal("{\"z\":1,\"a\":2}", written);

        Assert.True(MiniJson.TryParse(written, out var value, out _));
        Assert.Equal(JsonValueKind.Object, value!.Kind);
        Assert.True(value.TryGetProperty("a", out var a));
        Assert.Equal(2L, a!.AsInteger());
        Assert.True(value.TryGetProperty("z", out var z));
        Assert.Equal(1L, z!.AsInteger());
        Assert.False(value.TryGetProperty("missing", out _));
    }

    [Fact]
    public void NestedObjectsAndArrays_RoundTrip()
    {
        var nested = JsonValue.NewObject()
            .SetProperty("name", JsonValue.String("Player"))
            .SetProperty("tags", JsonValue.NewArray().Add(JsonValue.String("a")).Add(JsonValue.String("b")))
            .SetProperty("transform", JsonValue.NewObject()
                .SetProperty("x", JsonValue.Float(1.5))
                .SetProperty("y", JsonValue.Float(-2.25)));

        var written = MiniJson.Write(nested);
        Assert.True(MiniJson.TryParse(written, out var value, out var error));
        Assert.Null(error);
        Assert.Equal("Player", value!.TryGetProperty("name", out var name) ? name!.AsString() : null);
        Assert.True(value.TryGetProperty("tags", out var tags));
        Assert.Equal(2, tags!.Items.Count);
        Assert.True(value.TryGetProperty("transform", out var transform));
        Assert.True(transform!.TryGetProperty("x", out var x));
        Assert.Equal(1.5, x!.AsDouble());
    }

    // ---------------------------------------------------------------------------------------
    // Batch B: malformed input must never throw - this codec sits directly on a socket fed by
    // another process, and a truncated or hostile line must come back as a parse failure the
    // caller can inspect, not an exception that takes down the I/O thread.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TruncatedLine_NeverThrows_ReturnsFailure()
    {
        const string input = "{\"method\":\"tools/call\",\"params\":{\"name\":";

        var ex = Record.Exception(() => MiniJson.TryParse(input, out _, out _));
        Assert.Null(ex);

        Assert.False(MiniJson.TryParse(input, out var value, out var error));
        Assert.Null(value);
        Assert.NotNull(error);
    }

    [Fact]
    public void UnterminatedString_NeverThrows_ReturnsFailure()
    {
        const string input = "{\"method\": \"tools/call";

        var ex = Record.Exception(() => MiniJson.TryParse(input, out _, out _));
        Assert.Null(ex);

        Assert.False(MiniJson.TryParse(input, out var value, out var error));
        Assert.Null(value);
        Assert.NotNull(error);
    }

    [Fact]
    public void BareOpenBrace_NeverThrows_ReturnsFailure()
    {
        const string input = "{";

        var ex = Record.Exception(() => MiniJson.TryParse(input, out _, out _));
        Assert.Null(ex);

        Assert.False(MiniJson.TryParse(input, out var value, out var error));
        Assert.Null(value);
        Assert.NotNull(error);
    }

    [Fact]
    public void InvalidUtf8Bytes_NeverThrows_ReturnsFailure()
    {
        // 0xC3 announces a 2-byte UTF-8 sequence, but 0x28 ('(') is not a valid continuation
        // byte, so this is not decodable as UTF-8 at all - this has to be tested at the byte
        // level, since a .NET string cannot itself hold invalid UTF-8.
        var invalidUtf8 = new byte[] { 0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D }; // {"a":"<invalid>"}

        var ex = Record.Exception(() => MiniJson.TryParse(invalidUtf8, out _, out _));
        Assert.Null(ex);

        Assert.False(MiniJson.TryParse(invalidUtf8, out var value, out var error));
        Assert.Null(value);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidUtf8Bytes_ParseNormally()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"path\":\"日本語 プロジェクト\"}");

        Assert.True(MiniJson.TryParse(bytes, out var value, out var error));
        Assert.Null(error);
        Assert.True(value!.TryGetProperty("path", out var path));
        Assert.Equal("日本語 プロジェクト", path!.AsString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[")]
    [InlineData("{\"a\":")]
    [InlineData("{\"a\": \"unterminated")]
    [InlineData("[1,2,")]
    [InlineData("[1,2")]
    [InlineData("{\"a\":1")]
    [InlineData("nul")]
    [InlineData("truX")]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1,]")]
    [InlineData("{,}")]
    [InlineData("\"abc")]
    [InlineData("{\"a\" 1}")]
    [InlineData("123abc")]
    [InlineData("{} extra")]
    [InlineData("{\"a\":1} {\"b\":2}")]
    [InlineData("-")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData("1e")]
    [InlineData("{\"a\":\x01\"}")]
    public void MalformedInput_NeverThrows_ReturnsInspectableFailure(string input)
    {
        var ex = Record.Exception(() => MiniJson.TryParse(input, out _, out _));
        Assert.Null(ex);

        Assert.False(MiniJson.TryParse(input, out var value, out var error));
        Assert.Null(value);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void NullInput_NeverThrows_ReturnsFailure()
    {
        Assert.False(MiniJson.TryParse((string?)null, out var value, out var error));
        Assert.Null(value);
        Assert.NotNull(error);

        Assert.False(MiniJson.TryParse((byte[]?)null, out var byteValue, out var byteError));
        Assert.Null(byteValue);
        Assert.NotNull(byteError);
    }

    // ---------------------------------------------------------------------------------------
    // Long-vs-double precision: the single most likely defect in a hand-rolled JSON codec.
    // Unity file IDs are `long` and routinely exceed the ~15-17 significant decimal digits a
    // `double` can represent exactly.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void RealUnityFileId_RoundTripsWithoutPrecisionLoss()
    {
        const long fileId = 8218514011760283993L;

        var written = MiniJson.Write(JsonValue.Integer(fileId));
        Assert.Equal("8218514011760283993", written);

        Assert.True(MiniJson.TryParse(written, out var value, out var error));
        Assert.Null(error);
        Assert.Equal(JsonValueKind.Integer, value!.Kind);
        Assert.Equal(fileId, value.AsInteger());
    }

    [Fact]
    public void RealUnityFileId_SurvivesEmbeddedInObjectRoundTrip()
    {
        const long fileId = 8218514011760283993L;
        var obj = JsonValue.NewObject()
            .SetProperty("fileID", JsonValue.Integer(fileId))
            .SetProperty("guid", JsonValue.String("aaaabbbbccccddddeeeeffff00001111"));

        var written = MiniJson.Write(obj);

        Assert.True(MiniJson.TryParse(written, out var value, out var error));
        Assert.Null(error);
        Assert.True(value!.TryGetProperty("fileID", out var fileIdValue));
        Assert.Equal(fileId, fileIdValue!.AsInteger());
    }

    [Fact]
    public void DoubleWouldHaveCorruptedThisFileId_DemonstratingWhyIntegerKindExists()
    {
        // This is the failure mode the codec exists to avoid: routing the same file ID through
        // a double loses precision, and it would look fine for small numbers in a quick test.
        const long fileId = 8218514011760283993L;
        double asDouble = fileId;
        var roundTripped = (long)asDouble;
        Assert.NotEqual(fileId, roundTripped);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void LongBoundaryValues_RoundTripExactly(long input)
    {
        var written = MiniJson.Write(JsonValue.Integer(input));
        Assert.True(MiniJson.TryParse(written, out var value, out var error));
        Assert.Null(error);
        Assert.Equal(input, value!.AsInteger());
    }

    [Fact]
    public void IntegerToken_TooLargeForLong_FallsBackToDoubleInsteadOfFailing()
    {
        // One digit past long.MaxValue (9223372036854775807). Not a case the task requires to
        // survive exactly - it is explicitly out of long's range - but the parser should
        // degrade to a double rather than reporting a parse failure.
        const string token = "99999999999999999999";

        Assert.True(MiniJson.TryParse(token, out var value, out var error));
        Assert.Null(error);
        Assert.Equal(JsonValueKind.Float, value!.Kind);
    }

    // ---------------------------------------------------------------------------------------
    // Nesting depth: a hostile or corrupt peer must not be able to crash the Editor by growing
    // the parser's native call stack without bound.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void NestingAtMaxDepth_ParsesSuccessfully()
    {
        var input = new string('[', MiniJson.MaxDepth) + new string(']', MiniJson.MaxDepth);

        Assert.True(MiniJson.TryParse(input, out var value, out var error));
        Assert.Null(error);
        Assert.Equal(JsonValueKind.Array, value!.Kind);
    }

    [Fact]
    public void NestingOnePastMaxDepth_FailsGracefully_NotAnException()
    {
        var input = new string('[', MiniJson.MaxDepth + 1) + new string(']', MiniJson.MaxDepth + 1);

        var ex = Record.Exception(() => MiniJson.TryParse(input, out _, out _));
        Assert.Null(ex);

        Assert.False(MiniJson.TryParse(input, out var value, out var error));
        Assert.Null(value);
        Assert.NotNull(error);
    }

    [Fact]
    public void ExtremeNesting_FailsFastInsteadOfOverflowingTheStack()
    {
        // Far beyond MaxDepth, and deliberately missing every closing bracket. If depth were
        // not checked before recursing, this would either overflow the native call stack
        // (crashing the process - not catchable by any try/catch) or hang scanning input.
        // Bailing out at MaxDepth means only ~64 stack frames are ever used, regardless of how
        // deep the hostile input claims to go.
        var input = new string('[', 100_000);

        var ex = Record.Exception(() => MiniJson.TryParse(input, out _, out _));
        Assert.Null(ex);

        Assert.False(MiniJson.TryParse(input, out var value, out var error));
        Assert.Null(value);
        Assert.NotNull(error);
    }

    // ---------------------------------------------------------------------------------------
    // Batch C: the JSON-RPC DTOs built on top of the JsonValue codec above - request,
    // notification, and response (success and error are both `JsonRpcResponse`, since on the
    // wire a response is one JSON object that is either-or, never both).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Request_RoundTrips_ThroughJsonValue()
    {
        var request = new JsonRpcRequest
        {
            Id = JsonValue.Integer(1),
            Method = "tools/call",
            Params = JsonValue.NewObject().SetProperty("name", JsonValue.String("scene_get_hierarchy"))
        };

        var json = request.ToJson();
        Assert.True(JsonRpcRequest.TryParse(json, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal("tools/call", parsed!.Method);
        Assert.Equal(1L, parsed.Id!.AsInteger());
        Assert.True(parsed.Params!.TryGetProperty("name", out var name));
        Assert.Equal("scene_get_hierarchy", name!.AsString());
    }

    [Fact]
    public void Request_RoundTrips_ThroughWireText()
    {
        var request = new JsonRpcRequest { Id = JsonValue.String("req-42"), Method = "ping", Params = null };

        var text = MiniJson.Write(request.ToJson());
        Assert.True(JsonRpcRequest.TryParse(text, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal("ping", parsed!.Method);
        Assert.Equal("req-42", parsed.Id!.AsString());
    }

    [Fact]
    public void Request_Wire_HasNoNewlines_SoLineFramingSurvives()
    {
        var request = new JsonRpcRequest
        {
            Id = JsonValue.Integer(1),
            Method = "project/open",
            Params = JsonValue.NewObject().SetProperty("path", JsonValue.String("C:\\Projects\\My Game\\Assets"))
        };

        var text = MiniJson.Write(request.ToJson());
        Assert.DoesNotContain('\n', text);
    }

    [Theory]
    [InlineData("not json at all {{{")]
    [InlineData("{}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":123,\"id\":1}")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    public void Request_TryParse_NeverThrows_RejectsMalformedOrWrongShapedInput(string input)
    {
        var ex = Record.Exception(() => JsonRpcRequest.TryParse(input, out _, out _));
        Assert.Null(ex);

        Assert.False(JsonRpcRequest.TryParse(input, out var request, out var error));
        Assert.Null(request);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Notification_RoundTrips_AndHasNoId()
    {
        var notification = new JsonRpcNotification
        {
            Method = "editor/log",
            Params = JsonValue.NewObject().SetProperty("message", JsonValue.String("Recompiled"))
        };

        var json = notification.ToJson();
        Assert.False(json.TryGetProperty("id", out _));

        Assert.True(JsonRpcNotification.TryParse(json, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal("editor/log", parsed!.Method);
        Assert.True(parsed.Params!.TryGetProperty("message", out var message));
        Assert.Equal("Recompiled", message!.AsString());
    }

    [Fact]
    public void Notification_RoundTrips_ThroughWireText()
    {
        var notification = new JsonRpcNotification { Method = "editor/heartbeat", Params = null };

        var text = MiniJson.Write(notification.ToJson());
        Assert.True(JsonRpcNotification.TryParse(text, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal("editor/heartbeat", parsed!.Method);
    }

    [Fact]
    public void Notification_TryParse_RejectsInputThatHasAnId_BecauseThatIsARequest()
    {
        const string requestShapedText = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\"}";

        Assert.False(JsonRpcNotification.TryParse(requestShapedText, out var notification, out var error));
        Assert.Null(notification);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Response_Success_RoundTrips()
    {
        var response = JsonRpcResponse.Success(JsonValue.Integer(7), JsonValue.String("ok"));

        var text = MiniJson.Write(response.ToJson());
        Assert.True(JsonRpcResponse.TryParse(text, out var parsed, out var error));
        Assert.Null(error);
        Assert.False(parsed!.IsError);
        Assert.Equal(7L, parsed.Id!.AsInteger());
        Assert.Equal("ok", parsed.Result!.AsString());
    }

    [Fact]
    public void Response_Error_RoundTrips_WithData()
    {
        var response = JsonRpcResponse.Failure(JsonValue.Integer(7), -32602, "Invalid params",
            JsonValue.NewObject().SetProperty("field", JsonValue.String("path")));

        var text = MiniJson.Write(response.ToJson());
        Assert.True(JsonRpcResponse.TryParse(text, out var parsed, out var error));
        Assert.Null(error);
        Assert.True(parsed!.IsError);
        Assert.Equal(7L, parsed.Id!.AsInteger());
        Assert.Equal(-32602, parsed.Error!.Code);
        Assert.Equal("Invalid params", parsed.Error.Message);
        Assert.True(parsed.Error.Data!.TryGetProperty("field", out var field));
        Assert.Equal("path", field!.AsString());
    }

    [Fact]
    public void Response_Error_RoundTrips_WithoutData()
    {
        var response = JsonRpcResponse.Failure(JsonValue.String("req-1"), -32700, "Parse error");

        var text = MiniJson.Write(response.ToJson());
        Assert.True(JsonRpcResponse.TryParse(text, out var parsed, out var error));
        Assert.Null(error);
        Assert.True(parsed!.IsError);
        Assert.Equal(-32700, parsed.Error!.Code);
        Assert.Null(parsed.Error.Data);
    }

    [Theory]
    [InlineData("not json {{{")]
    [InlineData("{}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"message\":\"missing code\"}}")]
    [InlineData("42")]
    public void Response_TryParse_NeverThrows_RejectsMalformedOrWrongShapedInput(string input)
    {
        var ex = Record.Exception(() => JsonRpcResponse.TryParse(input, out _, out _));
        Assert.Null(ex);

        Assert.False(JsonRpcResponse.TryParse(input, out var response, out var error));
        Assert.Null(response);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Response_RealUnityFileId_SurvivesInResult()
    {
        const long fileId = 8218514011760283993L;
        var response = JsonRpcResponse.Success(JsonValue.Integer(1),
            JsonValue.NewObject().SetProperty("fileID", JsonValue.Integer(fileId)));

        var text = MiniJson.Write(response.ToJson());
        Assert.True(JsonRpcResponse.TryParse(text, out var parsed, out _));
        Assert.True(parsed!.Result!.TryGetProperty("fileID", out var fileIdValue));
        Assert.Equal(fileId, fileIdValue!.AsInteger());
    }

    // ---------------------------------------------------------------------------------------
    // Batch D: Hello, the handshake payload a Unity plugin sends when it connects. Round-tripped
    // as its own standalone wire shape - not wrapped in a JSON-RPC envelope - since Task 1 is
    // only the data contract, not the handshake protocol itself.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Hello_RoundTrips_ThroughJsonValue()
    {
        var hello = new Hello
        {
            ProjectGuid = "aaaabbbbccccddddeeeeffff00001111",
            ProjectPath = "/Users/mike/Projects/My Game (2024)",
            UnityVersion = "6000.3.1f1",
            PluginVersion = "0.1.0",
            ProcessId = 54321
        };

        var json = hello.ToJson();
        Assert.True(Hello.TryParse(json, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(hello.ProjectGuid, parsed!.ProjectGuid);
        Assert.Equal(hello.ProjectPath, parsed.ProjectPath);
        Assert.Equal(hello.UnityVersion, parsed.UnityVersion);
        Assert.Equal(hello.PluginVersion, parsed.PluginVersion);
        Assert.Equal(hello.ProcessId, parsed.ProcessId);
    }

    [Fact]
    public void Hello_RoundTrips_ThroughWireText()
    {
        var hello = new Hello
        {
            ProjectGuid = "00000000000000000000000000000000",
            ProjectPath = "C:\\Users\\mike\\Unity Projects\\日本語プロジェクト",
            UnityVersion = "6000.3.1f1",
            PluginVersion = "0.1.0",
            ProcessId = 1
        };

        var text = MiniJson.Write(hello.ToJson());
        Assert.DoesNotContain('\n', text);

        Assert.True(Hello.TryParse(text, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(hello.ProjectPath, parsed!.ProjectPath);
    }

    [Fact]
    public void Hello_ProcessId_RoundTripsExactly()
    {
        var hello = new Hello
        {
            ProjectGuid = "aaaabbbbccccddddeeeeffff00001111",
            ProjectPath = "/tmp/project",
            UnityVersion = "6000.3.1f1",
            PluginVersion = "0.1.0",
            ProcessId = int.MaxValue
        };

        var text = MiniJson.Write(hello.ToJson());
        Assert.True(Hello.TryParse(text, out var parsed, out _));
        Assert.Equal(int.MaxValue, parsed!.ProcessId);
    }

    [Theory]
    [InlineData("not json {{{")]
    [InlineData("{}")]
    [InlineData("{\"projectGuid\":\"abc\"}")]
    [InlineData("{\"projectGuid\":\"abc\",\"projectPath\":\"/tmp\",\"unityVersion\":\"6000.3.1f1\",\"pluginVersion\":\"0.1.0\",\"processId\":\"not-a-number\"}")]
    [InlineData("42")]
    public void Hello_TryParse_NeverThrows_RejectsMalformedOrWrongShapedInput(string input)
    {
        var ex = Record.Exception(() => Hello.TryParse(input, out _, out _));
        Assert.Null(ex);

        Assert.False(Hello.TryParse(input, out var hello, out var error));
        Assert.Null(hello);
        Assert.False(string.IsNullOrEmpty(error));
    }

    // ---------------------------------------------------------------------------------------
    // Batch E: EditorConnectionInfo - the token+port rendezvous file both sides read
    // (Wire/EditorConnectionInfo.cs), same "round trip + malformed input rejected" scope as
    // Hello above. B-F4: 'port' is only ever valid in [1, 65535] - TryParse used to cast the
    // parsed long straight to int, which silently wraps (rather than range-checks) once it is
    // out of int's range - so an out-of-range 'port' must be a parse failure, never a truncated
    // value.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void EditorConnectionInfo_RoundTrips_ThroughWireText()
    {
        var info = new EditorConnectionInfo { Port = 54321, Token = "abc123token" };

        var text = MiniJson.Write(info.ToJson());
        Assert.True(EditorConnectionInfo.TryParse(text, out var parsed, out var error), error);
        Assert.Equal(54321, parsed!.Port);
        Assert.Equal("abc123token", parsed.Token);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    [InlineData(54321)]
    public void EditorConnectionInfo_PortWithinValidRange_Parses(int port)
    {
        var json = JsonValue.NewObject()
            .SetProperty("port", JsonValue.Integer(port))
            .SetProperty("token", JsonValue.String("t"));

        Assert.True(EditorConnectionInfo.TryParse(json, out var info, out var error), error);
        Assert.Equal(port, info!.Port);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(65536L)]
    [InlineData(70000L)]
    [InlineData(4294967296L)]  // 2^32 - would wrap to 0 under an unchecked (int) cast
    [InlineData(4295032831L)]  // 2^32 + 65535 - would wrap to a value that LOOKS valid (65535) if truncated
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void EditorConnectionInfo_PortOutOfRange_FailsToParse_RatherThanTruncating(long port)
    {
        var json = JsonValue.NewObject()
            .SetProperty("port", JsonValue.Integer(port))
            .SetProperty("token", JsonValue.String("t"));

        var ex = Record.Exception(() => EditorConnectionInfo.TryParse(json, out _, out _));
        Assert.Null(ex);

        Assert.False(EditorConnectionInfo.TryParse(json, out var info, out var error));
        Assert.Null(info);
        Assert.False(string.IsNullOrEmpty(error));
    }

    // ---------------------------------------------------------------------------------------
    // Batch F: unpaired UTF-16 surrogate \u escapes (B-F3). \uD800 (a lone high surrogate) or
    // \uDC00 (a lone low surrogate) decodes into an ill-formed .NET string that this codec
    // accepted silently before this fix - a strict downstream consumer (System.Text.Json)
    // throws on it later, far from this parse call. Rejected at parse time instead, the same
    // "malformed escape" treatment every other bad \u escape already gets (see
    // MalformedInput_NeverThrows_ReturnsInspectableFailure above) - a well-formed PAIR must keep
    // parsing exactly as it always did.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\\uD800")]        // lone high surrogate, nothing follows
    [InlineData("\\uDBFF")]        // lone high surrogate, top of the high range
    [InlineData("\\uD800x")]       // lone high surrogate followed by ordinary text, not a \u escape
    [InlineData("\\uD800\\n")]     // lone high surrogate followed by a DIFFERENT (non-u) escape
    [InlineData("\\uDC00")]        // lone low surrogate
    [InlineData("\\uDFFF")]        // lone low surrogate, top of the low range
    [InlineData("\\uDC00\\uDC00")] // two low surrogates - the first is unpaired regardless of what follows
    public void UnpairedSurrogateEscape_FailsToParse_InsteadOfProducingAnIllFormedString(string escape)
    {
        var input = "\"" + escape + "\"";

        var ex = Record.Exception(() => MiniJson.TryParse(input, out _, out _));
        Assert.Null(ex);

        Assert.False(MiniJson.TryParse(input, out var value, out var error));
        Assert.Null(value);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void PairedSurrogateEscape_StillParsesToTheCorrectCharacter()
    {
        // 😀 is the UTF-16 surrogate pair for U+1F600 (grinning face) - a well-formed
        // pair must keep parsing exactly as it always did; only a LONE half is newly rejected.
        const string input = "\"\\uD83D\\uDE00\"";

        Assert.True(MiniJson.TryParse(input, out var value, out var error));
        Assert.Null(error);
        Assert.Equal("\U0001F600", value!.AsString());
    }
}
