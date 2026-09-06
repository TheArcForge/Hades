using Hades.Control.Client;
using Hades.Control.Client.Dtos;
using Hades.Shell.ViewModels;

namespace Hades.Shell.Tests;

/// <summary>
/// Guards that selecting a sequence resolves EVERY call in it, each with its own outcome and timing.
///
/// The Sequences tab used to drill into <c>TraceIds[0]</c> and nothing else. Because a sequence is
/// marked failed when ANY of its calls failed, the ordinary case was a row flagged with the error
/// glyph whose detail pane then reported <c>ok</c> - the first call having succeeded - while the
/// calls that actually failed were unreachable from that tab entirely. The Charon hand-run (Task 8
/// Step 8) hit it on a real seven-call sequence carrying three failures.
///
/// The whole suite was green throughout, because nothing asked how many of a sequence's calls a
/// user could open, or whether their outcomes were visible. That is what these assert.
/// </summary>
public class SequenceCallsTests
{
    static readonly ControlConnection AConnection = new() { Port = 1234, Token = "t" };

    sealed class StubClient : ITracesClient
    {
        public required Func<string, TraceDetailResult?> Detail { get; init; }

        public Task<ProjectsResult> ProjectsAsync() =>
            Task.FromResult(new ProjectsResult { Projects = [] });

        public Task<TraceSequencesResult> TracesSequencesAsync(
            string? project, string? tool, string? outcome, long? minDurationMs, long? maxDurationMs) =>
            Task.FromResult(new TraceSequencesResult { Sequences = [], Truncated = false });

        public Task<FailedCallsResult> TracesFailuresAsync(string? project) =>
            Task.FromResult(new FailedCallsResult { Failures = [], Truncated = false });

        public Task<SlowToolsResult> TracesSlowAsync(string? project) =>
            Task.FromResult(new SlowToolsResult { Tools = [], Truncated = false });

        public Task<TraceDetailResult> TraceDetailAsync(string traceId, string? project) =>
            Detail(traceId) is { } detail
                ? Task.FromResult(detail)
                : throw new ControlClientException(
                    ControlClientError.Server, "Unknown trace.", statusCode: 404);
    }

    static TraceSequenceRow Sequence(string[] tools, string[] ids, long startUtcMs = 1_000) => new()
    {
        Id = ids.Length > 0 ? ids[0] : "none",
        Tools = tools,
        Pattern = string.Join(" -> ", tools),
        CallCount = tools.Length,
        StartUtcMs = startUtcMs,
        EndUtcMs = startUtcMs + 100,
        DurationMs = 100,
        Outcome = TraceOutcome.Error,
        TraceIds = ids,
    };

    static TraceDetailResult Detail(string traceId, TraceOutcome outcome, long startUtcMs, long durationMs) => new()
    {
        TraceId = traceId,
        Tool = "unused-here",
        StartUtcMs = startUtcMs,
        DurationMs = durationMs,
        Outcome = outcome,
        Spans = [],
    };

    static TracesViewModel Subject(Func<string, TraceDetailResult?> detail) =>
        new(() => AConnection, _ => new StubClient { Detail = detail });

    /// <summary>The shape the hand-run actually hit: seven calls, three of them failed.</summary>
    [Fact]
    public async Task EveryCallIsResolved_WithItsOwnOutcome_NotJustTheFirst()
    {
        string[] tools =
        [
            "hades_ping", "get_project_summary", "search_by_name", "get_recently_changed",
            "project_get_console_log", "inspect_asset", "search_by_name",
        ];
        string[] ids = ["t-1", "t-2", "t-3", "t-4", "t-5", "t-6", "t-7"];
        var failed = new HashSet<string> { "t-3", "t-5", "t-6" };

        var vm = Subject(id => Detail(id, failed.Contains(id) ? TraceOutcome.Error : TraceOutcome.Ok, 1_000, 5));

        await vm.SelectSequenceAsync(Sequence(tools, ids));

        Assert.Equal(SequenceCallsFetchKind.Loaded, vm.SelectedSequenceCalls.Kind);
        Assert.Equal(7, vm.SelectedSequenceCalls.Calls.Count);
        Assert.Equal(3, vm.SelectedSequenceCalls.Calls.Count(c => c.Outcome == TraceOutcome.Error));
        Assert.Equal(ids, vm.SelectedSequenceCalls.Calls.Select(c => c.TraceId));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], vm.SelectedSequenceCalls.Calls.Select(c => c.Position));
    }

    /// <summary>
    /// The same tool name appearing twice with different ids must stay two separately-resolved calls
    /// with their own outcomes. In the hand-run's sequence <c>search_by_name</c> appears once failed
    /// and once succeeded; anything keyed on the tool name would collapse them and hide one outcome.
    /// </summary>
    [Fact]
    public async Task ARepeatedToolKeepsItsOwnOutcomePerCall()
    {
        var vm = Subject(id => Detail(id, id == "t-1" ? TraceOutcome.Error : TraceOutcome.Ok, 1_000, 1));

        await vm.SelectSequenceAsync(Sequence(["search_by_name", "search_by_name"], ["t-1", "t-2"]));

        var calls = vm.SelectedSequenceCalls.Calls;
        Assert.Equal(2, calls.Count);
        Assert.Equal("search_by_name", calls[0].Tool);
        Assert.Equal("search_by_name", calls[1].Tool);
        Assert.Equal(TraceOutcome.Error, calls[0].Outcome);
        Assert.Equal(TraceOutcome.Ok, calls[1].Outcome);
    }

    /// <summary>Offsets are measured from the SEQUENCE's start, which is what makes a column of them
    /// readable as a timeline rather than as unrelated wall-clock stamps.</summary>
    [Fact]
    public async Task OffsetsAreMeasuredFromTheSequenceStart()
    {
        var starts = new Dictionary<string, long> { ["t-1"] = 5_000, ["t-2"] = 5_250, ["t-3"] = 6_500 };
        var vm = Subject(id => Detail(id, TraceOutcome.Ok, starts[id], 10));

        await vm.SelectSequenceAsync(Sequence(["a", "b", "c"], ["t-1", "t-2", "t-3"], startUtcMs: 5_000));

        Assert.Equal([0L, 250L, 1_500L], vm.SelectedSequenceCalls.Calls.Select(c => c.OffsetMs));
    }

    /// <summary>
    /// A trace pruned out from under a still-listed sequence drops from the breakdown rather than
    /// appearing as an invented row. A placeholder would put an outcome on screen the core never
    /// reported, which is the exact failure this view is being fixed to stop.
    /// </summary>
    [Fact]
    public async Task AnUnfetchableCallIsDropped_NotInvented()
    {
        var vm = Subject(id => id == "t-2" ? null : Detail(id, TraceOutcome.Ok, 1_000, 1));

        await vm.SelectSequenceAsync(Sequence(["a", "b", "c"], ["t-1", "t-2", "t-3"]));

        Assert.Equal(SequenceCallsFetchKind.Loaded, vm.SelectedSequenceCalls.Kind);
        Assert.Equal(["t-1", "t-3"], vm.SelectedSequenceCalls.Calls.Select(c => c.TraceId));
    }

    /// <summary>Mismatched parallel arrays truncate to the shorter rather than throwing - the API's
    /// contract says they match, and a violation must degrade rather than blank the pane.</summary>
    [Theory]
    [InlineData(3, 2, 2)]
    [InlineData(2, 3, 2)]
    [InlineData(0, 0, 0)]
    public async Task MismatchedArraysTruncateToTheShorter(int toolCount, int idCount, int expected)
    {
        var vm = Subject(id => Detail(id, TraceOutcome.Ok, 1_000, 1));

        await vm.SelectSequenceAsync(Sequence(
            [.. Enumerable.Range(0, toolCount).Select(i => $"tool-{i}")],
            [.. Enumerable.Range(0, idCount).Select(i => $"id-{i}")]));

        Assert.Equal(expected, vm.SelectedSequenceCalls.Calls.Count);
    }

    /// <summary>
    /// Re-selecting the same sequence must NOT re-resolve its calls. Once the selection is restored
    /// by id after every refresh, a naive implementation would issue one request per call per poll
    /// tick, forever.
    /// </summary>
    [Fact]
    public async Task ReselectingTheSameSequenceDoesNotRefetch()
    {
        var fetches = 0;
        var vm = Subject(id => { fetches++; return Detail(id, TraceOutcome.Ok, 1_000, 1); });
        var sequence = Sequence(["a", "b", "c"], ["t-1", "t-2", "t-3"]);

        await vm.SelectSequenceAsync(sequence);
        var afterFirst = fetches;

        await vm.SelectSequenceAsync(sequence);
        await vm.SelectSequenceAsync(sequence);

        Assert.Equal(3, afterFirst);
        Assert.Equal(afterFirst, fetches);
        Assert.Equal(sequence.Id, vm.SelectedSequenceId);
    }

    /// <summary>A genuinely different sequence does resolve, of course.</summary>
    [Fact]
    public async Task SelectingADifferentSequenceRefetches()
    {
        var fetches = 0;
        var vm = Subject(id => { fetches++; return Detail(id, TraceOutcome.Ok, 1_000, 1); });

        await vm.SelectSequenceAsync(Sequence(["a"], ["t-1"]));
        await vm.SelectSequenceAsync(Sequence(["b"], ["t-2"]));

        Assert.Equal(2, fetches);
        Assert.Equal("t-2", vm.SelectedSequenceId);
    }

    /// <summary>Selecting a sequence must not leave the previously selected call's spans on screen
    /// beneath it - they belong to a call the user is no longer looking at.</summary>
    [Fact]
    public async Task SelectingASequenceClearsThePreviouslySelectedCall()
    {
        var vm = Subject(id => Detail(id, TraceOutcome.Ok, 1_000, 1));

        await vm.SelectTraceAsync("t-1");
        Assert.Equal(TraceDetailFetchKind.Loaded, vm.SelectedTraceDetail.Kind);

        await vm.SelectSequenceAsync(Sequence(["a"], ["t-9"]));

        Assert.Equal(TraceDetailFetchKind.NotSelected, vm.SelectedTraceDetail.Kind);
    }

    /// <summary>
    /// An unchanged refresh must not republish the list. Assigning an equivalent list still raises
    /// PropertyChanged, WPF rebinds, and the user's selection is discarded - which at one poll every
    /// few seconds made the highlight blink off and back continuously. Measured before the fix:
    /// present in only 14 of 30 samples over 24 seconds.
    /// </summary>
    [Fact]
    public async Task AnUnchangedRefreshDoesNotRepublishTheSequenceList()
    {
        var rows = new[] { Sequence(["a", "b"], ["t-1", "t-2"]) };
        var client = new SequencesClient { Rows = () => rows };
        var vm = new TracesViewModel(() => AConnection, _ => client);

        await vm.RefreshAsync();
        var first = vm.Sequences;

        // A fresh list of NEW instances carrying identical content, exactly as a real poll returns.
        client.Rows = () => new[] { Sequence(["a", "b"], ["t-1", "t-2"]) };
        var republished = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TracesViewModel.Sequences)) republished++; };

        await vm.RefreshAsync();

        Assert.Equal(0, republished);
        Assert.Same(first, vm.Sequences);
    }

    /// <summary>A genuine change still republishes - the point is to skip no-ops, not to freeze.</summary>
    [Fact]
    public async Task AChangedRefreshDoesRepublish()
    {
        var client = new SequencesClient { Rows = () => new[] { Sequence(["a"], ["t-1"]) } };
        var vm = new TracesViewModel(() => AConnection, _ => client);

        await vm.RefreshAsync();

        client.Rows = () => new[] { Sequence(["a", "b"], ["t-1", "t-2"]) };
        var republished = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TracesViewModel.Sequences)) republished++; };

        await vm.RefreshAsync();

        Assert.Equal(1, republished);
        Assert.Equal(2, vm.Sequences[0].CallCount);
    }

    sealed class SequencesClient : ITracesClient
    {
        public required Func<TraceSequenceRow[]> Rows { get; set; }

        public Task<ProjectsResult> ProjectsAsync() =>
            Task.FromResult(new ProjectsResult { Projects = [] });

        public Task<TraceSequencesResult> TracesSequencesAsync(
            string? project, string? tool, string? outcome, long? minDurationMs, long? maxDurationMs) =>
            Task.FromResult(new TraceSequencesResult { Sequences = Rows(), Truncated = false });

        public Task<FailedCallsResult> TracesFailuresAsync(string? project) =>
            Task.FromResult(new FailedCallsResult { Failures = [], Truncated = false });

        public Task<SlowToolsResult> TracesSlowAsync(string? project) =>
            Task.FromResult(new SlowToolsResult { Tools = [], Truncated = false });

        public Task<TraceDetailResult> TraceDetailAsync(string traceId, string? project) =>
            Task.FromResult(new TraceDetailResult
            {
                TraceId = traceId, Tool = "t", StartUtcMs = 1, Outcome = TraceOutcome.Ok, Spans = [],
            });
    }
}
