using Hades.Control.Client;
using Hades.Control.Client.Dtos;
using Hades.Shell.ViewModels;

namespace Hades.Shell.Tests;

/// <summary>
/// Charon's behaviour behind a fake client. The load-bearing cases here are the FOUR INDEPENDENT
/// fetches: one failing must never stop the other three updating, and must never clear what is
/// already on screen.
/// </summary>
public class TracesViewModelTests
{
    sealed class FakeTracesClient : ITracesClient
    {
        public Func<Task<ProjectsResult>> OnProjects { get; set; } =
            () => Task.FromResult(new ProjectsResult { Projects = [] });

        public Func<Task<TraceSequencesResult>> OnSequences { get; set; } =
            () => Task.FromResult(new TraceSequencesResult { Sequences = [], Truncated = false });

        public Func<Task<FailedCallsResult>> OnFailures { get; set; } =
            () => Task.FromResult(new FailedCallsResult { Failures = [], Truncated = false });

        public Func<Task<SlowToolsResult>> OnSlow { get; set; } =
            () => Task.FromResult(new SlowToolsResult { Tools = [], Truncated = false });

        public Func<string, Task<TraceDetailResult>>? OnDetail { get; set; }

        public string? SeenProject { get; private set; }
        public string? SeenTool { get; private set; }
        public string? SeenOutcome { get; private set; }
        public string? SeenDetailProject { get; private set; }

        public Task<ProjectsResult> ProjectsAsync() => OnProjects();

        public Task<TraceSequencesResult> TracesSequencesAsync(
            string? project, string? tool, string? outcome, long? minDurationMs, long? maxDurationMs)
        {
            SeenProject = project;
            SeenTool = tool;
            SeenOutcome = outcome;
            return OnSequences();
        }

        public Task<FailedCallsResult> TracesFailuresAsync(string? project) => OnFailures();

        public Task<SlowToolsResult> TracesSlowAsync(string? project) => OnSlow();

        public Task<TraceDetailResult> TraceDetailAsync(string traceId, string? project)
        {
            SeenDetailProject = project;
            return OnDetail?.Invoke(traceId) ?? Task.FromResult(Detail(traceId, TraceOutcome.Ok));
        }
    }

    static readonly ControlConnection AConnection = new() { Port = 1234, Token = "t" };

    static TraceDetailResult Detail(string traceId, TraceOutcome outcome) => new()
    {
        TraceId = traceId,
        Tool = "read_file",
        StartUtcMs = 1,
        Outcome = outcome,
        Spans = [],
    };

    static TraceSequenceRow Sequence(string id, TraceOutcome outcome) => new()
    {
        Id = id,
        Tools = ["read_file"],
        Pattern = "read_file",
        CallCount = 1,
        StartUtcMs = 1,
        EndUtcMs = 2,
        DurationMs = 1,
        Outcome = outcome,
        TraceIds = ["t-1"],
    };

    static ProjectRow Project(string name, string guid) => new()
    {
        Name = name,
        Path = @"C:\p\" + name,
        ProductGuid = guid,
        IndexState = ProjectIndexState.Indexed,
        IndexStatus = "Indexed",
        NodeCount = 1,
        EdgeCount = 1,
        Editor = new ProjectEditorInfo { State = ProjectEditorState.Absent, Status = "No editor" },
        Warnings = [],
    };

    static ControlClientException ServerError(string message) =>
        new(ControlClientError.Server, message, statusCode: 400);

    static (TracesViewModel Vm, FakeTracesClient Client) NewSubject()
    {
        var client = new FakeTracesClient();
        return (new TracesViewModel(() => AConnection, _ => client), client);
    }

    // ---- loading ------------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_LoadsSequencesFailuresAndSlowTools()
    {
        var (vm, client) = NewSubject();
        client.OnSequences = () => Task.FromResult(
            new TraceSequencesResult { Sequences = [Sequence("s-1", TraceOutcome.Ok)], Truncated = true });
        client.OnFailures = () => Task.FromResult(new FailedCallsResult
        {
            Failures = [new FailedCallRow { TraceId = "t-9", Tool = "read_file", StartUtcMs = 1, Error = "boom" }],
            Truncated = false,
        });
        client.OnSlow = () => Task.FromResult(new SlowToolsResult
        {
            Tools = [new SlowToolRow { Tool = "read_file", CallCount = 3, AverageDurationMs = 12.5, MaxDurationMs = 40 }],
            Truncated = false,
        });

        await vm.RefreshAsync();

        Assert.Single(vm.Sequences);
        Assert.True(vm.SequencesTruncated);
        Assert.Single(vm.Failures);
        Assert.Single(vm.SlowTools);
    }

    /// <summary>
    /// Failures and slow calls come from their OWN endpoints and are never filtered client-side out
    /// of the sequences list. If sequences fails, the other two must still update.
    /// </summary>
    [Fact]
    public async Task EachFetchSelfHealsIndependently()
    {
        var (vm, client) = NewSubject();
        client.OnSequences = () => throw new ControlClientException(ControlClientError.Transport, "blip");
        client.OnFailures = () => Task.FromResult(new FailedCallsResult
        {
            Failures = [new FailedCallRow { TraceId = "t-9", Tool = "read_file", StartUtcMs = 1 }],
            Truncated = false,
        });

        await vm.RefreshAsync();

        Assert.Empty(vm.Sequences);
        Assert.Single(vm.Failures);
    }

    [Fact]
    public async Task ATransientFailure_KeepsWhatIsAlreadyOnScreen()
    {
        var (vm, client) = NewSubject();
        client.OnSequences = () => Task.FromResult(
            new TraceSequencesResult { Sequences = [Sequence("s-1", TraceOutcome.Ok)], Truncated = false });
        await vm.RefreshAsync();

        client.OnSequences = () => throw new ControlClientException(ControlClientError.Transport, "blip");
        await vm.RefreshAsync();

        Assert.Single(vm.Sequences);
    }

    // ---- refreshError -------------------------------------------------------------------------

    /// <summary>
    /// A server error carrying a message is not a transient blip - most often "Hades knows N
    /// projects, so this call needs a 'project' argument". The shell cannot act on that silently, so
    /// it is surfaced verbatim. The DATA is still left untouched.
    /// </summary>
    [Fact]
    public async Task AServerMessage_IsSurfacedVerbatim_WithoutClearingData()
    {
        var (vm, client) = NewSubject();
        client.OnSequences = () => Task.FromResult(
            new TraceSequencesResult { Sequences = [Sequence("s-1", TraceOutcome.Ok)], Truncated = false });
        await vm.RefreshAsync();

        client.OnSequences = () => throw ServerError("Hades knows 3 projects, so this call needs a 'project' argument.");
        await vm.RefreshAsync();

        Assert.Equal("Hades knows 3 projects, so this call needs a 'project' argument.", vm.RefreshError);
        Assert.Single(vm.Sequences);
    }

    /// <summary>
    /// RefreshError reflects the LAST attempt's own outcome - it is not a sticky banner outliving
    /// its cause. Once a later refresh succeeds it must clear itself.
    /// </summary>
    [Fact]
    public async Task RefreshError_ClearsOnceALaterRefreshSucceeds()
    {
        var (vm, client) = NewSubject();
        client.OnSequences = () => throw ServerError("needs a project");
        await vm.RefreshAsync();
        Assert.NotNull(vm.RefreshError);

        client.OnSequences = () => Task.FromResult(new TraceSequencesResult { Sequences = [], Truncated = false });
        await vm.RefreshAsync();

        Assert.Null(vm.RefreshError);
    }

    [Fact]
    public async Task ATransientFailure_SetsNoRefreshError()
    {
        var (vm, client) = NewSubject();
        client.OnSequences = () => throw new ControlClientException(ControlClientError.Transport, "blip");

        await vm.RefreshAsync();

        Assert.Null(vm.RefreshError);
    }

    // ---- project filter -----------------------------------------------------------------------

    /// <summary>
    /// The traces routes refuse with a 400 when more than one project is known and none is given, so
    /// the filter defaults to the first known project - within the SAME tick, because the projects
    /// fetch runs before the other three.
    /// </summary>
    [Fact]
    public async Task ProjectFilter_DefaultsToTheFirstKnownProject_WithinOneTick()
    {
        var (vm, client) = NewSubject();
        client.OnProjects = () => Task.FromResult(new ProjectsResult
        {
            Projects = [Project("First", "guid-1"), Project("Second", "guid-2")],
        });

        await vm.RefreshAsync();

        Assert.Equal("guid-1", vm.ProjectFilter);
        Assert.Equal("guid-1", client.SeenProject);
    }

    [Fact]
    public async Task ProjectFilter_NeverOverridesAnAlreadyChosenValue()
    {
        var (vm, client) = NewSubject();
        client.OnProjects = () => Task.FromResult(new ProjectsResult
        {
            Projects = [Project("First", "guid-1"), Project("Second", "guid-2")],
        });

        await vm.SelectProjectAsync("guid-2");
        await vm.RefreshAsync();

        Assert.Equal("guid-2", vm.ProjectFilter);
    }

    // ---- selection ----------------------------------------------------------------------------

    [Fact]
    public async Task SelectTrace_LoadsTheDetail()
    {
        var (vm, client) = NewSubject();
        client.OnDetail = id => Task.FromResult(Detail(id, TraceOutcome.Error));

        await vm.SelectTraceAsync("t-1");

        Assert.Equal(TraceDetailFetchKind.Loaded, vm.SelectedTraceDetail.Kind);
        Assert.Equal("t-1", vm.SelectedTraceDetail.Detail!.TraceId);
    }

    [Fact]
    public async Task SelectTrace_UsesTheCurrentProjectFilter()
    {
        var (vm, client) = NewSubject();
        await vm.SelectProjectAsync("guid-7");

        await vm.SelectTraceAsync("t-1");

        Assert.Equal("guid-7", client.SeenDetailProject);
    }

    [Fact]
    public async Task SelectTrace_ServerFailure_ShowsTheServersOwnMessage()
    {
        var (vm, client) = NewSubject();
        client.OnDetail = _ => throw ServerError("Unknown trace 't-1'.");

        await vm.SelectTraceAsync("t-1");

        Assert.Equal(TraceDetailFetchKind.Failed, vm.SelectedTraceDetail.Kind);
        Assert.Equal("Unknown trace 't-1'.", vm.SelectedTraceDetail.Message);
    }

    [Fact]
    public async Task SelectTrace_TransientFailure_LeavesTheDetailAsItWas()
    {
        var (vm, client) = NewSubject();
        await vm.SelectTraceAsync("t-1");

        client.OnDetail = _ => throw new ControlClientException(ControlClientError.Transport, "blip");
        await vm.SelectTraceAsync("t-2");

        Assert.Equal(TraceDetailFetchKind.Loaded, vm.SelectedTraceDetail.Kind);
        Assert.Equal("t-1", vm.SelectedTraceDetail.Detail!.TraceId);
    }

    /// <summary>
    /// A selected call's spans are scoped to the project they came from. Carrying one across a
    /// project switch shows one project's trace while the picker reads another - confirmed live on
    /// the Mac, where switching to an empty project still displayed the previous project's span.
    /// </summary>
    [Fact]
    public async Task SelectProject_ClearsTheSelectedDetail()
    {
        var (vm, _) = NewSubject();
        await vm.SelectTraceAsync("t-1");
        Assert.Equal(TraceDetailFetchKind.Loaded, vm.SelectedTraceDetail.Kind);

        await vm.SelectProjectAsync("guid-2");

        Assert.Equal(TraceDetailFetchKind.NotSelected, vm.SelectedTraceDetail.Kind);
    }

    /// <summary>
    /// An ordinary tick must NOT clear a selection: a selected call is a fixed historical record,
    /// not a live value. Only a deliberate project change ends its lifetime.
    /// </summary>
    [Fact]
    public async Task Refresh_DoesNotClearTheSelectedDetail()
    {
        var (vm, _) = NewSubject();
        await vm.SelectTraceAsync("t-1");

        await vm.RefreshAsync();

        Assert.Equal(TraceDetailFetchKind.Loaded, vm.SelectedTraceDetail.Kind);
    }

    // ---- filters ------------------------------------------------------------------------------

    [Fact]
    public async Task ApplyFilters_SendsThemAndNormalisesEmptyToAbsent()
    {
        var (vm, client) = NewSubject();

        await vm.ApplyFiltersAsync(tool: "", outcome: "error", minDurationMs: 5, maxDurationMs: 50);

        Assert.Null(client.SeenTool);
        Assert.Equal("error", client.SeenOutcome);
    }

    [Fact]
    public async Task ApplyFilters_LeavesTheProjectFilterAlone()
    {
        var (vm, _) = NewSubject();
        await vm.SelectProjectAsync("guid-2");

        await vm.ApplyFiltersAsync(tool: "read_file", outcome: null, minDurationMs: null, maxDurationMs: null);

        Assert.Equal("guid-2", vm.ProjectFilter);
    }

    // ---- glyphs -------------------------------------------------------------------------------

    /// <summary>
    /// A failing call must be visibly distinguishable from a successful one, and an outcome this
    /// build does not recognise must render something rather than crash.
    /// </summary>
    [Fact]
    public void EveryOutcomeRendersADistinctGlyph_IncludingUnknown()
    {
        var ok = StatusGlyph.For(TraceOutcome.Ok);
        var error = StatusGlyph.For(TraceOutcome.Error);
        var unknown = StatusGlyph.For((TraceOutcome)9999);

        Assert.NotEqual(ok, error);
        Assert.False(string.IsNullOrEmpty(unknown));
    }
}
