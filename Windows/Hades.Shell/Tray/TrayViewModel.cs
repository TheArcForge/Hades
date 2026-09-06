using System.Net.Http;
using Hades.Control.Client;
using Hades.Control.Client.Dtos;
using Hades.Supervision;

namespace Hades.Shell.Tray;

/// <summary>The summary/release subset of the control API the tray needs. A seam, so the view model
/// can be tested without a running core - Swift's <c>ControlSummaryFetching</c> plays this role.</summary>
public interface ISummaryClient
{
    Task<SummaryResult> SummaryAsync();
    Task<ActionResult> ReleaseLeaseAsync(string leaseId);
}

/// <summary>
/// Owns exactly one piece of state - <see cref="Content"/> - and the polling that keeps it current.
/// The port of <c>Mac/HadesApp/Sources/HadesApp/MenuBarViewModel.swift</c>.
///
/// Holds nothing a view could turn into new display text: <see cref="Content"/> is produced entirely
/// by <see cref="MenuContent.Resolve"/>, which combines nothing. This type's only job is deciding
/// WHEN to resolve again, and WHAT to pass it - the supervisor's current state, and the most recent
/// successful /control/summary response, verbatim.
///
/// NOTE: no task in the plan assigns this wiring. Task 4 Step 6 connects the menu to the tray icon
/// and Task 5 assumes polling already exists ("fire once per lease acquisition, not repeatedly on
/// every poll"), but nothing in between ever hands the tray a supervisor or a summary. This fills
/// that gap.
/// </summary>
public sealed class TrayViewModel : IDisposable
{
    // One handler for every client this type ever builds. ControlClient's constructor REWRITES
    // BaseAddress and Authorization on the HttpClient it is handed, and HttpClient throws on both
    // once it has sent its first request - so a shared HttpClient breaks after the first poll, and
    // every real core restart yields a fresh port, so it would break in production rather than only
    // in tests. A fresh HttpClient per client avoids that; sharing the HANDLER keeps the connection
    // pool, so polling does not burn a socket per tick.
    static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 4,
    };

    readonly ICoreSupervisor _supervisor;
    readonly Func<ControlConnection?> _discover;
    readonly Func<ControlConnection, ISummaryClient> _makeClient;
    readonly TimeSpan _idleInterval;
    readonly TimeSpan _activeInterval;

    /// <summary>
    /// The most recent successful /control/summary response. Cleared the instant the supervisor is
    /// observed NOT running, so a later return to Running never shows a summary left over from a
    /// core that has since died - <see cref="MenuContent.Resolve"/>'s own documented precondition.
    /// </summary>
    SummaryResult? _lastSummary;

    CancellationTokenSource? _poll;
    readonly SemaphoreSlim _tickGate = new(1, 1);
    volatile bool _menuOpen;

    public MenuContent Content { get; private set; } = MenuContent.NotRunning;

    /// <summary>Fires with the value <see cref="Content"/> was just set to.</summary>
    public event EventHandler<MenuContent>? ContentChanged;

    public TrayViewModel(
        ICoreSupervisor supervisor,
        Func<ControlConnection?> discover,
        Func<ControlConnection, ISummaryClient>? makeClient = null,
        TimeSpan? idleInterval = null,
        TimeSpan? activeInterval = null)
    {
        _supervisor = supervisor;
        _discover = discover;
        _makeClient = makeClient ?? DefaultClient;
        _idleInterval = idleInterval ?? TimeSpan.FromSeconds(5);
        _activeInterval = activeInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>One immediate tick, so the tray reflects reality as soon as the supervisor has
    /// started rather than sitting on the not-running icon until the first poll comes round.</summary>
    public Task BootstrapAsync(CancellationToken cancellationToken = default) => TickAsync(cancellationToken);

    /// <summary>
    /// Starts the poll loop. Idempotent.
    ///
    /// DIVERGENCE FROM THE MAC, deliberately. `MenuBarViewModel` polls ONLY while the dropdown is
    /// open, on the grounds that "a background app has no business polling continuously" - and it
    /// can afford that, because its status item is a fixed "H" that never varies by state
    /// (StatusIcon.swift says so explicitly). This tray is NOT fixed: Task 3 gives it seven
    /// state-dependent icons, so an icon that only refreshes while the menu is open would be wrong
    /// for as long as the menu is shut, which is nearly always. The compromise is two cadences - a
    /// slow one to keep the icon honest, and the Mac's 1Hz while the menu is actually open.
    /// </summary>
    public void StartPolling()
    {
        if (_poll is not null) return;

        _poll = new CancellationTokenSource();
        var token = _poll.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await TickAsync(token).ConfigureAwait(false);

                try
                {
                    await Task.Delay(_menuOpen ? _activeInterval : _idleInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, token);
    }

    public void StopPolling()
    {
        _poll?.Cancel();
        _poll?.Dispose();
        _poll = null;
    }

    /// <summary>
    /// The menu is open or closed. Open means poll at the faster cadence AND tick immediately, so
    /// what the user is looking at is current rather than up to one idle interval stale.
    /// </summary>
    public void MenuOpened()
    {
        _menuOpen = true;
        _ = TickAsync(CancellationToken.None);
    }

    public void MenuClosed() => _menuOpen = false;

    /// <summary>
    /// POST /control/leases/{id}/release. Idempotent and safe to call late - the TTL may already
    /// have fired - and a <c>success: false</c> result is not a client-side error either: the
    /// server's own message already names what happened. The result, success or thrown
    /// ControlClientError alike, is discarded, and the UI is brought current with one immediate
    /// tick rather than waiting for the next scheduled poll.
    /// </summary>
    public async Task ReleaseAsync(string leaseId, CancellationToken cancellationToken = default)
    {
        if (_discover() is { } connection)
        {
            try
            {
                await _makeClient(connection).ReleaseLeaseAsync(leaseId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never surfaced. See this method's own doc comment.
            }
        }

        await TickAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-validates the supervisor, then resolves <see cref="Content"/> from its state plus the most
    /// recent summary.
    ///
    /// This is the ENTIRE stale-token recovery mechanism: <see cref="_discover"/> is called fresh
    /// every tick and never cached, so a token that went stale because the core restarted needs no
    /// special-case code - the next tick re-reads the by-then-rewritten discovery file on its own.
    /// Every fetch failure is treated identically: swallowed, never surfaced, leaving
    /// <see cref="_lastSummary"/> exactly as it was until either a later tick succeeds or the
    /// supervisor itself moves off Running.
    /// </summary>
    async Task TickAsync(CancellationToken cancellationToken)
    {
        // Ticks can overlap - the loop, MenuOpened, and ReleaseAsync all call this - and two
        // interleaved ticks could publish states out of order, flickering the icon between them.
        if (!await _tickGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;

        try
        {
            await _supervisor.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var state = _supervisor.State;

            if (state.Kind != SupervisorStateKind.Running)
            {
                _lastSummary = null;
                Publish(MenuContent.Resolve(state, null));
                return;
            }

            if (_discover() is { } connection)
            {
                try
                {
                    _lastSummary = await _makeClient(connection).SummaryAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Self-heals next tick - see this method's own doc comment. Nothing to do here.
                }
            }
            // No else: a momentarily-unreadable discovery file, while the supervisor still reports
            // Running, keeps whatever _lastSummary already holds rather than clearing it. One
            // unlucky file read should not flash the tray back to "not running".

            Publish(MenuContent.Resolve(state, _lastSummary));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            _tickGate.Release();
        }
    }

    void Publish(MenuContent content)
    {
        Content = content;
        ContentChanged?.Invoke(this, content);
    }

    static ISummaryClient DefaultClient(ControlConnection connection) =>
        new ControlClientAdapter(new ControlClient(connection, new HttpClient(SharedHandler, disposeHandler: false)));

    /// <summary>Adapts the concrete <see cref="ControlClient"/> onto <see cref="ISummaryClient"/>.
    /// Exists only because the client is a sealed class; it adds nothing.</summary>
    sealed class ControlClientAdapter(ControlClient inner) : ISummaryClient
    {
        public Task<SummaryResult> SummaryAsync() => inner.SummaryAsync();
        public Task<ActionResult> ReleaseLeaseAsync(string leaseId) => inner.ReleaseLeaseAsync(leaseId);
    }

    public void Dispose()
    {
        StopPolling();
        _tickGate.Dispose();
    }
}
