using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Hades.Control.Client;
using Hades.Control.Client.Dtos;

namespace Hades.Shell.ViewModels;

/// <summary>The control-API surface the Projects section needs. A seam, so the view model can be
/// driven through every failure without a running core - Swift's <c>ControlProjectsFetching</c>
/// plays the same role.</summary>
public interface IProjectsClient
{
    Task<ProjectsResult> ProjectsAsync();
    Task<ProjectRow> AddProjectAsync(string path);
    Task<ActionResult> RemoveProjectAsync(string productGuid);
    Task<RebuildStartedResult> RebuildProjectAsync(string productGuid);
    Task<InstallPluginResult> InstallPluginAsync(string productGuid);
    Task<ActionResult> RevealInFinderAsync(string productGuid);
    Task<ActionResult> OpenInUnityAsync(string productGuid);
    Task<OperationResult> OperationAsync(string id);
}

/// <summary>
/// Owns the Projects section's own fetch and published state - nothing else. The port of
/// <c>Mac/HadesApp/Sources/HadesApp/MainWindow/ProjectsViewModel.swift</c>.
///
/// Per the settled data-ownership split, <see cref="MainWindowViewModel"/> owns navigation and the
/// polling LIFECYCLE only; each section owns its own view model and its own fetch.
/// <see cref="RefreshAsync"/> is what <see cref="MainWindowViewModel.RefreshSelectedSection"/> calls
/// once per tick, and only while Projects is the selected section. This type never starts a timer of
/// its own: a section not currently selected has no business polling.
///
/// It holds no state a view could turn into new display text. <see cref="Projects"/> is
/// <c>ProjectsResult.Projects</c> unchanged, and every message shown is server-authored - see
/// <see cref="LastActionMessage"/>. A fetch failure leaves what is on screen exactly as it was:
/// one unlucky poll must not flash a populated list back to empty.
/// </summary>
public sealed class ProjectsViewModel : INotifyPropertyChanged
{
    // One handler for every client built here, for the reason TrayViewModel documents at length:
    // ControlClient's constructor rewrites BaseAddress and Authorization, and HttpClient throws on
    // both once it has sent a request, so a shared HttpClient breaks after the first call.
    static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 4,
    };

    readonly Func<ControlConnection?> _discover;
    readonly Func<ControlConnection, IProjectsClient> _makeClient;

    /// <summary>
    /// productGuid -> operationId, for every rebuild <see cref="RefreshAsync"/> should keep polling.
    /// Removed the instant an operation reaches a terminal state or is found pruned.
    /// </summary>
    readonly Dictionary<string, string> _trackedOperationIds = [];

    readonly Dictionary<string, OperationProgress> _rebuildProgress = [];

    IReadOnlyList<ProjectRow> _projects = [];
    string? _lastActionMessage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProjectsViewModel(
        Func<ControlConnection?> discover,
        Func<ControlConnection, IProjectsClient>? makeClient = null)
    {
        _discover = discover;
        _makeClient = makeClient ?? DefaultClient;
    }

    public IReadOnlyList<ProjectRow> Projects
    {
        get => _projects;
        private set { _projects = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The most recent action's server-authored result text, verbatim. Shared across all six actions
    /// rather than one property per action: at most one is ever in flight from this view at a time,
    /// and a single "last thing that happened" is one published fact rather than six.
    ///
    /// NEVER text invented here. A transport, stale-token or decoding failure has no server text to
    /// show, so it leaves this exactly as it was rather than clearing it or making something up.
    /// </summary>
    public string? LastActionMessage
    {
        get => _lastActionMessage;
        private set { _lastActionMessage = value; OnPropertyChanged(); }
    }

    /// <summary>One polled rebuild's display state per project. Populated by
    /// <see cref="RefreshAsync"/>, never by <see cref="RebuildProjectAsync"/>, which only starts
    /// tracking.</summary>
    public IReadOnlyDictionary<string, OperationProgress> RebuildProgress => _rebuildProgress;

    /// <summary>
    /// GET /control/projects, plus re-polling every tracked rebuild - one discovery read and one
    /// client for both, since both belong to the same tick. A projects fetch failure is swallowed:
    /// the next tick re-reads discovery and re-fetches on its own.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_discover() is not { } connection) return;

        var client = _makeClient(connection);

        try
        {
            Projects = (await client.ProjectsAsync().ConfigureAwait(false)).Projects;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Self-heals next tick - see this method's own doc comment. Nothing to do here.
        }

        await PollTrackedOperationsAsync(client).ConfigureAwait(false);
    }

    // ---- Actions ------------------------------------------------------------------------------
    //
    // Every action below: discover a connection, call the one matching route, and either record its
    // server-authored message or start tracking (rebuild only). None derives eligibility from a
    // ProjectRow's warnings or editor state, because none of them receives a ProjectRow at all -
    // only a bare productGuid. A discover() failure is swallowed the same way a refresh is: there is
    // no server text to show for a call that was never made.

    /// <summary>
    /// POST /control/projects/add. The response is a bare ProjectRow with no message field - the row
    /// appearing IS the feedback, so no success text is invented here.
    ///
    /// It refreshes EXPLICITLY rather than waiting for a tick. Relying on the next tick is true only
    /// where something drives one; onboarding drives no tick at all, so an add there completed
    /// server-side and left "No projects yet" on screen - the success real and entirely invisible.
    /// Refreshing here makes the feedback a property of the action rather than of whoever happens to
    /// be polling.
    /// </summary>
    /// <returns>
    /// The id of the operation indexing the new project, or null when the add failed. The server
    /// now registers the project and indexes it in the background rather than blocking until the
    /// walk is done, so a caller that wants to show progress polls this; a caller that does not
    /// simply ignores it and behaves exactly as before.
    /// </returns>
    public async Task<string?> AddProjectAsync(string path)
    {
        if (_discover() is not { } connection) return null;

        try
        {
            var row = await _makeClient(connection).AddProjectAsync(path).ConfigureAwait(false);

            // A previous failure's text must not outlive the success that replaced it - otherwise
            // "not a Unity project" stays on screen above the row that just added fine.
            LastActionMessage = null;
            await RefreshAsync().ConfigureAwait(false);

            return row.IndexOperationId;
        }
        catch (Exception ex)
        {
            RecordServerMessage(ex);
            return null;
        }
    }

    /// <summary>One poll of an operation, for a caller showing its progress. Returns null when the
    /// operation is unknown or unreachable, which the caller treats as "stop polling" rather than
    /// as a failure worth wording - the add itself already succeeded.</summary>
    public async Task<OperationResult?> OperationAsync(string operationId)
    {
        if (_discover() is not { } connection) return null;

        try
        {
            return await _makeClient(connection).OperationAsync(operationId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// POST /control/projects/{productGuid}/remove. <paramref name="confirmed"/> is the gate itself,
    /// not a hint: false never reaches the network at all. That is what makes "never remove without
    /// confirming" provable here rather than merely trusted of the call site.
    /// </summary>
    public async Task RemoveProjectAsync(string productGuid, bool confirmed)
    {
        if (!confirmed) return;
        if (_discover() is not { } connection) return;

        try
        {
            LastActionMessage = (await _makeClient(connection).RemoveProjectAsync(productGuid)
                .ConfigureAwait(false)).Message;
        }
        catch (Exception ex)
        {
            RecordServerMessage(ex);
        }
    }

    /// <summary>
    /// POST /control/projects/{productGuid}/rebuild. Registers the returned operation id for
    /// <see cref="RefreshAsync"/> to poll; does not poll it itself. Clears any stale progress entry
    /// first, so a previous rebuild's frozen state cannot linger on screen while the new one is
    /// still unpolled.
    /// </summary>
    public async Task RebuildProjectAsync(string productGuid)
    {
        if (_discover() is not { } connection) return;

        try
        {
            var started = await _makeClient(connection).RebuildProjectAsync(productGuid).ConfigureAwait(false);

            _rebuildProgress.Remove(productGuid);
            _trackedOperationIds[productGuid] = started.OperationId;
            OnPropertyChanged(nameof(RebuildProgress));
        }
        catch (Exception ex)
        {
            RecordServerMessage(ex);
        }
    }

    /// <summary>
    /// POST /control/projects/{productGuid}/installPlugin. The result's message already says whether
    /// a restart is needed in plain language; this renders it verbatim rather than re-stating
    /// needsRestart as separate text of its own.
    /// </summary>
    /// <returns>
    /// Whether the server reported success. Written out rather than routed through
    /// <see cref="RunAction"/> because that helper keeps only the message, and success and failure
    /// both land in <see cref="LastActionMessage"/> - so a caller could not tell them apart. The
    /// onboarding step needs to: it marks a row "✓ Installed", and doing that on a refusal would be
    /// both wrong and reassuring. The <c>success</c> flag was already on the wire and simply discarded.
    /// </returns>
    public async Task<bool> InstallPluginAsync(string productGuid)
    {
        if (_discover() is not { } connection) return false;

        try
        {
            var result = await _makeClient(connection).InstallPluginAsync(productGuid).ConfigureAwait(false);
            LastActionMessage = result.Message;
            return result.Success;
        }
        catch (Exception ex)
        {
            RecordServerMessage(ex);
            return false;
        }
    }

    /// <summary>
    /// POST /control/projects/{productGuid}/revealInFinder. Named for Explorer here because that is
    /// what a Windows user sees; the ROUTE keeps its macOS name because it is the server's, and the
    /// server decides what revealing means per platform.
    /// </summary>
    public Task RevealInExplorerAsync(string productGuid) =>
        RunAction(productGuid, async client => (await client.RevealInFinderAsync(productGuid)).Message);

    /// <summary>POST /control/projects/{productGuid}/openInUnity.</summary>
    public Task OpenInUnityAsync(string productGuid) =>
        RunAction(productGuid, async client => (await client.OpenInUnityAsync(productGuid)).Message);

    async Task RunAction(string productGuid, Func<IProjectsClient, Task<string>> action)
    {
        if (_discover() is not { } connection) return;

        try
        {
            LastActionMessage = await action(_makeClient(connection)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordServerMessage(ex);
        }
    }

    /// <summary>
    /// Re-polls every rebuild this view model is currently tracking. A terminal state or a pruned
    /// (404) result stops tracking that project; any other failure leaves the progress exactly as it
    /// was and keeps tracking, retrying on the next tick.
    /// </summary>
    async Task PollTrackedOperationsAsync(IProjectsClient client)
    {
        // Snapshot: the loop removes from _trackedOperationIds as operations finish.
        foreach (var (productGuid, operationId) in _trackedOperationIds.ToArray())
        {
            try
            {
                var result = await client.OperationAsync(operationId).ConfigureAwait(false);

                _rebuildProgress[productGuid] = OperationProgress.Tracked(result);
                if (result.State != OperationState.Running)
                {
                    _trackedOperationIds.Remove(productGuid);
                }
            }
            catch (ControlClientException ex) when (ex.Error == ControlClientError.Server && ex.StatusCode == 404)
            {
                // Ordinary, not a failure: completed operations are pruned after five minutes.
                _rebuildProgress[productGuid] = OperationProgress.Pruned(ex.Message);
                _trackedOperationIds.Remove(productGuid);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Transient. Leave the progress as-is, keep tracking, retry next tick.
            }
        }

        OnPropertyChanged(nameof(RebuildProgress));
    }

    /// <summary>
    /// The shared tail of every action. A server error is the one failure case carrying text meant
    /// to be shown; every other case has nothing to render, so the message is left exactly as it was
    /// rather than cleared or replaced with text invented here.
    /// </summary>
    void RecordServerMessage(Exception exception)
    {
        if (exception is ControlClientException { Error: ControlClientError.Server } ex)
        {
            LastActionMessage = ex.Message;
        }
    }

    static IProjectsClient DefaultClient(ControlConnection connection) =>
        new ControlClientAdapter(new ControlClient(connection, new HttpClient(SharedHandler, disposeHandler: false)));

    /// <summary>Adapts the sealed <see cref="ControlClient"/> onto <see cref="IProjectsClient"/>.
    /// Adds nothing.</summary>
    sealed class ControlClientAdapter(ControlClient inner) : IProjectsClient
    {
        public Task<ProjectsResult> ProjectsAsync() => inner.ProjectsAsync();
        public Task<ProjectRow> AddProjectAsync(string path) => inner.AddProjectAsync(path);
        public Task<ActionResult> RemoveProjectAsync(string productGuid) => inner.RemoveProjectAsync(productGuid);
        public Task<RebuildStartedResult> RebuildProjectAsync(string productGuid) => inner.RebuildProjectAsync(productGuid);
        public Task<InstallPluginResult> InstallPluginAsync(string productGuid) => inner.InstallPluginAsync(productGuid);
        public Task<ActionResult> RevealInFinderAsync(string productGuid) => inner.RevealInFinderAsync(productGuid);
        public Task<ActionResult> OpenInUnityAsync(string productGuid) => inner.OpenInUnityAsync(productGuid);
        public Task<OperationResult> OperationAsync(string id) => inner.OperationAsync(id);
    }

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
