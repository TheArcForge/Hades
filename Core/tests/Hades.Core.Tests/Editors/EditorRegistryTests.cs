using Hades.Contract.Wire;
using Hades.Core.Editors;

namespace Hades.Core.Tests.Editors;

/// <summary>
/// EditorRegistry in isolation, with lightweight in-memory-stream sessions standing in for real
/// connections - registration/deregistration identity semantics do not need a real socket. See
/// EditorListenerTests for the same policy proven over real connections end to end.
/// </summary>
public class EditorRegistryTests
{
    static Hello MakeHello(string projectGuid, long processId = 1) => new()
    {
        ProjectGuid = projectGuid,
        ProjectPath = $"/tmp/{projectGuid}",
        UnityVersion = "6000.3.2f1",
        PluginVersion = "0.1.0",
        ProcessId = processId,
    };

    /// <summary>A session that is never Start()-ed and never touches the network - fine here,
    /// since these tests only exercise identity (same-instance-or-not), never message flow.</summary>
    static EditorSession MakeSession(string projectGuid, long processId = 1) =>
        new(new MemoryStream(), MakeHello(projectGuid, processId));

    [Fact]
    public void Get_OnAnEmptyRegistry_ReturnsNull()
    {
        var registry = new EditorRegistry();

        Assert.Null(registry.Get("aaaabbbbccccddddeeeeffff00001111"));
    }

    [Fact]
    public void Register_MakesTheEditorFindableByProjectGuid()
    {
        var registry = new EditorRegistry();
        var session = MakeSession("aaaabbbbccccddddeeeeffff00001111", processId: 777);

        registry.Register(new AttachedEditor { Hello = session.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = session });

        var found = registry.Get("aaaabbbbccccddddeeeeffff00001111");
        Assert.NotNull(found);
        Assert.Equal(777, found!.Hello.ProcessId);
        Assert.Equal("6000.3.2f1", found.Hello.UnityVersion);
        Assert.Equal("/tmp/aaaabbbbccccddddeeeeffff00001111", found.Hello.ProjectPath);
    }

    [Fact]
    public void Deregister_RemovesIt()
    {
        var registry = new EditorRegistry();
        var session = MakeSession("aaaabbbbccccddddeeeeffff00001111");
        registry.Register(new AttachedEditor { Hello = session.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = session });

        var removed = registry.Deregister("aaaabbbbccccddddeeeeffff00001111", session);

        Assert.True(removed); // this session WAS the current registration - reported as such
        Assert.Null(registry.Get("aaaabbbbccccddddeeeeffff00001111"));
    }

    [Fact]
    public void TwoEditorsSameProject_NewestRegistrationWins()
    {
        var registry = new EditorRegistry();
        const string guid = "aaaabbbbccccddddeeeeffff00001111";
        var older = MakeSession(guid, processId: 1);
        var newer = MakeSession(guid, processId: 2);

        registry.Register(new AttachedEditor { Hello = older.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = older });
        registry.Register(new AttachedEditor { Hello = newer.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = newer });

        var found = registry.Get(guid);
        Assert.NotNull(found);
        Assert.Equal(2, found!.Hello.ProcessId);
        Assert.Single(registry.All()); // never two entries for one project
    }

    [Fact]
    public void TheSupersededSessionsOwnDeregisterCall_DoesNotEvictTheNewerRegistration()
    {
        // Exactly the race the plan calls out: a user reopens Unity while the old connection is
        // still dying. The new hello registers first (newest wins); when the OLD connection's
        // socket finally notices it's dead and calls Deregister, that must be a no-op.
        var registry = new EditorRegistry();
        const string guid = "aaaabbbbccccddddeeeeffff00001111";
        var older = MakeSession(guid, processId: 1);
        var newer = MakeSession(guid, processId: 2);

        registry.Register(new AttachedEditor { Hello = older.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = older });
        registry.Register(new AttachedEditor { Hello = newer.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = newer });

        var removed = registry.Deregister(guid, older); // the old, superseded session's belated cleanup

        Assert.False(removed); // a no-op, reported as such - see Deregister's own doc comment
        var found = registry.Get(guid);
        Assert.NotNull(found);
        Assert.Equal(2, found!.Hello.ProcessId); // still the newer one, not evicted and not null
    }

    [Fact]
    public void DifferentProjects_BothStayRegistered()
    {
        var registry = new EditorRegistry();
        var a = MakeSession("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var b = MakeSession("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        registry.Register(new AttachedEditor { Hello = a.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = a });
        registry.Register(new AttachedEditor { Hello = b.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = b });

        Assert.Equal(2, registry.All().Count);
    }

    [Fact]
    public void Register_RejectsAnEditorWithNoProjectGuid()
    {
        var registry = new EditorRegistry();
        var badHello = new Hello { ProjectGuid = "", ProjectPath = "/tmp/x", UnityVersion = "1", PluginVersion = "1", ProcessId = 1 };
        var session = new EditorSession(new MemoryStream(), badHello);

        Assert.Throws<ArgumentException>(() =>
            registry.Register(new AttachedEditor { Hello = badHello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = session }));
    }

    [Fact]
    public async Task ConcurrentRegisterDeregisterQuery_NeverThrowsAndEndsEmpty()
    {
        // "The registry is safe to query while connections come and go" - many threads
        // register/deregister DIFFERENT projects' editors and query the whole table
        // simultaneously. Not just sequential: everything runs concurrently via Task.WhenAll.
        var registry = new EditorRegistry();
        const int projectCount = 24;
        const int cyclesPerProject = 50;

        var guids = Enumerable.Range(0, projectCount)
            .Select(i => i.ToString("x32"))
            .ToList();

        var readerCts = new CancellationTokenSource();
        var readerTask = Task.Run(async () =>
        {
            // Concurrent readers must never throw or see a torn/corrupt collection, for as long
            // as writers are still active.
            while (!readerCts.IsCancellationRequested)
            {
                _ = registry.All().Count;
                foreach (var guid in guids) registry.Get(guid);
                await Task.Yield();
            }
        });

        var writers = guids.Select(guid => Task.Run(() =>
        {
            for (var cycle = 0; cycle < cyclesPerProject; cycle++)
            {
                var session = MakeSession(guid, cycle);
                registry.Register(new AttachedEditor { Hello = session.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = session });
                registry.Deregister(guid, session);
            }
        }));

        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(30));
        readerCts.Cancel();
        await readerTask.WaitAsync(TimeSpan.FromSeconds(30));

        // Every writer's last act for its project was a matching Deregister, so nothing should
        // remain - proves no lost deregistration and no duplicate/corrupted entries survived
        // the concurrent hammering.
        Assert.Empty(registry.All());
    }

    [Fact]
    public async Task ConcurrentRegistrationsForTheSameProject_LeaveExactlyOneEntryNeverZeroNeverTwo()
    {
        // The specific race from the plan, but hammered concurrently rather than in two neat
        // sequential steps: many "reopen Unity" registrations for the SAME project GUID racing
        // each other, none of them ever deregistering. However the race resolves, the registry
        // must end with exactly one entry - never duplicated, never lost.
        var registry = new EditorRegistry();
        const string guid = "aaaabbbbccccddddeeeeffff00001111";
        const int concurrency = 32;
        var barrier = new Barrier(concurrency);
        var sessions = Enumerable.Range(0, concurrency).Select(i => MakeSession(guid, i)).ToList();

        var threads = sessions.Select(session => new Thread(() =>
        {
            barrier.SignalAndWait();
            registry.Register(new AttachedEditor { Hello = session.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = session });
        })).ToList();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        var all = registry.All();
        Assert.Single(all);
        Assert.Contains(all[0].Session, sessions); // the winner was genuinely one of the contenders
    }
}
