using Hades.Control.Client;
using Hades.Control.Client.Dtos;
using Hades.Shell.ViewModels;

namespace Hades.Shell.Tests;

/// <summary>
/// Asphodel's behaviour behind a fake client.
///
/// Memory is AUTHORED and irreplaceable - the graph, trace and memory-index databases are all
/// derived and can be rebuilt, but <c>memory/*.md</c> has no other copy. So the two destructive
/// actions here, saving a document and dismissing a proposal, are gated on an explicit `confirmed`
/// flag that is enforced in the view model itself: false never reaches the network. Accepting and
/// deferring need no gate, and the tests below pin that asymmetry so nobody "tidies" it away.
/// </summary>
public class MemoryViewModelTests
{
    sealed class FakeMemoryClient : IMemoryClient
    {
        public Func<Task<ProjectsResult>> OnProjects { get; set; } =
            () => Task.FromResult(new ProjectsResult { Projects = [] });

        public Func<Task<MemoryResult>> OnMemory { get; set; } =
            () => Task.FromResult(new MemoryResult { Documents = [], Proposals = [] });

        public Func<string, Task<MemoryDocumentResult>>? OnDocument { get; set; }
        public Func<Task<ActionResult>>? OnWrite { get; set; }
        public Func<Task<ActionResult>>? OnAccept { get; set; }
        public Func<Task<ActionResult>>? OnDefer { get; set; }
        public Func<Task<ActionResult>>? OnDismiss { get; set; }

        public int WriteCalls { get; private set; }
        public int DismissCalls { get; private set; }
        public bool? SeenDismissConfirm { get; private set; }
        public string? SeenProject { get; private set; }
        public string? SeenWrittenContent { get; private set; }

        public Task<ProjectsResult> ProjectsAsync() => OnProjects();

        public Task<MemoryResult> MemoryAsync(string? project)
        {
            SeenProject = project;
            return OnMemory();
        }

        public Task<MemoryDocumentResult> MemoryDocumentAsync(string name, string? project) =>
            OnDocument?.Invoke(name) ?? Task.FromResult(new MemoryDocumentResult { Name = name, Content = "# " + name });

        public Task<ActionResult> WriteMemoryDocumentAsync(string name, string content, string? project)
        {
            WriteCalls++;
            SeenWrittenContent = content;
            return OnWrite?.Invoke() ?? Task.FromResult(Action("saved"));
        }

        public Task<ActionResult> AcceptMemoryProposalAsync(string fileName, string? project) =>
            OnAccept?.Invoke() ?? Task.FromResult(Action("accepted"));

        public Task<ActionResult> DeferMemoryProposalAsync(string fileName, string? project) =>
            OnDefer?.Invoke() ?? Task.FromResult(Action("deferred"));

        public Task<ActionResult> DismissMemoryProposalAsync(string fileName, bool confirm, string? project)
        {
            DismissCalls++;
            SeenDismissConfirm = confirm;
            return OnDismiss?.Invoke() ?? Task.FromResult(Action("dismissed"));
        }
    }

    static ActionResult Action(string message) => new() { Success = true, Message = message };

    static readonly ControlConnection AConnection = new() { Port = 1234, Token = "t" };

    static ControlClientException ServerError(string message) =>
        new(ControlClientError.Server, message, statusCode: 400);

    static MemoryDocumentRow Document(string name) => new()
    {
        Name = name,
        SizeBytes = 120,
        SizeDisplay = "120 B",
    };

    static MemoryProposalRow Proposal(string fileName) => new()
    {
        FileName = fileName,
        TargetFile = "Conventions.md",
        Rationale = "Observed the same pattern three times.",
        Status = "pending",
        Content = "- Prefer X over Y.",
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

    static (MemoryViewModel Vm, FakeMemoryClient Client) NewSubject()
    {
        var client = new FakeMemoryClient();
        return (new MemoryViewModel(() => AConnection, _ => client), client);
    }

    // ---- refresh ------------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_LoadsDocumentsAndProposals()
    {
        var (vm, client) = NewSubject();
        client.OnMemory = () => Task.FromResult(new MemoryResult
        {
            Documents = [Document("Conventions.md")],
            Proposals = [Proposal("p-1.md")],
        });

        await vm.RefreshAsync();

        Assert.Single(vm.Documents);
        Assert.Single(vm.Proposals);
    }

    [Fact]
    public async Task Refresh_ATransientFailureKeepsWhatIsOnScreen()
    {
        var (vm, client) = NewSubject();
        client.OnMemory = () => Task.FromResult(new MemoryResult
        {
            Documents = [Document("Conventions.md")],
            Proposals = [],
        });
        await vm.RefreshAsync();

        client.OnMemory = () => throw new ControlClientException(ControlClientError.Transport, "blip");
        await vm.RefreshAsync();

        Assert.Single(vm.Documents);
    }

    [Fact]
    public async Task Refresh_AServerMessageSurfacesAsRefreshError()
    {
        var (vm, client) = NewSubject();
        client.OnMemory = () => throw ServerError("Hades knows 3 projects, so this call needs a 'project' argument.");

        await vm.RefreshAsync();

        Assert.Equal("Hades knows 3 projects, so this call needs a 'project' argument.", vm.RefreshError);
    }

    [Fact]
    public async Task RefreshError_ClearsOnceALaterRefreshSucceeds()
    {
        var (vm, client) = NewSubject();
        client.OnMemory = () => throw ServerError("needs a project");
        await vm.RefreshAsync();
        Assert.NotNull(vm.RefreshError);

        client.OnMemory = () => Task.FromResult(new MemoryResult { Documents = [], Proposals = [] });
        await vm.RefreshAsync();

        Assert.Null(vm.RefreshError);
    }

    /// <summary>
    /// RefreshError and LastActionMessage are deliberately separate. A passive poll failure
    /// overwriting a just-seen action success - or the reverse - would be actively misleading: each
    /// reflects only its own kind of attempt.
    /// </summary>
    [Fact]
    public async Task ARefreshFailure_DoesNotOverwriteTheLastActionMessage()
    {
        var (vm, client) = NewSubject();
        await vm.AcceptProposalAsync("p-1.md");
        Assert.Equal("accepted", vm.LastActionMessage);

        client.OnMemory = () => throw ServerError("needs a project");
        await vm.RefreshAsync();

        Assert.Equal("accepted", vm.LastActionMessage);
        Assert.Equal("needs a project", vm.RefreshError);
    }

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

    // ---- documents ----------------------------------------------------------------------------

    [Fact]
    public async Task SelectDocument_FetchesItsContent()
    {
        var (vm, client) = NewSubject();
        client.OnDocument = name => Task.FromResult(new MemoryDocumentResult { Name = name, Content = "# Conventions" });

        await vm.SelectDocumentAsync("Conventions.md");

        Assert.Equal(MemoryDocumentFetchKind.Loaded, vm.SelectedDocument.Kind);
        Assert.Equal("# Conventions", vm.SelectedDocument.Document!.Content);
    }

    [Fact]
    public async Task SelectDocument_AServerFailureShowsTheServersOwnMessage()
    {
        var (vm, client) = NewSubject();
        client.OnDocument = _ => throw ServerError("'Missing.md' does not exist yet.");

        await vm.SelectDocumentAsync("Missing.md");

        Assert.Equal(MemoryDocumentFetchKind.Failed, vm.SelectedDocument.Kind);
        Assert.Equal("'Missing.md' does not exist yet.", vm.SelectedDocument.Message);
    }

    /// <summary>
    /// An open document is a fixed snapshot for as long as it is being read or edited. A tick must
    /// not silently overwrite it out from under an in-progress edit.
    /// </summary>
    [Fact]
    public async Task Refresh_DoesNotTouchAnOpenDocument()
    {
        var (vm, _) = NewSubject();
        await vm.SelectDocumentAsync("Conventions.md");

        await vm.RefreshAsync();

        Assert.Equal(MemoryDocumentFetchKind.Loaded, vm.SelectedDocument.Kind);
    }

    /// <summary>
    /// A document read under the old project must not keep rendering once the picker reads another.
    /// </summary>
    [Fact]
    public async Task SelectProject_ClearsTheOpenDocument()
    {
        var (vm, _) = NewSubject();
        await vm.SelectDocumentAsync("Conventions.md");

        await vm.SelectProjectAsync("guid-2");

        Assert.Equal(MemoryDocumentFetchKind.NotSelected, vm.SelectedDocument.Kind);
    }

    // ---- the destructive actions and their gates ----------------------------------------------

    /// <summary>
    /// A save OVERWRITES an authored file with no merge and no version history. `confirmed` is the
    /// gate itself: false never reaches the network.
    /// </summary>
    [Fact]
    public async Task SaveDocument_WithoutConfirmation_NeverReachesTheNetwork()
    {
        var (vm, client) = NewSubject();

        await vm.SaveDocumentAsync("Conventions.md", "replacement", confirmed: false);

        Assert.Equal(0, client.WriteCalls);
        Assert.Null(vm.LastActionMessage);
    }

    [Fact]
    public async Task SaveDocument_WhenConfirmed_WritesAndRecordsTheServersMessage()
    {
        var (vm, client) = NewSubject();
        client.OnWrite = () => Task.FromResult(Action("Wrote 'Conventions.md'."));

        await vm.SaveDocumentAsync("Conventions.md", "replacement", confirmed: true);

        Assert.Equal(1, client.WriteCalls);
        Assert.Equal("replacement", client.SeenWrittenContent);
        Assert.Equal("Wrote 'Conventions.md'.", vm.LastActionMessage);
    }

    /// <summary>
    /// Dismiss DELETES the proposal file. The core refuses without confirm=true as well, so this is
    /// defence in depth - but the client-side gate is what makes it provable here.
    /// </summary>
    [Fact]
    public async Task DismissProposal_WithoutConfirmation_NeverReachesTheNetwork()
    {
        var (vm, client) = NewSubject();

        await vm.DismissProposalAsync("p-1.md", confirmed: false);

        Assert.Equal(0, client.DismissCalls);
    }

    [Fact]
    public async Task DismissProposal_WhenConfirmed_SendsConfirmTrue()
    {
        var (vm, client) = NewSubject();

        await vm.DismissProposalAsync("p-1.md", confirmed: true);

        Assert.Equal(1, client.DismissCalls);
        Assert.True(client.SeenDismissConfirm);
    }

    // ---- the non-destructive actions, which deliberately have NO gate -------------------------

    /// <summary>
    /// Accepting only ever APPENDS to the target document, creating it if missing, so it needs no
    /// confirmation. Pinned so the gate is not "helpfully" added later.
    /// </summary>
    [Fact]
    public async Task AcceptProposal_NeedsNoConfirmation()
    {
        var (vm, client) = NewSubject();
        client.OnAccept = () => Task.FromResult(Action("Appended to 'Conventions.md'."));

        await vm.AcceptProposalAsync("p-1.md");

        Assert.Equal("Appended to 'Conventions.md'.", vm.LastActionMessage);
    }

    [Fact]
    public async Task DeferProposal_NeedsNoConfirmation()
    {
        var (vm, client) = NewSubject();
        client.OnDefer = () => Task.FromResult(Action("Deferred."));

        await vm.DeferProposalAsync("p-1.md");

        Assert.Equal("Deferred.", vm.LastActionMessage);
    }

    [Fact]
    public async Task AFailedAction_RecordsTheServersOwnMessage()
    {
        var (vm, client) = NewSubject();
        client.OnAccept = () => throw ServerError("The proposal no longer exists.");

        await vm.AcceptProposalAsync("p-1.md");

        Assert.Equal("The proposal no longer exists.", vm.LastActionMessage);
    }

    [Theory]
    [InlineData(ControlClientError.Transport)]
    [InlineData(ControlClientError.StaleToken)]
    public async Task AFailureWithNoServerText_LeavesTheLastMessageAlone(ControlClientError error)
    {
        var (vm, client) = NewSubject();
        await vm.DeferProposalAsync("p-1.md");
        Assert.Equal("deferred", vm.LastActionMessage);

        client.OnAccept = () => throw new ControlClientException(error, "client-side detail");
        await vm.AcceptProposalAsync("p-1.md");

        Assert.Equal("deferred", vm.LastActionMessage);
    }
}
