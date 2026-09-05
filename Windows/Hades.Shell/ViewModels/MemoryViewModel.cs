using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Hades.Control.Client;
using Hades.Control.Client.Dtos;

namespace Hades.Shell.ViewModels;

/// <summary>The memory surface of the control API. A seam, so the view model can be driven through
/// every failure without a running core.</summary>
public interface IMemoryClient
{
    Task<ProjectsResult> ProjectsAsync();
    Task<MemoryResult> MemoryAsync(string? project);
    Task<MemoryDocumentResult> MemoryDocumentAsync(string name, string? project);
    Task<ActionResult> WriteMemoryDocumentAsync(string name, string content, string? project);
    Task<ActionResult> AcceptMemoryProposalAsync(string fileName, string? project);
    Task<ActionResult> DeferMemoryProposalAsync(string fileName, string? project);
    Task<ActionResult> DismissMemoryProposalAsync(string fileName, bool confirm, string? project);
}

/// <summary>
/// Owns the Asphodel (memory) section's own fetch and published state. The port of
/// <c>Mac/HadesApp/Sources/HadesApp/MainWindow/MemoryViewModel.swift</c>.
///
/// <b>MEMORY IS AUTHORED AND IRREPLACEABLE - the sharpest instance of that distinction in this app.</b>
/// The graph, trace and memory-index databases are all derived and can be rebuilt from the project;
/// <c>memory/*.md</c> is written by hand and has no other copy. Two actions here destroy or overwrite
/// something real - <see cref="SaveDocumentAsync"/> and <see cref="DismissProposalAsync"/> - and both
/// take an explicit <c>confirmed</c> gate enforced HERE, not merely by whatever dialog sets it:
/// false never reaches the network. That is the same "confirmed is the gate itself, not a hint"
/// discipline <see cref="ProjectsViewModel.RemoveProjectAsync"/> already holds to.
///
/// <see cref="AcceptProposalAsync"/> and <see cref="DeferProposalAsync"/> deliberately have NO gate,
/// and that asymmetry is the point: accepting only ever APPENDS to a document (creating it if
/// missing) and deferring is pure bookkeeping that never touches an authored file at all. Gating
/// them would train the user to click through confirmations that never mattered, which is how the
/// two that do matter stop being read.
/// </summary>
public sealed class MemoryViewModel : INotifyPropertyChanged
{
    // See TrayViewModel for why the handler is shared and the HttpClient is not.
    static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 4,
    };

    readonly Func<ControlConnection?> _discover;
    readonly Func<ControlConnection, IMemoryClient> _makeClient;

    IReadOnlyList<MemoryDocumentRow> _documents = [];
    IReadOnlyList<MemoryProposalRow> _proposals = [];
    IReadOnlyList<ProjectRow> _knownProjects = [];
    MemoryDocumentFetchState _selectedDocument = MemoryDocumentFetchState.NotSelected;
    string _projectFilter = string.Empty;
    string? _refreshError;
    string? _lastActionMessage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MemoryViewModel(
        Func<ControlConnection?> discover,
        Func<ControlConnection, IMemoryClient>? makeClient = null)
    {
        _discover = discover;
        _makeClient = makeClient ?? DefaultClient;
    }

    public IReadOnlyList<MemoryDocumentRow> Documents
    {
        get => _documents;
        private set { _documents = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<MemoryProposalRow> Proposals
    {
        get => _proposals;
        private set { _proposals = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<ProjectRow> KnownProjects
    {
        get => _knownProjects;
        private set { _knownProjects = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The currently open document. Populated only by <see cref="SelectDocumentAsync"/>, never by
    /// <see cref="RefreshAsync"/> - see <see cref="MemoryDocumentFetchState"/> for why a tick must
    /// not overwrite it. Cleared by <see cref="SelectProjectAsync"/>.
    /// </summary>
    public MemoryDocumentFetchState SelectedDocument
    {
        get => _selectedDocument;
        private set { _selectedDocument = value; OnPropertyChanged(); }
    }

    /// <summary>Which project every call scopes to; empty means nothing explicitly chosen, in which
    /// case the server auto-resolves a single known project or reports its own ambiguity error.</summary>
    public string ProjectFilter
    {
        get => _projectFilter;
        private set { _projectFilter = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The most recent REFRESH failure the shell cannot act on silently, verbatim.
    ///
    /// Deliberately separate from <see cref="LastActionMessage"/>: that is the most recent ACTION's
    /// own result, and a passive poll failure overwriting a just-seen action success - or the
    /// reverse - would be actively misleading. Each reflects only its own kind of attempt.
    /// </summary>
    public string? RefreshError
    {
        get => _refreshError;
        private set { _refreshError = value; OnPropertyChanged(); }
    }

    /// <summary>The most recent action's server-authored result, verbatim. Never text invented
    /// here: a failure with no server message leaves this exactly as it was.</summary>
    public string? LastActionMessage
    {
        get => _lastActionMessage;
        private set { _lastActionMessage = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Projects, then documents-and-proposals together in the one round trip that endpoint provides.
    /// Transient failures self-heal, leaving what is on screen untouched; a server error carrying a
    /// message surfaces via <see cref="RefreshError"/> instead, which is recomputed fresh each time.
    /// The projects fetch runs first so <see cref="ProjectFilter"/>'s default resolves within this
    /// same tick.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_discover() is not { } connection) return;

        var client = _makeClient(connection);
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

        await Attempt(async () =>
        {
            var result = await client.MemoryAsync(Project());

            Documents = result.Documents;
            Proposals = result.Proposals;
        });
    }

    /// <summary>
    /// Sets the project and re-fetches at once. Clears the open document first, for the same reason
    /// Charon clears a selected trace: a document read under the old project must not keep rendering
    /// once the picker reads a different one.
    /// </summary>
    public async Task SelectProjectAsync(string productGuid)
    {
        ProjectFilter = productGuid;
        ClearSelectedDocument();

        await RefreshAsync();
    }

    /// <summary>GET /control/memory/document - one document's complete raw text, for reading or
    /// editing. User-initiated and never polled.</summary>
    public async Task SelectDocumentAsync(string name)
    {
        if (_discover() is not { } connection) return;

        try
        {
            SelectedDocument = MemoryDocumentFetchState.Loaded(
                await _makeClient(connection).MemoryDocumentAsync(name, Project()).ConfigureAwait(false));
        }
        catch (ControlClientException ex) when (ex.Error == ControlClientError.Server)
        {
            SelectedDocument = MemoryDocumentFetchState.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transient: leave it exactly as it was - re-selecting can succeed later.
        }
    }

    public void ClearSelectedDocument() => SelectedDocument = MemoryDocumentFetchState.NotSelected;

    /// <summary>
    /// POST /control/memory/document - OVERWRITES the document. There is no merge and no version
    /// history: the core writes atomically over whatever was there.
    ///
    /// <paramref name="confirmed"/> is the gate itself, not a hint. False never reaches the network,
    /// which is what makes "never overwrite an authored file without confirming" provable here
    /// rather than only trusted of the dialog that sets it.
    /// </summary>
    public async Task SaveDocumentAsync(string name, string content, bool confirmed)
    {
        if (!confirmed) return;

        await RunAction(client => client.WriteMemoryDocumentAsync(name, content, Project()));
    }

    /// <summary>POST /control/memory/proposals/accept. No gate: accepting only ever appends.</summary>
    public Task AcceptProposalAsync(string fileName) =>
        RunAction(client => client.AcceptMemoryProposalAsync(fileName, Project()));

    /// <summary>POST /control/memory/proposals/defer. No gate: pure bookkeeping.</summary>
    public Task DeferProposalAsync(string fileName) =>
        RunAction(client => client.DeferMemoryProposalAsync(fileName, Project()));

    /// <summary>
    /// POST /control/memory/proposals/dismiss - DELETES the proposal file.
    ///
    /// The core also refuses without confirm=true, so this is defence in depth rather than the only
    /// gate; confirm=true is only ever sent once this method's own guard has passed.
    /// </summary>
    public async Task DismissProposalAsync(string fileName, bool confirmed)
    {
        if (!confirmed) return;

        await RunAction(client => client.DismissMemoryProposalAsync(fileName, confirm: true, Project()));
    }

    async Task RunAction(Func<IMemoryClient, Task<ActionResult>> action)
    {
        if (_discover() is not { } connection) return;

        try
        {
            LastActionMessage = (await action(_makeClient(connection)).ConfigureAwait(false)).Message;
        }
        catch (ControlClientException ex) when (ex.Error == ControlClientError.Server)
        {
            LastActionMessage = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing server-authored to show, so leave the last message exactly as it was rather
            // than clearing it or inventing text.
        }
    }

    /// <summary>One fetch under the shared self-heal contract - see <see cref="RefreshAsync"/>.</summary>
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

    string? Project() => ProjectFilter.Length == 0 ? null : ProjectFilter;

    static IMemoryClient DefaultClient(ControlConnection connection) =>
        new ControlClientAdapter(new ControlClient(connection, new HttpClient(SharedHandler, disposeHandler: false)));

    sealed class ControlClientAdapter(ControlClient inner) : IMemoryClient
    {
        public Task<ProjectsResult> ProjectsAsync() => inner.ProjectsAsync();
        public Task<MemoryResult> MemoryAsync(string? project) => inner.MemoryAsync(project);

        public Task<MemoryDocumentResult> MemoryDocumentAsync(string name, string? project) =>
            inner.MemoryDocumentAsync(name, project);

        public Task<ActionResult> WriteMemoryDocumentAsync(string name, string content, string? project) =>
            inner.WriteMemoryDocumentAsync(name, content, project);

        public Task<ActionResult> AcceptMemoryProposalAsync(string fileName, string? project) =>
            inner.AcceptMemoryProposalAsync(fileName, project);

        public Task<ActionResult> DeferMemoryProposalAsync(string fileName, string? project) =>
            inner.DeferMemoryProposalAsync(fileName, project);

        public Task<ActionResult> DismissMemoryProposalAsync(string fileName, bool confirm, string? project) =>
            inner.DismissMemoryProposalAsync(fileName, confirm, project);
    }

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
