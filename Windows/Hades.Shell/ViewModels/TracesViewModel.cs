using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Hades.Control.Client;
using Hades.Control.Client.Dtos;

namespace Hades.Shell.ViewModels;

/// <summary>The traces surface of the control API. A seam, so the view model can be driven through
/// every failure without a running core.</summary>
public interface ITracesClient
{
    Task<ProjectsResult> ProjectsAsync();

    Task<TraceSequencesResult> TracesSequencesAsync(
        string? project, string? tool, string? outcome, long? minDurationMs, long? maxDurationMs);

    Task<FailedCallsResult> TracesFailuresAsync(string? project);
    Task<SlowToolsResult> TracesSlowAsync(string? project);
    Task<TraceDetailResult> TraceDetailAsync(string traceId, string? project);
}

/// <summary>
/// Owns the Charon (traces) section's own fetch and published state. The port of
/// <c>Mac/HadesApp/Sources/HadesApp/MainWindow/TracesViewModel.swift</c>.
///
/// <b>THREE INDEPENDENT FETCHES, NOT ONE</b> (four counting projects). Failures and slow calls come
/// from their own endpoints and are never filtered client-side out of the sequences list - each of
/// <c>/traces/sequences</c>, <c>/failures</c> and <c>/slow</c> maps 1:1 onto a separate store query.
/// So each self-heals independently: a failure in one must not stop the others updating, and must
/// not clear what is already on screen.
///
/// <b>Sequences are the primary timeline.</b> It is the only traces route that accepts filters at
/// all - there is no flat "every call" endpoint. A sequence's own parallel <c>Tools</c>/<c>TraceIds</c>
/// arrays already list every call it groups, in order, and <see cref="SelectTraceAsync"/> is how a
/// view reaches one of them for span detail.
/// </summary>
public sealed class TracesViewModel : INotifyPropertyChanged
{
    // See TrayViewModel for why the handler is shared and the HttpClient is not.
    static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 4,
    };

    readonly Func<ControlConnection?> _discover;
    readonly Func<ControlConnection, ITracesClient> _makeClient;

    IReadOnlyList<TraceSequenceRow> _sequences = [];


    IReadOnlyList<FailedCallRow> _failures = [];
    IReadOnlyList<SlowToolRow> _slowTools = [];
    IReadOnlyList<ProjectRow> _knownProjects = [];
    TraceDetailFetchState _selectedTraceDetail = TraceDetailFetchState.NotSelected;
    SequenceCallsFetchState _selectedSequenceCalls = SequenceCallsFetchState.NotSelected;
    string? _selectedSequenceId;
    bool _sequencesTruncated;
    string? _refreshError;
    string _projectFilter = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TracesViewModel(
        Func<ControlConnection?> discover,
        Func<ControlConnection, ITracesClient>? makeClient = null)
    {
        _discover = discover;
        _makeClient = makeClient ?? DefaultClient;
    }

    /// <summary>
    /// The sequences list, REPLACED wholesale on every refresh - deliberately, and not because the
    /// alternative was not tried.
    ///
    /// <para>An <c>ObservableCollection</c> reconciled in place would give the rows stable identity
    /// and let a selection survive a refresh on its own. It cannot live here: <see cref="RefreshAsync"/>
    /// runs on the poll loop's thread, and mutating a collection bound to a WPF list from off the UI
    /// thread throws. It was tried and it emptied the entire view - the exception escaped the refresh
    /// and took the projects and failures fetches down with it. It is also barred by this shell's own
    /// rule, which the plan states outright: view models never touch the Dispatcher, so tests need no
    /// STA apartment, and marshalling belongs in the view.</para>
    ///
    /// <para>So the identity problem is solved WHERE IT BELONGS, in <c>TracesView.xaml.cs</c>, which
    /// re-selects by <see cref="SelectedSequenceId"/> after this property changes.</para>
    /// </summary>
    public IReadOnlyList<TraceSequenceRow> Sequences
    {
        get => _sequences;
        private set { _sequences = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether two sequence lists say the same thing, field by field.
    ///
    /// <para>Written out rather than using the records' own equality, because
    /// <see cref="TraceSequenceRow"/> holds <c>IReadOnlyList</c> members and the generated
    /// <c>Equals</c> compares those by REFERENCE - so every freshly deserialised row differs from
    /// its identical predecessor and the comparison would always say "changed", which is exactly the
    /// churn this exists to avoid.</para>
    ///
    /// <para>Only the fields a row RENDERS are compared. Two rows agreeing on all of them are
    /// indistinguishable on screen, so replacing one with the other buys nothing and costs the
    /// selection.</para>
    /// </summary>
    static bool SameSequences(IReadOnlyList<TraceSequenceRow> current, IReadOnlyList<TraceSequenceRow> incoming)
    {
        if (current.Count != incoming.Count) return false;

        for (var i = 0; i < current.Count; i++)
        {
            if (current[i].Id != incoming[i].Id
                || current[i].CallCount != incoming[i].CallCount
                || current[i].Outcome != incoming[i].Outcome
                || current[i].DurationMs != incoming[i].DurationMs
                || current[i].StartUtcMs != incoming[i].StartUtcMs
                || current[i].EndUtcMs != incoming[i].EndUtcMs
                || current[i].Pattern != incoming[i].Pattern)
            {
                return false;
            }
        }

        return true;
    }

    public bool SequencesTruncated
    {
        get => _sequencesTruncated;
        private set { _sequencesTruncated = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<FailedCallRow> Failures
    {
        get => _failures;
        private set { _failures = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<SlowToolRow> SlowTools
    {
        get => _slowTools;
        private set { _slowTools = value; OnPropertyChanged(); }
    }

    /// <summary>Every known project, for this view's own project picker.</summary>
    public IReadOnlyList<ProjectRow> KnownProjects
    {
        get => _knownProjects;
        private set { _knownProjects = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The currently selected call's span detail. Populated only by
    /// <see cref="SelectTraceAsync"/>, never by <see cref="RefreshAsync"/> - a selected call is a
    /// fixed historical record, not something to re-poll every tick. Cleared by
    /// <see cref="SelectProjectAsync"/> though; see that method for why.
    /// </summary>
    public TraceDetailFetchState SelectedTraceDetail
    {
        get => _selectedTraceDetail;
        private set { _selectedTraceDetail = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The selected sequence's calls, each resolved to its own outcome and timing. Populated only by
    /// <see cref="SelectSequenceAsync"/>; never re-polled, for the same reason
    /// <see cref="SelectedTraceDetail"/> is not - a sequence the user clicked is a fixed historical
    /// record.
    /// </summary>
    public SequenceCallsFetchState SelectedSequenceCalls
    {
        get => _selectedSequenceCalls;
        private set { _selectedSequenceCalls = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// WHICH sequence is selected, by id - and the reason the selection survives a refresh.
    ///
    /// <para><b>The bug this closes.</b> Every refresh replaces <see cref="Sequences"/> with freshly
    /// deserialised rows, so the selected INSTANCE is no longer in the list and WPF drops the
    /// selection. Measured: a selected sequence lost its highlight within three seconds, while the
    /// calls pane went on describing a sequence that was no longer selected. <c>TraceSequenceRow</c>
    /// is a record, but record equality compares its <c>IReadOnlyList</c> members by REFERENCE, so
    /// two rows with identical contents are not equal and structural equality could not save it.</para>
    ///
    /// <para>An id survives what an instance does not, so the view re-selects the row carrying this
    /// id after each refresh (<c>TracesView.xaml.cs</c>). Binding the list's <c>SelectedValue</c>
    /// straight to this was tried first and does NOT work: replacing ItemsSource nulls the selection
    /// before anything can restore it, and a TwoWay binding faithfully writes that null back here.
    /// <see cref="SelectSequenceAsync"/> short-circuits when the id has not actually changed, so
    /// restoring the selection does not re-resolve every call in it on every tick.</para>
    /// </summary>
    public string? SelectedSequenceId
    {
        get => _selectedSequenceId;
        set { _selectedSequenceId = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The most recent refresh failure the shell cannot act on silently, verbatim from the server.
    /// Recomputed fresh every refresh rather than being a sticky banner that outlives its cause.
    /// Distinct from <see cref="SelectedTraceDetail"/>'s Failed case: that is one call's fetch, this
    /// is the whole section's.
    /// </summary>
    public string? RefreshError
    {
        get => _refreshError;
        private set { _refreshError = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Defaults to the first known project once any is known, and never overrides a chosen value.
    /// The traces routes refuse with a 400 ("Hades knows N projects, so this call needs a 'project'
    /// argument") whenever more than one project is known and none is given. Applied whenever any
    /// project is known, not only when several are: it keeps the single-project case working with
    /// zero interaction and gives the picker something concrete to show from the first tick.
    /// </summary>
    public string ProjectFilter
    {
        get => _projectFilter;
        private set { _projectFilter = value; OnPropertyChanged(); }
    }

    public string ToolFilter { get; private set; } = string.Empty;
    public string? OutcomeFilter { get; private set; }
    public long? MinDurationMsFilter { get; private set; }
    public long? MaxDurationMsFilter { get; private set; }

    /// <summary>
    /// Projects, then sequences, failures and slow tools - one discovery read and one client for all
    /// four, since they belong to the same tick.
    ///
    /// A TRANSIENT failure (transport, stale token, decoding, or a server error with no message)
    /// self-heals: the data is left exactly as it was. That self-heal is NARROWED, not deleted, for a
    /// server error that does carry a message - that is the server explaining something the shell
    /// cannot act on silently, so it surfaces verbatim via <see cref="RefreshError"/> while the data
    /// is still left untouched.
    ///
    /// The projects fetch runs FIRST and feeds <see cref="ProjectFilter"/>'s default before the other
    /// three use it, which is what makes "several projects known, none chosen" resolve within this
    /// same tick rather than needing a second one.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_discover() is not { } connection) return;

        var client = _makeClient(connection);

        // Recomputed fresh: this reflects the last attempt's own outcome, like the data properties.
        RefreshError = null;

        await Attempt(async () =>
        {
            // Only publish a list that actually differs. Reassigning an equivalent one still
            // raises PropertyChanged, which rebinds the ComboBox, which discards its SelectedValue -
            // and the binding is OneWay, so nothing ever puts it back. That is why the picker
            // rendered empty. See ProjectPicker.SameProjects.
            var projects = (await client.ProjectsAsync()).Projects;
            if (!ProjectPicker.SameProjects(KnownProjects, projects)) KnownProjects = projects;
        });

        if (ProjectFilter.Length == 0 && KnownProjects.Count > 0)
        {
            ProjectFilter = KnownProjects[0].ProductGuid;
        }

        var project = Blank(ProjectFilter);
        var tool = Blank(ToolFilter);

        await Attempt(async () =>
        {
            var result = await client.TracesSequencesAsync(
                project, tool, OutcomeFilter, MinDurationMsFilter, MaxDurationMsFilter);

            // Only publish a list that actually DIFFERS. Assigning an equivalent list still raises
            // PropertyChanged, which rebinds the ListBox, which discards the user's selection - and
            // at one poll every few seconds that made the highlight blink off and back continuously
            // (measured: present in only 14 of 30 samples over 24 seconds). Traces are historical
            // records, so most polls return exactly what is already on screen and this skips them.
            if (!SameSequences(Sequences, result.Sequences)) Sequences = result.Sequences;
            SequencesTruncated = result.Truncated;
        });

        await Attempt(async () => Failures = (await client.TracesFailuresAsync(project)).Failures);
        await Attempt(async () => SlowTools = (await client.TracesSlowAsync(project)).Tools);
    }

    /// <summary>
    /// Sets every filter EXCEPT the project and re-fetches at once rather than waiting for a tick.
    /// An empty tool string is normalised to absent here, so the server's own no-filter behaviour
    /// applies rather than this type keeping a stale distinction between "empty" and "absent".
    /// </summary>
    public async Task ApplyFiltersAsync(string tool, string? outcome, long? minDurationMs, long? maxDurationMs)
    {
        ToolFilter = tool;
        OutcomeFilter = outcome;
        MinDurationMsFilter = minDurationMs;
        MaxDurationMsFilter = maxDurationMs;

        await RefreshAsync();
    }

    /// <summary>
    /// Sets the project alone and re-fetches at once. A picker's selection is not something a user
    /// "applies" the way free-text filters are: it should take effect immediately, without also
    /// applying whatever unconfirmed text sits in the other fields.
    ///
    /// CLEARS THE SELECTED DETAIL FIRST. A selected call's spans are scoped to the project they came
    /// from; carrying them across a project switch shows one project's trace while the picker reads
    /// another. An ordinary tick never does this - only a deliberate project change ends a
    /// selection's lifetime.
    /// </summary>
    public async Task SelectProjectAsync(string productGuid)
    {
        ProjectFilter = productGuid;
        ClearSelectedTrace();

        await RefreshAsync();
    }

    /// <summary>
    /// GET /control/traces/{traceId} - span detail for one call, wherever its id came from: a
    /// sequence's own TraceIds, or a failed call's. Uses the current project filter, so a call
    /// selected while a filter is active resolves against that same project.
    /// </summary>
    /// <summary>
    /// Resolves a whole sequence into its individual calls, each with the outcome, duration and
    /// start offset needed to READ it rather than merely click it.
    ///
    /// <para><b>One request per call, deliberately.</b> A sequence row carries only parallel
    /// tool/traceId arrays - the API has no "give me this sequence's calls" route, and
    /// <c>GET /control/traces/{traceId}</c> is the only place a call's own outcome and timing live.
    /// They are fetched concurrently rather than in series so a nineteen-call sequence costs about
    /// one round trip, not nineteen.</para>
    ///
    /// <para><b>A call that cannot be fetched is dropped, not faked.</b> Trace retention can prune an
    /// individual trace out from under a sequence still listed above it; inventing a placeholder row
    /// with a guessed outcome would put a claim on screen the core never made. The remaining calls
    /// still render, which is more useful than failing the whole pane over one missing id.</para>
    /// </summary>
    public async Task SelectSequenceAsync(TraceSequenceRow sequence)
    {
        // Re-selecting the SAME sequence is what a refresh does every tick once the selection is
        // restored by id, and re-resolving every call each time would be one request per call per
        // poll forever. Only a genuine change of sequence does the work.
        if (sequence.Id == SelectedSequenceId
            && SelectedSequenceCalls.Kind is SequenceCallsFetchKind.Loaded or SequenceCallsFetchKind.Loading)
        {
            return;
        }

        SelectedSequenceId = sequence.Id;
        ClearSelectedTrace();

        if (_discover() is not { } connection)
        {
            SelectedSequenceCalls = SequenceCallsFetchState.NotSelected;
            return;
        }

        var count = Math.Min(sequence.Tools.Count, sequence.TraceIds.Count);
        if (count == 0)
        {
            SelectedSequenceCalls = SequenceCallsFetchState.Loaded([]);
            return;
        }

        SelectedSequenceCalls = SequenceCallsFetchState.Loading;

        try
        {
            var client = _makeClient(connection);
            var project = Blank(ProjectFilter);

            var fetches = new Task<TraceDetailResult?>[count];
            for (var i = 0; i < count; i++)
            {
                fetches[i] = FetchOrNull(client, sequence.TraceIds[i], project);
            }

            var details = await Task.WhenAll(fetches).ConfigureAwait(false);

            var calls = new List<SequenceCallRow>(count);
            for (var i = 0; i < count; i++)
            {
                if (details[i] is not { } detail) continue;

                calls.Add(new SequenceCallRow(
                    Position: i + 1,
                    Tool: sequence.Tools[i],
                    TraceId: sequence.TraceIds[i],
                    Outcome: detail.Outcome,
                    OffsetMs: Math.Max(0, detail.StartUtcMs - sequence.StartUtcMs),
                    DurationMs: detail.DurationMs));
            }

            SelectedSequenceCalls = SequenceCallsFetchState.Loaded(calls);
        }
        catch (ControlClientException ex)
        {
            SelectedSequenceCalls = SequenceCallsFetchState.Failed(ex.Message);
        }
    }

    static async Task<TraceDetailResult?> FetchOrNull(ITracesClient client, string traceId, string? project)
    {
        try
        {
            return await client.TraceDetailAsync(traceId, project).ConfigureAwait(false);
        }
        catch (ControlClientException)
        {
            // See SelectSequenceAsync: a pruned or unknown trace drops out of the list rather than
            // appearing as an invented row.
            return null;
        }
    }

    public async Task SelectTraceAsync(string traceId)
    {
        if (_discover() is not { } connection) return;

        try
        {
            SelectedTraceDetail = TraceDetailFetchState.Loaded(
                await _makeClient(connection).TraceDetailAsync(traceId, Blank(ProjectFilter)).ConfigureAwait(false));
        }
        catch (ControlClientException ex) when (ex.Error == ControlClientError.Server)
        {
            SelectedTraceDetail = TraceDetailFetchState.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transient: leave the detail exactly as it was - re-selecting can succeed later.
        }
    }

    public void ClearSelectedTrace() => SelectedTraceDetail = TraceDetailFetchState.NotSelected;

    /// <summary>
    /// Runs one of the four fetches under the shared self-heal contract: a server error with a
    /// message surfaces via <see cref="RefreshError"/>, anything else is swallowed, and in both cases
    /// this fetch's data is left exactly as it was so the other three are unaffected.
    /// </summary>
    async Task Attempt(Func<Task> fetch)
    {
        try
        {
            await fetch().ConfigureAwait(false);
        }
        catch (ControlClientException ex) when (ex.Error == ControlClientError.Server)
        {
            RefreshError = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Self-heals next tick.
        }
    }

    static string? Blank(string value) => value.Length == 0 ? null : value;

    static ITracesClient DefaultClient(ControlConnection connection) =>
        new ControlClientAdapter(new ControlClient(connection, new HttpClient(SharedHandler, disposeHandler: false)));

    sealed class ControlClientAdapter(ControlClient inner) : ITracesClient
    {
        public Task<ProjectsResult> ProjectsAsync() => inner.ProjectsAsync();

        public Task<TraceSequencesResult> TracesSequencesAsync(
            string? project, string? tool, string? outcome, long? minDurationMs, long? maxDurationMs) =>
            inner.TracesSequencesAsync(project, tool, outcome, minDurationMs, maxDurationMs);

        public Task<FailedCallsResult> TracesFailuresAsync(string? project) => inner.TracesFailuresAsync(project);
        public Task<SlowToolsResult> TracesSlowAsync(string? project) => inner.TracesSlowAsync(project);

        public Task<TraceDetailResult> TraceDetailAsync(string traceId, string? project) =>
            inner.TraceDetailAsync(traceId, project);
    }

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
