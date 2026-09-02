using Hades.Control.Client;
using Hades.Control.Client.Dtos;
using Hades.Shell.ViewModels;
using Hades.Shell.Onboarding;

namespace Hades.Shell.Tests;

public class OnboardingViewModelTests
{
    sealed class FakeVerifier : IClaudeCodeVerifier
    {
        public ClaudeCodeVerification Result { get; set; } = ClaudeCodeVerification.Unreachable(0);
        public int Calls { get; private set; }

        public Task<ClaudeCodeVerification> VerifyAsync()
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    sealed class FakeCompletionStore : IOnboardingCompletionStore
    {
        public bool HasCompletedOnboarding { get; private set; }
        public void MarkCompleted() => HasCompletedOnboarding = true;
    }

    /// <summary>Enough of <see cref="IProjectsClient"/> for the two steps that act on projects.
    /// Records what was asked of it, which is the point: the bug being pinned here was a step whose
    /// copy asked the user to do something the app never called through for.</summary>
    sealed class FakeProjectsClient : IProjectsClient
    {
        public List<string> AddedPaths { get; } = [];
        public List<string> InstalledInto { get; } = [];
        public IReadOnlyList<ProjectRow> Rows { get; set; } = [];
        public Func<string, Task<ProjectRow>>? OnAdd { get; set; }

        public Task<ProjectsResult> ProjectsAsync() =>
            Task.FromResult(new ProjectsResult { Projects = Rows });

        public Task<ProjectRow> AddProjectAsync(string path)
        {
            AddedPaths.Add(path);
            return OnAdd?.Invoke(path) ?? Task.FromResult(Row(path, "guid-1"));
        }

        public Task<InstallPluginResult> InstallPluginAsync(string productGuid)
        {
            InstalledInto.Add(productGuid);
            return Task.FromResult(new InstallPluginResult
            {
                Success = true, NeedsRestart = false, Message = "Plugin installed.",
            });
        }

        public Task<ActionResult> RemoveProjectAsync(string productGuid) => throw new NotSupportedException();
        public Task<RebuildStartedResult> RebuildProjectAsync(string productGuid) => throw new NotSupportedException();
        public Task<ActionResult> RevealInFinderAsync(string productGuid) => throw new NotSupportedException();
        public Task<ActionResult> OpenInUnityAsync(string productGuid) => throw new NotSupportedException();
        public Task<OperationResult> OperationAsync(string id) => throw new NotSupportedException();
    }

    static ProjectRow Row(string path, string guid) => new()
    {
        Name = "MyGame",
        Path = path,
        ProductGuid = guid,
        IndexState = ProjectIndexState.Indexed,
        IndexStatus = "indexed just now",
        NodeCount = 1,
        EdgeCount = 0,
        Editor = new ProjectEditorInfo { State = ProjectEditorState.Absent, Status = "No Editor attached" },
        Warnings = [],
    };

    static readonly ControlConnection AConnection = new() { Port = 1234, Token = "t" };

    static (OnboardingViewModel Vm, FakeVerifier Verifier, FakeCompletionStore Store, FakeProjectsClient Client) NewSubject()
    {
        var verifier = new FakeVerifier();
        var store = new FakeCompletionStore();
        var client = new FakeProjectsClient();
        var projects = new ProjectsViewModel(() => AConnection, _ => client);
        return (new OnboardingViewModel(verifier, store, projects), verifier, store, client);
    }

    // ---- the affordance gap -----------------------------------------------------------------
    //
    // The Projects and Unity-plugin steps shipped with copy telling the user to add a project and
    // install the plugin, and no control that did either: OnboardingWindow had one action panel, for
    // the Claude Code check. Thirteen tests here passed the whole time, because every one of them
    // asked about step count, order or copy, and none asked whether a step could DO anything.

    /// <summary>
    /// The guard that would have caught it. Install is the only step with nothing to do; every other
    /// step's copy asks the user to act, so every other step must offer a way to.
    /// </summary>
    [Fact]
    public void OnlyTheInstallStepIsPurelyInformational()
    {
        var withoutAnAction = OnboardingViewModel.AllSteps
            .Where(step => OnboardingViewModel.ActionFor(step) == OnboardingAction.None)
            .ToArray();

        Assert.Equal([OnboardingStep.Install], withoutAnAction);
    }

    [Theory]
    [InlineData(OnboardingStep.ClaudeCode, OnboardingAction.VerifyClaudeCode)]
    [InlineData(OnboardingStep.Projects, OnboardingAction.AddProject)]
    [InlineData(OnboardingStep.UnityPlugin, OnboardingAction.InstallPlugin)]
    public void EachStepOffersTheActionItsCopyDescribes(OnboardingStep step, OnboardingAction expected) =>
        Assert.Equal(expected, OnboardingViewModel.ActionFor(step));

    [Fact]
    public void CurrentActionTracksTheStep()
    {
        var (vm, _, _, _) = NewSubject();

        Assert.Equal(OnboardingAction.None, vm.CurrentAction);
        vm.Advance();
        Assert.Equal(OnboardingAction.VerifyClaudeCode, vm.CurrentAction);
        vm.Advance();
        Assert.Equal(OnboardingAction.AddProject, vm.CurrentAction);
        vm.Advance();
        Assert.Equal(OnboardingAction.InstallPlugin, vm.CurrentAction);
    }

    /// <summary>Onboarding reaches the same add the Projects section uses, through the view model it
    /// is handed - not a second implementation of its own.</summary>
    [Fact]
    public async Task AddingAProject_ReachesTheServer()
    {
        var (vm, _, _, client) = NewSubject();
        client.Rows = [Row(@"D:\Games\MyGame", "guid-1")];

        await vm.Projects.AddProjectAsync(@"D:\Games\MyGame");

        Assert.Equal([@"D:\Games\MyGame"], client.AddedPaths);
    }

    /// <summary>
    /// The plugin step installs into a project the caller NAMES. It used to install into "the one
    /// added a step ago", which could not answer the obvious question - if I have ten projects,
    /// which one is this? - so the step now lists them and each row carries its own guid.
    /// </summary>
    [Fact]
    public async Task InstallingThePlugin_TargetsTheProjectAskedFor_NotTheMostRecent()
    {
        var (vm, _, _, client) = NewSubject();
        client.Rows =
        [
            Row(@"D:\Games\First", "guid-first"),
            Row(@"D:\Games\Second", "guid-second"),
        ];
        await vm.Projects.RefreshAsync();

        await vm.Projects.InstallPluginAsync("guid-first");

        Assert.Equal(["guid-first"], client.InstalledInto);
    }

    /// <summary>Every known project is offered, so the step can list them all rather than guess.</summary>
    [Fact]
    public async Task ThePluginStepSeesEveryProject()
    {
        var (vm, _, _, client) = NewSubject();
        client.Rows =
        [
            Row(@"D:\Games\First", "guid-first"),
            Row(@"D:\Games\Second", "guid-second"),
        ];

        await vm.Projects.RefreshAsync();

        Assert.Equal(["guid-first", "guid-second"], vm.Projects.Projects.Select(p => p.ProductGuid));
    }

    /// <summary>
    /// A refused add shows the server's own refusal and adopts nothing. Deliberately a
    /// <see cref="ControlClientError.Server"/> failure: that is the only kind carrying text meant
    /// for a human, and <see cref="ProjectsViewModel"/> renders no message for any other - a first
    /// draft of this test threw a plain exception and asserted a message appeared, which is the
    /// product being right and the test being wrong.
    /// </summary>
    [Fact]
    public async Task ARefusedAdd_ReportsTheServersReasonAndAddsNothing()
    {
        var (vm, _, _, client) = NewSubject();
        client.OnAdd = _ => throw new ControlClientException(
            ControlClientError.Server, "'D:\\NotAProject' is not a Unity project.");

        await vm.Projects.AddProjectAsync(@"D:\NotAProject");

        Assert.Equal("'D:\\NotAProject' is not a Unity project.", vm.Projects.LastActionMessage);
        Assert.Empty(vm.Projects.Projects);
        Assert.Empty(client.InstalledInto);
    }

    /// <summary>
    /// Windows has FOUR steps. `Permissions` is macOS TCC folder access - Windows shows no such
    /// prompt, and walking a user through one that never fires would be a lie. The member still
    /// exists on the enum so this exclusion is explicit and testable rather than silently forgotten.
    /// </summary>
    [Fact]
    public void HasFourSteps_PermissionsIsNotOneOfThem()
    {
        var steps = OnboardingViewModel.AllSteps;

        Assert.Equal(4, steps.Length);
        Assert.DoesNotContain(OnboardingStep.Permissions, steps);
    }

    [Fact]
    public void StepsAreInAFixedOrder()
    {
        Assert.Equal(
            [OnboardingStep.Install, OnboardingStep.ClaudeCode, OnboardingStep.Projects, OnboardingStep.UnityPlugin],
            OnboardingViewModel.AllSteps);
    }

    /// <summary>
    /// THE COPY TRAP. The Mac's install step hardcodes "…five steps, and you can stop after the
    /// fourth with a fully working setup." That is authored copy, not API-served, and porting it
    /// verbatim would have the app state a number that is wrong on this platform.
    /// </summary>
    [Fact]
    public void CopyDoesNotClaimFiveSteps()
    {
        foreach (var step in OnboardingViewModel.AllSteps)
        {
            Assert.DoesNotContain("five steps", OnboardingViewModel.CopyFor(step), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>And no step may mention permissions either, for the same reason.</summary>
    [Fact]
    public void CopyDoesNotMentionAPermissionsStep()
    {
        foreach (var step in OnboardingViewModel.AllSteps)
        {
            Assert.DoesNotContain("permission", OnboardingViewModel.CopyFor(step), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EveryStepHasATitleAndCopy()
    {
        foreach (var step in OnboardingViewModel.AllSteps)
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Title()));
            Assert.False(string.IsNullOrWhiteSpace(OnboardingViewModel.CopyFor(step)));
        }
    }

    // ---- navigation ---------------------------------------------------------------------------

    [Fact]
    public void StartsOnTheFirstStep()
    {
        var (vm, _, _, _) = NewSubject();

        Assert.Equal(OnboardingStep.Install, vm.CurrentStep);
        Assert.False(vm.IsFinished);
    }

    [Fact]
    public void AdvanceWalksTheStepsInOrder()
    {
        var (vm, _, _, _) = NewSubject();

        vm.Advance();
        Assert.Equal(OnboardingStep.ClaudeCode, vm.CurrentStep);
        vm.Advance();
        Assert.Equal(OnboardingStep.Projects, vm.CurrentStep);
        vm.Advance();
        Assert.Equal(OnboardingStep.UnityPlugin, vm.CurrentStep);
    }

    /// <summary>
    /// The Unity plugin step is an UPGRADE, not a requirement: a user who stops after the third step
    /// already has a working Hades. So completion is never gated on anything that step does.
    /// </summary>
    [Fact]
    public void AdvancingPastTheLastStepCompletesOnboarding()
    {
        var (vm, _, store, _) = NewSubject();

        for (var i = 0; i < OnboardingViewModel.AllSteps.Length; i++) vm.Advance();

        Assert.True(vm.IsFinished);
        Assert.True(store.HasCompletedOnboarding);
    }

    [Fact]
    public void SkippingCompletesOnboardingToo()
    {
        var (vm, _, store, _) = NewSubject();

        vm.Skip();

        Assert.True(vm.IsFinished);
        Assert.True(store.HasCompletedOnboarding);
    }

    [Fact]
    public void AdvancingBeyondTheEndIsHarmless()
    {
        var (vm, _, _, _) = NewSubject();

        for (var i = 0; i < 20; i++) vm.Advance();

        Assert.True(vm.IsFinished);
        Assert.Equal(OnboardingStep.UnityPlugin, vm.CurrentStep);
    }

    // ---- the Claude Code check ----------------------------------------------------------------

    [Fact]
    public async Task VerifyingClaudeCode_ReportsTheToolCount()
    {
        var (vm, verifier, _, _) = NewSubject();
        verifier.Result = ClaudeCodeVerification.Reachable(23);

        await vm.VerifyClaudeCodeAsync();

        Assert.Equal(ClaudeCodeVerificationKind.Reachable, vm.ClaudeCodeVerification.Kind);
        Assert.Equal(23, vm.ClaudeCodeVerification.ToolCount);
    }

    [Fact]
    public async Task VerifyingClaudeCode_AFailureIsJustUnreachable()
    {
        var (vm, verifier, _, _) = NewSubject();
        verifier.Result = ClaudeCodeVerification.Unreachable(0);

        await vm.VerifyClaudeCodeAsync();

        Assert.Equal(ClaudeCodeVerificationKind.Unreachable, vm.ClaudeCodeVerification.Kind);
    }

    /// <summary>
    /// A reachable result proves the CORE is up and serving N tools. It does NOT prove Claude Code
    /// has connected - this check never inspects Claude Code's own state. The step's copy has to say
    /// so, or it claims something it did not verify.
    /// </summary>
    [Fact]
    public void TheClaudeCodeStepCopyDoesNotClaimClaudeCodeIsConnected()
    {
        var copy = OnboardingViewModel.CopyFor(OnboardingStep.ClaudeCode);

        Assert.Contains("does not", copy, StringComparison.OrdinalIgnoreCase);
    }
}
