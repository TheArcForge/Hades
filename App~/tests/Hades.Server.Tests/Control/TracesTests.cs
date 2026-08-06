using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Hades.Core.Tracing;
using Hades.Server.Control;

namespace Hades.Server.Tests.Control;

/// <summary>
/// Pure, deterministic tests of <see cref="TracesEndpoint.GroupIntoSequences"/> - the sequence-
/// grouping logic behind <c>GET /control/traces/sequences</c>, exercised directly against
/// hand-built <see cref="TraceRecordSnapshot"/> lists with literal millisecond timestamps, no
/// clock, no SQLite. Same "verbatim" discipline as SummaryTests.cs's own SummaryResolveTests: the
/// pattern string is a hand-typed literal, never rebuilt from the same response under test.
///
/// <b>What makes a sequence:</b> a maximal run of calls where each call starts within
/// <see cref="TracesEndpoint.DefaultSequenceGapMs"/> (30 seconds) of the latest call-end seen so
/// far in the run. A gap larger than that starts a new sequence. 30s is chosen as comfortably
/// longer than the pause between two tool calls in one agentic burst of activity (typically
/// sub-second to a few seconds) while still being short enough that a real context-switch - a
/// human stepping away, a new unrelated task starting - reliably starts a new one.
/// </summary>
public sealed class TracesGroupIntoSequencesTests
{
    static TraceRecordSnapshot Call(string traceId, string tool, long startUtcMs, long endUtcMs, string status = "ok") => new()
    {
        TraceId = traceId,
        Tool = tool,
        StartUtcMs = startUtcMs,
        EndUtcMs = endUtcMs,
        Status = status,
    };

    [Fact]
    public void NoTraces_IsAWellFormedEmptyList()
    {
        Assert.Empty(TracesEndpoint.GroupIntoSequences([]));
    }

    [Fact]
    public void SingleCall_IsItsOwnSequence_OfLengthOne()
    {
        var sequences = TracesEndpoint.GroupIntoSequences([Call("t1", "hades_status", 1000, 1050)]);

        var seq = Assert.Single(sequences);
        Assert.Equal(1, seq.CallCount);
        Assert.Equal("hades_status", seq.Pattern);
        Assert.Equal(["hades_status"], seq.Tools);
        Assert.Equal(["t1"], seq.TraceIds);
        Assert.Equal("t1", seq.Id);
        Assert.Equal(1000, seq.StartUtcMs);
        Assert.Equal(1050, seq.EndUtcMs);
        Assert.Equal(50, seq.DurationMs);
    }

    [Fact]
    public void CallsWithinTheGapThreshold_MergeIntoOneSequence_PatternIsArrowJoined()
    {
        var sequences = TracesEndpoint.GroupIntoSequences([
            Call("t1", "search_by_name", 0, 100),
            Call("t2", "find_references_to", 5_000, 5_200), // 4.8s after t1 ended - well inside 30s
            Call("t3", "read_file", 10_000, 10_050), // 4.8s after t2 ended
        ]);

        var seq = Assert.Single(sequences);
        Assert.Equal(3, seq.CallCount);
        Assert.Equal("search_by_name → find_references_to → read_file", seq.Pattern);
        Assert.Equal(["search_by_name", "find_references_to", "read_file"], seq.Tools);
        Assert.Equal(0, seq.StartUtcMs);
        Assert.Equal(10_050, seq.EndUtcMs);
    }

    [Fact]
    public void AGapLargerThanTheThreshold_StartsANewSequence()
    {
        var sequences = TracesEndpoint.GroupIntoSequences([
            Call("t1", "search_by_name", 0, 100),
            Call("t2", "read_file", 100 + 30_001, 100 + 30_001 + 50), // 30.001s idle - just over the 30s default
        ]);

        Assert.Equal(2, sequences.Count);
        Assert.Single(sequences[0].Tools, "search_by_name");
        Assert.Single(sequences[1].Tools, "read_file");
    }

    [Fact]
    public void AGapExactlyAtTheThreshold_StaysOneSequence_ThresholdIsInclusive()
    {
        var sequences = TracesEndpoint.GroupIntoSequences([
            Call("t1", "search_by_name", 0, 100),
            Call("t2", "read_file", 100 + 30_000, 100 + 30_000 + 50), // exactly 30.000s idle
        ]);

        Assert.Single(sequences);
    }

    [Fact]
    public void UnsortedInput_IsSortedByStartTimeBeforeGrouping()
    {
        // Callers (TracesEndpoint.GetSequences) fetch newest-first from TraceStore and must not
        // have to remember to reverse it correctly - GroupIntoSequences sorts its own input.
        var sequences = TracesEndpoint.GroupIntoSequences([
            Call("t2", "second", 5_000, 5_050),
            Call("t1", "first", 0, 100),
        ]);

        var seq = Assert.Single(sequences);
        Assert.Equal("first → second", seq.Pattern);
    }

    [Fact]
    public void AnyErrorInTheSequence_ResolvesTheWholeSequencesOutcomeToError()
    {
        var sequences = TracesEndpoint.GroupIntoSequences([
            Call("t1", "search_by_name", 0, 100, status: "ok"),
            Call("t2", "component_add", 200, 300, status: "error"),
            Call("t3", "read_file", 400, 500, status: "ok"),
        ]);

        Assert.Equal(TraceOutcome.Error, Assert.Single(sequences).Outcome);
    }

    [Fact]
    public void EveryCallOk_ResolvesTheSequencesOutcomeToOk()
    {
        var sequences = TracesEndpoint.GroupIntoSequences([
            Call("t1", "search_by_name", 0, 100, status: "ok"),
            Call("t2", "read_file", 200, 300, status: "ok"),
        ]);

        Assert.Equal(TraceOutcome.Ok, Assert.Single(sequences).Outcome);
    }

    [Fact]
    public void NullEndTime_FallsBackToStartTime_NeverThrowsOrProducesANegativeDuration()
    {
        var sequences = TracesEndpoint.GroupIntoSequences([new TraceRecordSnapshot
        {
            TraceId = "t1", Tool = "hades_status", StartUtcMs = 1000, EndUtcMs = null, Status = "ok",
        }]);

        var seq = Assert.Single(sequences);
        Assert.Equal(1000, seq.EndUtcMs);
        Assert.Equal(0, seq.DurationMs);
    }
}

/// <summary>
/// Pure tests of <see cref="TracesEndpoint.ResolveSequences"/> - filter/sort/truncate over an
/// already-grouped sequence list, exercised directly against hand-built <see cref="TraceSequenceRow"/>
/// values.
/// </summary>
public sealed class TracesResolveSequencesTests
{
    static TraceSequenceRow Seq(string id, string[] tools, long start, long end, TraceOutcome outcome) => new()
    {
        Id = id,
        Tools = tools,
        Pattern = string.Join(" → ", tools),
        CallCount = tools.Length,
        StartUtcMs = start,
        EndUtcMs = end,
        DurationMs = end - start,
        Outcome = outcome,
        TraceIds = [id],
    };

    [Fact]
    public void NoFilters_ReturnsEverySequence_NewestFirst()
    {
        var early = Seq("early", ["a"], 0, 100, TraceOutcome.Ok);
        var late = Seq("late", ["b"], 1000, 1100, TraceOutcome.Ok);

        var result = TracesEndpoint.ResolveSequences([early, late], tool: null, outcome: null, minDurationMs: null, maxDurationMs: null, truncated: false);

        Assert.Equal(["late", "early"], result.Sequences.Select(s => s.Id));
        Assert.False(result.Truncated);
    }

    [Fact]
    public void ToolFilter_MatchesAnySequenceContainingAToolNameSubstring_CaseInsensitive()
    {
        var withSearch = Seq("with-search", ["search_by_name", "read_file"], 0, 100, TraceOutcome.Ok);
        var withoutSearch = Seq("without", ["read_file"], 0, 100, TraceOutcome.Ok);

        var result = TracesEndpoint.ResolveSequences([withSearch, withoutSearch], tool: "SEARCH", outcome: null, minDurationMs: null, maxDurationMs: null, truncated: false);

        Assert.Equal("with-search", Assert.Single(result.Sequences).Id);
    }

    [Fact]
    public void OutcomeFilter_MatchesTheSequencesResolvedOutcome()
    {
        var ok = Seq("ok", ["a"], 0, 100, TraceOutcome.Ok);
        var failed = Seq("failed", ["a"], 0, 100, TraceOutcome.Error);

        var result = TracesEndpoint.ResolveSequences([ok, failed], tool: null, outcome: TraceOutcome.Error, minDurationMs: null, maxDurationMs: null, truncated: false);

        Assert.Equal("failed", Assert.Single(result.Sequences).Id);
    }

    [Fact]
    public void DurationFilters_BoundBothEnds()
    {
        var short_ = Seq("short", ["a"], 0, 100, TraceOutcome.Ok); // 100ms
        var medium = Seq("medium", ["a"], 0, 5_000, TraceOutcome.Ok); // 5000ms
        var long_ = Seq("long", ["a"], 0, 60_000, TraceOutcome.Ok); // 60000ms

        var result = TracesEndpoint.ResolveSequences([short_, medium, long_], tool: null, outcome: null, minDurationMs: 1_000, maxDurationMs: 10_000, truncated: false);

        Assert.Equal("medium", Assert.Single(result.Sequences).Id);
    }

    [Fact]
    public void Truncated_PassesThroughFromTheCaller()
    {
        var result = TracesEndpoint.ResolveSequences([], tool: null, outcome: null, minDurationMs: null, maxDurationMs: null, truncated: true);

        Assert.True(result.Truncated);
    }
}

/// <summary>
/// Pure tests of <see cref="TracesEndpoint.FlattenJsonToDisplayRows"/> - the JSON-to-display-rows
/// logic behind <see cref="SpanRow.Attributes"/>/<see cref="SpanRow.Events"/>, exercised directly
/// against hand-built <see cref="JsonDocument"/>s, no store, no HTTP. Same "verbatim, pure function"
/// discipline as <see cref="TracesGroupIntoSequencesTests"/>/<see cref="TracesResolveSequencesTests"/>.
///
/// <b>The gap this closes (Plan 13 Task 5 -> Task 7 Step 0).</b> Swift's own
/// <c>ControlJSONValue.stringLeaves()</c> could only ever surface a JSON <c>.string</c> leaf -
/// every <c>.int</c>/<c>.double</c>/<c>.bool</c> value (<c>resultSizeBytes</c>, <c>timeUtcMs</c>,
/// ...) was silently invisible, because stringifying a number or bool client-side is Swift deciding
/// how a value reads - exactly what spec #3 §1 ("Swift renders, .NET decides") forbids. This method
/// is where that decision now happens, once, server-side: every leaf becomes a
/// <c>{key, valueDisplay}</c> pair with <c>valueDisplay</c> already the exact string to show.
/// </summary>
public sealed class TracesFlattenAttributesTests
{
    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void StringLeaf_ValueDisplayIsTheDecodedTextVerbatim_NeverTheQuotedJsonToken()
    {
        var rows = TracesEndpoint.FlattenJsonToDisplayRows(Parse("""{"resultType":"CallToolResult"}"""));

        var row = Assert.Single(rows);
        Assert.Equal("resultType", row.Key);
        Assert.Equal("CallToolResult", row.ValueDisplay);
    }

    [Fact]
    public void IntLeaf_ValueDisplayIsTheLiteralJsonToken_PreviouslyInvisibleToTheShell()
    {
        // The exact gap this closes: resultSizeBytes was silently dropped by Swift's own
        // stringLeaves() because rendering an Int is a formatting decision Swift must not make.
        // GetRawText() reads back exactly the digits already written - no re-serialization.
        var rows = TracesEndpoint.FlattenJsonToDisplayRows(Parse("""{"resultSizeBytes":2532}"""));

        var row = Assert.Single(rows);
        Assert.Equal("resultSizeBytes", row.Key);
        Assert.Equal("2532", row.ValueDisplay);
    }

    [Fact]
    public void DoubleLeaf_ValueDisplayIsTheLiteralJsonToken_NoRoundingNoReformatting()
    {
        var rows = TracesEndpoint.FlattenJsonToDisplayRows(Parse("""{"averageDurationMs":12.5}"""));

        Assert.Equal("12.5", Assert.Single(rows).ValueDisplay);
    }

    [Fact]
    public void BoolAndNullLeaves_RenderAsTheirLiteralJsonTokens()
    {
        var rows = TracesEndpoint.FlattenJsonToDisplayRows(Parse("""{"needsRestart":true,"parentSpanId":null}"""));

        Assert.Contains(rows, r => r.Key == "needsRestart" && r.ValueDisplay == "true");
        Assert.Contains(rows, r => r.Key == "parentSpanId" && r.ValueDisplay == "null");
    }

    [Fact]
    public void NestedObject_KeysAreDotJoined_SortedAlphabetically_SameConventionAsSwiftsOwnRetiredStringLeaves()
    {
        var rows = TracesEndpoint.FlattenJsonToDisplayRows(
            Parse("""{"resultType":"CallToolResult","arguments":{"namePattern":"Hades"}}"""));

        Assert.Equal(["arguments.namePattern", "resultType"], rows.Select(r => r.Key));
        Assert.Equal("Hades", rows.Single(r => r.Key == "arguments.namePattern").ValueDisplay);
    }

    [Fact]
    public void ArrayOfObjects_KeysAreBracketIndexed()
    {
        var rows = TracesEndpoint.FlattenJsonToDisplayRows(
            Parse("""[{"name":"exception","message":"boom"}]"""));

        Assert.Equal(["[0].message", "[0].name"], rows.Select(r => r.Key));
    }

    [Fact]
    public void EmptyObject_ProducesNoRows_AWellFormedEmptyList()
    {
        Assert.Empty(TracesEndpoint.FlattenJsonToDisplayRows(Parse("{}")));
    }
}

/// <summary>
/// The four <c>/control/traces/*</c> GET actions over real HTTP against a directly-constructed
/// <see cref="ControlListener"/>, backed by a REAL <see cref="TraceStore"/> written to via
/// <see cref="TraceStore.RecordToolCall"/> exactly as <see cref="Hades.Core.Tracing.ToolCallTracer"/>
/// would - proving TracesEndpoint actually builds on <see cref="TraceStore"/>'s own query methods
/// (recent traces, one trace with spans, slowest tools, failures - the plan's own explicit list),
/// not a second parallel read of the database. Same style as every other Control
/// *EndpointHttpTests class: proving auth/Origin/routing/shape.
/// </summary>
public sealed class TracesEndpointHttpTests : IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff66600006";

    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");

    readonly ProjectService _projects;

    public TracesEndpointHttpTests()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {ProjectGuid}\n");

        _projects = new ProjectService(new AppPaths(Path.Combine(_tempDir, "app")));
        _projects.Adopt(_projectRoot);
    }

    static void RecordCall(TraceStore store, string tool, long startUtcMs, long endUtcMs, bool ok, string? errorMessage = null) =>
        store.RecordToolCall(new ToolCallOutcome
        {
            ToolName = tool,
            StartUtcMs = startUtcMs,
            EndUtcMs = endUtcMs,
            Status = ok ? "ok" : "error",
            ErrorMessage = errorMessage,
        });

    static HttpRequestMessage Request(HttpMethod method, string path, string? token, string? origin = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    static HttpClient ClientFor(ControlListener listener) => new() { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };

    public static IEnumerable<object[]> Routes()
    {
        yield return ["/control/traces/sequences"];
        yield return ["/control/traces/slow"];
        yield return ["/control/traces/failures"];
        yield return ["/control/traces/some-trace-id"];
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task NoToken_IsRefused(string path)
    {
        using var listener = new ControlListener(ConnectionFilePath, projects: _projects);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"{path}?project={ProjectGuid}", token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ForeignOrigin_IsRejectedWith403_EvenWithAValidToken(string path)
    {
        using var listener = new ControlListener(ConnectionFilePath, projects: _projects);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"{path}?project={ProjectGuid}", listener.Token, origin: "https://evil.example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sequences_AmbiguousProject_Returns400_NamesTheProblem()
    {
        // A second known project makes 'project' required - TryResolveProject must translate
        // ToolSupport's own McpException into a resolved 400, never an unhandled 500.
        var secondRoot = Path.Combine(_tempDir, "second");
        Directory.CreateDirectory(Path.Combine(secondRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(secondRoot, "ProjectSettings", "ProjectSettings.asset"), "  productGUID: aaaabbbbccccddddeeeeffff77700007\n");
        _projects.Adopt(secondRoot);

        using var listener = new ControlListener(ConnectionFilePath, projects: _projects);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/traces/sequences", listener.Token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("project", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sequences_RealTraceStoreData_GroupsIntoOneSequence_OverRealHttp()
    {
        using (var store = TraceStore.Open(_projects.Paths.TracesDb(ProjectGuid)))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RecordCall(store, "search_by_name", now, now + 50, ok: true);
            RecordCall(store, "find_references_to", now + 1000, now + 1200, ok: true);
        }

        using var listener = new ControlListener(ConnectionFilePath, projects: _projects);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/traces/sequences?project={ProjectGuid}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seq = Assert.Single(body.GetProperty("sequences").EnumerateArray());
        Assert.Equal("search_by_name → find_references_to", seq.GetProperty("pattern").GetString());
        Assert.Equal(2, seq.GetProperty("callCount").GetInt32());
        Assert.Equal("ok", seq.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task TraceDetail_UnknownId_Returns404()
    {
        using var listener = new ControlListener(ConnectionFilePath, projects: _projects);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/traces/not-a-real-trace-id?project={ProjectGuid}", listener.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TraceDetail_KnownId_ReturnsSpans_AttributesArePreRenderedKeyValueDisplayRows()
    {
        string traceId;
        using (var store = TraceStore.Open(_projects.Paths.TracesDb(ProjectGuid)))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            traceId = store.RecordToolCall(new ToolCallOutcome
            {
                ToolName = "search_by_name",
                StartUtcMs = now,
                EndUtcMs = now + 50,
                Status = "ok",
                ArgumentsJson = "{\"namePattern\":\"Player\"}",
                ResultType = "SearchResult",
                ResultSizeBytes = 128,
            });
        }

        using var listener = new ControlListener(ConnectionFilePath, projects: _projects);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/traces/{traceId}?project={ProjectGuid}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(traceId, body.GetProperty("traceId").GetString());
        var span = Assert.Single(body.GetProperty("spans").EnumerateArray());
        Assert.Equal("search_by_name", span.GetProperty("name").GetString());

        // Plan 11 Task 7 audit finding: a span-detail view (a waterfall/flamegraph, the natural
        // rendering for nested spans) needs each span's OWN duration to size its bar - without this,
        // the shell would have to subtract endUtcMs-startUtcMs itself for every span, exactly the
        // "raw timestamps where a display string [is] needed" violation the audit looks for. Mirrors
        // the resolved durationMs TraceSequenceRow/TraceDetailResult already carry at the trace level.
        Assert.Equal(50, span.GetProperty("durationMs").GetInt64());

        // Plan 13 Task 7 Step 0: a flat array of {key, valueDisplay} rows, never nested JSON - and,
        // the entire point of this reshape, resultSizeBytes (an Int - exactly what the old nested-JSON
        // shape left invisible to the shell, since rendering it would be Swift formatting a number)
        // is now a row too, right alongside the string leaves.
        var attributes = span.GetProperty("attributes");
        Assert.Equal(JsonValueKind.Array, attributes.ValueKind);
        var rows = attributes.EnumerateArray().ToDictionary(
            r => r.GetProperty("key").GetString()!, r => r.GetProperty("valueDisplay").GetString()!);
        Assert.Equal("Player", rows["arguments.namePattern"]);
        Assert.Equal("SearchResult", rows["resultType"]);
        Assert.Equal("128", rows["resultSizeBytes"]);
    }

    [Fact]
    public async Task SlowTools_RealTraceStoreData_RanksSlowestFirst()
    {
        using (var store = TraceStore.Open(_projects.Paths.TracesDb(ProjectGuid)))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RecordCall(store, "fast_tool", now, now + 10, ok: true);
            RecordCall(store, "slow_tool", now, now + 5000, ok: true);
        }

        using var listener = new ControlListener(ConnectionFilePath, projects: _projects);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/traces/slow?project={ProjectGuid}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var first = body.GetProperty("tools").EnumerateArray().First();
        Assert.Equal("slow_tool", first.GetProperty("tool").GetString());
    }

    [Fact]
    public async Task SlowTools_AverageDurationMs_IsRoundedToOneDecimalPlace_NeverAManyDigitDouble()
    {
        // Spec #3 §1: "Swift renders, .NET decides" - AVG(total_duration_ms) is a genuine SQL
        // average and does not divide evenly for these three durations (56/3 =
        // 18.666666666666668 raw), the exact shape that reached the shell as a 14-decimal-place
        // double before this fix (TracesView prints averageDurationMs verbatim via plain string
        // interpolation, so whatever precision reaches the wire is exactly what a user sees).
        using (var store = TraceStore.Open(_projects.Paths.TracesDb(ProjectGuid)))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RecordCall(store, "propose_memory_update", now, now + 18, ok: true);
            RecordCall(store, "propose_memory_update", now, now + 18, ok: true);
            RecordCall(store, "propose_memory_update", now, now + 20, ok: true);
        }

        using var listener = new ControlListener(ConnectionFilePath, projects: _projects);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/traces/slow?project={ProjectGuid}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
        // The raw JSON token itself, not a tolerance-based double comparison - this is what a
        // client actually receives and, per spec #1, must be able to render with no formatting
        // decision of its own. 18.666666666666668 rounds to 18.7.
        Assert.Equal("18.7", tool.GetProperty("averageDurationMs").GetRawText());
    }

    [Fact]
    public async Task Failures_RealTraceStoreData_IncludesTheErrorMessage_ActionableNotOpaque()
    {
        using (var store = TraceStore.Open(_projects.Paths.TracesDb(ProjectGuid)))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RecordCall(store, "component_add", now, now + 30, ok: false, errorMessage: "Unknown component type 'Foo'.");
        }

        using var listener = new ControlListener(ConnectionFilePath, projects: _projects);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/traces/failures?project={ProjectGuid}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var failure = Assert.Single(body.GetProperty("failures").EnumerateArray());
        Assert.Equal("component_add", failure.GetProperty("tool").GetString());
        Assert.Equal("Unknown component type 'Foo'.", failure.GetProperty("error").GetString());
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _tempDir, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
