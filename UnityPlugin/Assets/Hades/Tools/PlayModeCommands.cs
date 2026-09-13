// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Globalization;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;

namespace Hades.Tools
{
    /// <summary>
    /// DESIGN-01 (docs/design/DESIGN-01-play-mode-control.md), Lane B of the roadmap's Phase 1.
    /// One file, carrying four things - matching ProjectCommands.cs's own "carries several
    /// classes because they are one concern" convention (see that file's own class doc comment):
    ///
    /// <see cref="PlayModeCommands"/>  - the five wire handlers (play.enter/exit/set_paused/step/status)
    /// <see cref="PlayModeSession"/>   - the SessionState-backed record that survives the enter reload
    /// <see cref="PlayModeWatchdog"/>  - the EditorApplication.update hook: deadline + per-tick reconciliation
    /// <see cref="EditorPlayModeApi"/> - the seam over EditorApplication.EnterPlaymode/ExitPlaymode/etc
    ///
    /// <para><b>Why per-tick reconciliation, not boot-only (DESIGN-01 §9.2).</b> Exiting Play Mode does
    /// NOT trigger a domain reload - entering does (VERIFIED by probe, DESIGN-01 §14 item 1). A design
    /// that only reconciled from a post-reload boot would never notice an exit (or a deadline passing
    /// with the enter never landing), because there is no reload to run that reconciliation at all.
    /// <see cref="PlayModeWatchdog.Tick"/> therefore runs the SAME reconciliation table on every
    /// <c>EditorApplication.update</c>, not just once from <see cref="PlayModeWatchdog.Install"/> -
    /// boot is simply the first tick.</para>
    ///
    /// <para><b>The mutation guard this file feeds (CommandTable.AllowedWhilePlaying).</b> The
    /// generating rule (DESIGN-01 §6.2): the guard refuses anything that ACQUIRES or MUTATES; it
    /// permits anything that RELEASES, READS, or RECOVERS. That rule - not "what happens to a
    /// mutation after Play Mode exits" - is what CommandTable's allow-list encodes.</para>
    /// </summary>
    public static class PlayModeCommands
    {
        // ---------------------------------------------------------------- constants

        /// <summary>DESIGN-01 §9.3: a human-scale observation window - deliberately not
        /// ReloadGate.DefaultTtl (30s, sized for a script-editing round trip) nor the regression
        /// recording TTL (10min).</summary>
        public const double DefaultTtlSeconds = 120;
        public const double MinTtlSeconds = 10;
        public const double MaxTtlSeconds = 600;

        // ---------------------------------------------------------------- play.enter

        internal static JsonValue Enter(ReloadGate gate, JsonValue @params) =>
            LeaseScope.Run(gate, "play.enter", () => DoEnter(@params));

        /// <summary>Lease-free core, run INSIDE LeaseScope.Run's bounded acquire/release (DESIGN-08
        /// §6's recommendation over DESIGN-01's original hand-rolled gate check: "the reason to take
        /// one anyway is not protection - it is that acquiring is how you find out someone else holds
        /// it"). This handler's own work (stamp SessionState, schedule a delayCall) carries no reload
        /// risk of its own - the lease exists only to detect and refuse a conflicting holder, with the
        /// exact wording <see cref="LeaseScope.Run"/> already produces
        /// (naming 'script_editing_session', a live tool - DESIGN-01 §5.1).</summary>
        internal static JsonValue DoEnter(JsonValue @params)
        {
            CommandTable.RejectUnknownParams(@params, "ttlSeconds");
            var requestedTtl = JsonParams.OptionalDouble(@params, "ttlSeconds", DefaultTtlSeconds);
            var ttlSeconds = Clamp(requestedTtl, MinTtlSeconds, MaxTtlSeconds);

            if (PlayModeSession.HasSession)
            {
                // Idempotent: a Hades session already exists (requested/playing/exitRequested) -
                // renew using its OWN original ttl (never the newly requested one - mirrors
                // ReloadGate.Acquire's "re-acquiring your own lease is a renewal ... ttl is ignored
                // on a renewal, since a lease's duration is fixed at creation") and hand back its
                // current record rather than starting a second, redundant transition.
                PlayModeSession.RenewFromNow(EditorPlayModeApi.PlayClock);
                return BuildEnterResult(PlayModeSession.SessionId, CurrentState());
            }

            if (EditorPlayModeApi.IsPlaying())
            {
                // DESIGN-01 principle 3: "Never adopt what Hades did not start." Refusing costs
                // nothing here - the agent is already free to call action:'status' to observe it, or
                // action:'exit' to stop it (an explicit exit is honoured even for a user-owned
                // session - see DESIGN-01 §9.1's "Playing, user-owned | play.exit" row).
                throw new InvalidOperationException(
                    "play_mode action 'enter' cannot start a Hades-owned play session: this Unity "
                    + "Editor is already in Play Mode, started by the user (not Hades) pressing Play. "
                    + "Hades never adopts a play session it did not start. Call play_mode with action "
                    + "'status' to observe it, or action 'exit' to stop it.");
            }

            var sessionId = Guid.NewGuid().ToString("N");
            var now = EditorPlayModeApi.PlayClock();

            // Stamped BEFORE the transition is even scheduled - this ordering is the entire reason
            // the design is safe (DESIGN-01 §7.2 step 3 and its own closing note): if the response
            // itself is lost to the reload that follows, the agent's very next 'status' call still
            // finds a truthful record.
            PlayModeSession.Stamp(sessionId, PlayModeSession.PhaseRequested, now, ttlSeconds);

            // Scheduled rather than called inline, so the pump tick that produced THIS response
            // finishes and the response is written before the reload begins (DESIGN-01 §7.2 step 4).
            EditorPlayModeApi.ScheduleAfterThisTick(() => EditorPlayModeApi.EnterPlayMode());

            return BuildEnterResult(sessionId, "entering");
        }

        static JsonValue BuildEnterResult(string sessionId, string state)
        {
            var deadline = PlayModeSession.DeadlineUtc;
            return JsonValue.NewObject()
                .SetProperty("sessionId", JsonValue.String(sessionId))
                .SetProperty("state", JsonValue.String(state))
                .SetProperty("expiresAtUtcMs", deadline.HasValue ? JsonValue.Integer(ToUnixTimeMs(deadline.Value)) : JsonValue.Null);
        }

        // ---------------------------------------------------------------- play.exit

        /// <summary>DESIGN-01 §5.1 (revised, third draft): deliberately UNCONDITIONAL. No lease
        /// check, no advisory note - exit triggers no reload at all (VERIFIED by probe), so there is
        /// no LockReloadAssemblies interaction to be cautious about, and refusing here would convert
        /// a precondition failure into a trap (the only way OUT of a state the agent is currently
        /// in). An earlier draft's "proceed with a deferral note" language was itself describing a
        /// mechanism that does not exist and has been deliberately dropped, not merely reworded.</summary>
        internal static JsonValue Exit(ReloadGate gate, JsonValue @params)
        {
            CommandTable.RejectUnknownParams(@params);
            RenewIfSessionExists();

            var wasPlaying = EditorPlayModeApi.IsPlaying();
            var sessionId = PlayModeSession.HasSession ? PlayModeSession.SessionId : null;

            if (!wasPlaying)
            {
                // Idempotent no-op - same "closing something never opened is safe" contract as
                // EndScriptEditing/ReloadGate.Release (DESIGN-01 §9.1's "EditMode | play.exit" row).
                // A leftover phase=requested record (the enter never actually landed) is left alone
                // here - PlayModeWatchdog's own deadline rule is what clears that, not this handler.
                return JsonValue.NewObject()
                    .SetProperty("state", JsonValue.String("editMode"))
                    .SetProperty("wasPlaying", JsonValue.Bool(false))
                    .SetProperty("sessionId", sessionId != null ? JsonValue.String(sessionId) : JsonValue.Null);
            }

            if (PlayModeSession.HasSession) PlayModeSession.SetPhase(PlayModeSession.PhaseExitRequested);
            EditorPlayModeApi.ScheduleAfterThisTick(() => EditorPlayModeApi.ExitPlayMode());

            return JsonValue.NewObject()
                .SetProperty("state", JsonValue.String("exiting"))
                .SetProperty("wasPlaying", JsonValue.Bool(true))
                .SetProperty("sessionId", sessionId != null ? JsonValue.String(sessionId) : JsonValue.Null);
        }

        // ---------------------------------------------------------------- play.set_paused

        internal static JsonValue SetPaused(ReloadGate gate, JsonValue @params)
        {
            CommandTable.RejectUnknownParams(@params, "paused");
            var paused = RequireBool(@params, "paused", "play.set_paused");
            RenewIfSessionExists();

            EditorPlayModeApi.SetPaused(paused);

            return WithSessionFields(JsonValue.NewObject()
                .SetProperty("state", JsonValue.String(CurrentState()))
                .SetProperty("paused", JsonValue.Bool(EditorPlayModeApi.IsPaused())));
        }

        // ---------------------------------------------------------------- play.step

        /// <summary>Advances exactly ONE frame and takes NO 'frames' parameter - DESIGN-01 §12's
        /// exclusion. EditorApplication.Step() advances one frame and there is no N-frame API; N
        /// synchronous calls inside one handler would not actually let N frames elapse (that needs
        /// returning to Unity's own loop between each), so a 'frames' count is not implementable as a
        /// single-tick command. Do not re-add the parameter without building a watchdog-driven
        /// per-tick counter (a real, separate feature) - <see cref="CommandTable.RejectUnknownParams"/>
        /// is what makes reintroducing it accidentally impossible.</summary>
        internal static JsonValue Step(ReloadGate gate, JsonValue @params)
        {
            CommandTable.RejectUnknownParams(@params);
            RenewIfSessionExists();

            EditorPlayModeApi.StepFrame();

            return WithSessionFields(JsonValue.NewObject()
                .SetProperty("state", JsonValue.String(CurrentState()))
                .SetProperty("frame", JsonValue.Integer(UnityEngine.Time.frameCount)));
        }

        /// <summary>Appends 'sessionId'/'ownedByHades' to a single-tick action's result - not part of
        /// DESIGN-01 §5's minimum wire shape for 'play.set_paused'/'play.step' (which lists only
        /// {state, paused}/{state, frame}), but a cheap, harmless addition: PlayModeSession already
        /// knows both, and it keeps every action's result consistently answerable on the core side
        /// (PlayModeTool.cs), rather than only 'status'/'enter'/'exit' carrying session identity.</summary>
        static JsonValue WithSessionFields(JsonValue result)
        {
            var hasSession = PlayModeSession.HasSession;
            return result
                .SetProperty("sessionId", hasSession ? JsonValue.String(PlayModeSession.SessionId) : JsonValue.Null)
                .SetProperty("ownedByHades", JsonValue.Bool(hasSession));
        }

        // ---------------------------------------------------------------- play.status

        /// <summary>Class 4 (live-state read - no lease): answers entirely from SessionState plus
        /// <see cref="EditorPlayModeApi.IsPlaying"/>, exactly like every other class-4 handler in this
        /// plugin (ProjectCommands.GetConsoleLog/GetTestResults, InspectorCommands.InspectGameObject) -
        /// there is nothing here a domain reload could interrupt mid-flight.</summary>
        internal static JsonValue Status(ReloadGate gate, JsonValue @params)
        {
            CommandTable.RejectUnknownParams(@params);
            RenewIfSessionExists();

            var hasSession = PlayModeSession.HasSession;
            var isPlaying = EditorPlayModeApi.IsPlaying();
            var deadline = PlayModeSession.DeadlineUtc;
            var startedAt = PlayModeSession.StartedAtUtc;

            var result = JsonValue.NewObject()
                .SetProperty("state", JsonValue.String(CurrentState()))
                .SetProperty("sessionId", hasSession ? JsonValue.String(PlayModeSession.SessionId) : JsonValue.Null)
                .SetProperty("ownedByHades", JsonValue.Bool(hasSession))
                .SetProperty("expiresAtUtcMs", hasSession && deadline.HasValue ? JsonValue.Integer(ToUnixTimeMs(deadline.Value)) : JsonValue.Null)
                .SetProperty("frame", isPlaying ? JsonValue.Integer(UnityEngine.Time.frameCount) : JsonValue.Null);

            result.SetProperty("playingForSeconds",
                hasSession && isPlaying && startedAt.HasValue
                    ? JsonValue.Float((EditorPlayModeApi.PlayClock() - startedAt.Value).TotalSeconds)
                    : JsonValue.Null);

            return result;
        }

        // ---------------------------------------------------------------- shared helpers

        /// <summary>"Activity renews, intent does not" (ReloadLease's own rule, DESIGN-01 §9.3): every
        /// play.* call renews a currently-held Hades session's deadline from now, using its OWN
        /// original ttl. A no-op when no Hades session exists (nothing to renew) - in particular,
        /// never adopts or extends a user-owned session.</summary>
        static void RenewIfSessionExists()
        {
            if (PlayModeSession.HasSession) PlayModeSession.RenewFromNow(EditorPlayModeApi.PlayClock);
        }

        /// <summary>The 'state' vocabulary (DESIGN-01 §4.1): editMode | entering | playing | paused |
        /// exiting. ('detached' is a core-side, not-attached/busy concept - see PlayModeTool.cs on the
        /// Hades.Server side - and never appears here.)
        ///
        /// 'paused' is a property of the EDITOR (EditorApplication.isPaused), not of ownership - it
        /// must be checked regardless of whether PlayModeSession.HasSession is true, so a 'pause'
        /// call against a USER-OWNED session (allow-listed and honoured exactly like everything else
        /// - DESIGN-01 never distinguishes ownership for pause/resume/step) is still reported
        /// correctly rather than collapsing to a bare 'playing'.</summary>
        static string CurrentState()
        {
            if (PlayModeSession.HasSession)
            {
                var phase = PlayModeSession.Phase;
                if (phase == PlayModeSession.PhaseRequested) return "entering";
                if (phase == PlayModeSession.PhaseExitRequested) return "exiting";
                // PhasePlaying:
                if (!EditorPlayModeApi.IsPlaying()) return "exiting"; // watchdog has not reconciled this tick yet
            }
            else if (!EditorPlayModeApi.IsPlaying())
            {
                return "editMode";
            }

            // Reached only when IsPlaying() is already known true (either branch above fell
            // through) - 'paused' is checked here, unconditionally on ownership.
            return EditorPlayModeApi.IsPaused() ? "paused" : "playing";
        }

        static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;

        static bool RequireBool(JsonValue @params, string key, string context)
        {
            if (@params != null && @params.Kind == JsonValueKind.Object
                && @params.TryGetProperty(key, out var value) && value != null && value.Kind == JsonValueKind.Boolean)
            {
                return value.AsBoolean();
            }

            throw new ArgumentException("'" + context + "' requires a boolean '" + key + "' parameter.");
        }

        /// <summary>Small, separate copy rather than widening CommandTable's private helper's
        /// visibility - same "this file's own change surface stays limited to itself" reasoning
        /// ProjectCommands.ToUnixTimeMs already states for its own identical copy.</summary>
        static long ToUnixTimeMs(DateTime utc) =>
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        // ---------------------------------------------------------------- CommandTable.AllowedWhilePlaying messages

        /// <summary>DESIGN-08 §7.3: stated as an explicit refuse (present in the guard's dictionary,
        /// not merely absent from it) so the play-mode x test-run interaction is a decision, not an
        /// oversight. Names only 'play_mode' (a live tool) - see LiveMcpToolNamesTests /
        /// ToolWireMethodsTests for the pin.</summary>
        // NOTE: deliberately does not spell out the wire method name ("project.run_tests") or the
        // MCP tool name ("project_run_tests") in this text - either would introduce an
        // underscore-joined, tool-name-SHAPED token (e.g. "run_tests") that
        // LiveMcpToolNames.AssertMessageNamesOnlyLiveTools' scan would flag as looking like an MCP
        // tool name that is not one. Referring to the action in prose instead sidesteps that
        // entirely; only 'play_mode' (a live tool) appears as a tool-shaped token here.
        public const string RunTestsRefusedWhilePlayingMessage =
            "Hades refuses starting a new Unity Test Runner run while this Editor is in Play Mode - "
            + "an EditMode run would itself trigger a domain reload that ends this play session, and "
            + "a PlayMode run cannot start inside an already-running Play Mode. Call play_mode with "
            + "action 'exit' first, then retry.";

        /// <summary>DESIGN-08 §7.1/§7.3, for the not-yet-built 'scene.bake' action 'start' -
        /// declarative until DESIGN-06 ships (CommandTable.Dispatch never reaches this entry without a
        /// registered handler - see CommandTable's own AllowedWhilePlaying doc comment).</summary>
        public const string BakeStartRefusedWhilePlayingMessage =
            "'scene.bake' (action 'start') begins a lighting/occlusion/navmesh bake, and this Unity "
            + "Editor is in Play Mode. Entering Play Mode already cancels any bake in progress, and "
            + "Hades refuses starting a new one while playing. Call play_mode with action 'exit' "
            + "first, then retry.";

        /// <summary>DESIGN-08 §7.1/§7.3, for the not-yet-built 'project.build_start' - declarative
        /// until DESIGN-03 ships (see BakeStartRefusedWhilePlayingMessage's own doc comment).</summary>
        // See RunTestsRefusedWhilePlayingMessage's own note - deliberately no "build_start"-shaped
        // token here either.
        public const string BuildStartRefusedWhilePlayingMessage =
            "Hades refuses starting a Player build while this Unity Editor is in Play Mode. Call "
            + "play_mode with action 'exit' first, then retry.";
    }

    /// <summary>
    /// SessionState-backed record of a Hades-owned play session - the single source of truth
    /// DESIGN-01 §7.1 requires: it (and only it) survives the domain reload 'enter' causes, is
    /// cleared on Editor restart, and every writer here is a command handler or
    /// <see cref="PlayModeWatchdog"/>, both always on the main thread - the same guarantee
    /// ReloadGate's own SessionState calls rely on.
    ///
    /// Presence of a session id IS 'ownedByHades' (DESIGN-01 §7.1's "(implicit)" row) - there is no
    /// separate boolean to fall out of sync with it.
    ///
    /// Beyond the three keys DESIGN-01 §7.1 names explicitly (SessionId/Phase/DeadlineTicks), this
    /// also persists the session's own original ttl (needed so <see cref="RenewFromNow"/> can push
    /// the deadline out by the SAME amount every time, matching ReloadLease's "a lease's duration is
    /// fixed at creation" rule) and the moment play actually started (needed for the 'playingForSeconds'
    /// result field, DESIGN-01 §8.3) - both cheap, both covered by the identical "SessionState survives
    /// both play-mode transitions" probe result DESIGN-01 §7.1 already cites for the other three.
    /// </summary>
    public static class PlayModeSession
    {
        public const string PhaseRequested = "requested";
        public const string PhasePlaying = "playing";
        public const string PhaseExitRequested = "exitRequested";

        const string SessionIdKey = "Hades.PlayMode.SessionId";
        const string PhaseKey = "Hades.PlayMode.Phase";
        const string DeadlineTicksKey = "Hades.PlayMode.DeadlineTicks";
        const string TtlSecondsKey = "Hades.PlayMode.TtlSeconds";
        const string StartedAtTicksKey = "Hades.PlayMode.StartedAtUtcTicks";

        /// <summary>Mirrors whether <see cref="SessionIdKey"/> is set, so <see cref="HasSession"/> -
        /// which <see cref="PlayModeWatchdog.Tick"/> evaluates on EVERY EditorApplication.update tick,
        /// forever, in every Editor - does not cross into native and allocate a string per tick just
        /// to answer "no" in the overwhelmingly common case where no play session has ever existed.
        ///
        /// <para>Null means "not yet known in this AppDomain", which is exactly the state a domain
        /// reload leaves it in: the reload wipes this static while SessionState itself survives, so
        /// the next read re-derives it from the authoritative store once and caches again. That is
        /// why this needs no explicit boot hook - laziness IS the reload handling.</para>
        ///
        /// <para><b>Correct only while <see cref="Stamp"/> and <see cref="Clear"/> remain the only
        /// writers of <see cref="SessionIdKey"/>.</b> Both maintain this field. A third writer that
        /// forgot to would leave the mirror stale, and the staleness would be invisible - so if one
        /// is ever added, it must set this too, or this optimisation must come out.</para></summary>
        static bool? _hasSession;

        public static bool HasSession
        {
            get
            {
                if (_hasSession == null) _hasSession = !string.IsNullOrEmpty(SessionState.GetString(SessionIdKey, ""));
                return _hasSession.Value;
            }
        }

        public static string SessionId => SessionState.GetString(SessionIdKey, null);

        public static string Phase => SessionState.GetString(PhaseKey, null);

        public static DateTime? DeadlineUtc => ReadTicks(DeadlineTicksKey);

        public static DateTime? StartedAtUtc => ReadTicks(StartedAtTicksKey);

        static double TtlSeconds
        {
            get
            {
                var raw = SessionState.GetString(TtlSecondsKey, "");
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : PlayModeCommands.DefaultTtlSeconds;
            }
        }

        static DateTime? ReadTicks(string key)
        {
            var raw = SessionState.GetString(key, "");
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                ? new DateTime(ticks, DateTimeKind.Utc)
                : (DateTime?)null;
        }

        /// <summary>Stamps a brand-new Hades-owned session. Callers MUST call this before scheduling
        /// the transition that follows it (DESIGN-01 §7.2 step 3) - that ordering, not this method
        /// itself, is what makes the design safe against a lost response.</summary>
        public static void Stamp(string sessionId, string phase, DateTime nowUtc, double ttlSeconds)
        {
            SessionState.SetString(SessionIdKey, sessionId);
            _hasSession = true; // keep the mirror truthful - see its own doc comment
            SessionState.SetString(PhaseKey, phase);
            SessionState.SetString(TtlSecondsKey, ttlSeconds.ToString(CultureInfo.InvariantCulture));
            SessionState.SetString(DeadlineTicksKey, (nowUtc + TimeSpan.FromSeconds(ttlSeconds)).Ticks.ToString(CultureInfo.InvariantCulture));
            SessionState.EraseString(StartedAtTicksKey);
        }

        /// <summary>No-op when no session exists - callers (PlayModeWatchdog included) never need to
        /// guard this themselves.</summary>
        public static void SetPhase(string phase)
        {
            if (!HasSession) return;
            SessionState.SetString(PhaseKey, phase);
        }

        /// <summary>The 'requested' -&gt; 'playing' transition PlayModeWatchdog performs once it
        /// observes IsPlaying() true - also stamps <see cref="StartedAtUtc"/>, the moment
        /// 'playingForSeconds' is measured from.</summary>
        public static void MarkPlaying(DateTime nowUtc)
        {
            SetPhase(PhasePlaying);
            SessionState.SetString(StartedAtTicksKey, nowUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>"Activity renews, intent does not": pushes the deadline out by this session's OWN
        /// original ttl, from <paramref name="clock"/>() now. A no-op when no session exists.</summary>
        public static void RenewFromNow(Func<DateTime> clock)
        {
            if (!HasSession) return;
            var now = clock();
            SessionState.SetString(DeadlineTicksKey, (now + TimeSpan.FromSeconds(TtlSeconds)).Ticks.ToString(CultureInfo.InvariantCulture));
        }

        public static bool DeadlinePassed(Func<DateTime> clock)
        {
            var deadline = DeadlineUtc;
            return deadline.HasValue && clock() >= deadline.Value;
        }

        /// <summary>Erases every key this session touches. PlayModeWatchdog calls this the instant it
        /// observes the session has ended, by whatever means (agent exit, user Stop, a recompile, or a
        /// deadline passing before the enter ever actually landed).</summary>
        public static void Clear()
        {
            SessionState.EraseString(SessionIdKey);
            _hasSession = false; // keep the mirror truthful - see its own doc comment
            SessionState.EraseString(PhaseKey);
            SessionState.EraseString(DeadlineTicksKey);
            SessionState.EraseString(TtlSecondsKey);
            SessionState.EraseString(StartedAtTicksKey);
        }
    }

    /// <summary>
    /// The EditorApplication.update hook that enforces the deadline and reconciles
    /// <see cref="PlayModeSession"/> against reality - PER TICK, not boot-only (DESIGN-01 §9.2). Rides
    /// the same EditorApplication.update tick MainThreadPump does (VERIFIED by probe: it fires during
    /// Play Mode too, DESIGN-01 §14 item 3) rather than a background Timer, because unlike
    /// ReloadGate's TTL watchdog (which only PEEKS off-thread and defers the real Unity call) there is
    /// no off-thread peek/defer split needed here at all - the check IS the action, and both must run
    /// on the main thread regardless.
    ///
    /// <see cref="Install"/> only SUBSCRIBES (plus runs one immediate reconciliation pass, covering
    /// the post-reload boot case) - <see cref="Tick"/> is what actually reconciles, every single time
    /// it runs, which is the property that makes this design indifferent to whether Unity's own
    /// "Enter Play Mode Options" reload is even enabled (DESIGN-01 §9.2/§12).
    /// </summary>
    public static class PlayModeWatchdog
    {
        static bool _installed;

        /// <summary>Idempotent - called from HadesBoot's static ctor, which re-runs after every
        /// domain reload (the same argument that makes ReloadGate's own boot reconciliation real -
        /// see HadesBoot's own class doc comment).</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            EditorApplication.update += Tick;
            Tick(); // boot is simply the first tick - see this class's own doc comment.
        }

        /// <summary>Runs the DESIGN-01 §9.2 reconciliation table exactly once. Public so tests can
        /// drive it directly and deterministically, without waiting on a real EditorApplication.update
        /// tick (a Unity EditMode test cannot enter Play Mode from inside itself - see
        /// PlayModeCommandsTests' own class doc comment).</summary>
        public static void Tick()
        {
            if (!PlayModeSession.HasSession) return; // none, or user-owned playing - nothing to reconcile

            var playing = EditorPlayModeApi.IsPlaying();

            switch (PlayModeSession.Phase)
            {
                case PlayModeSession.PhaseRequested:
                    if (playing)
                    {
                        PlayModeSession.MarkPlaying(EditorPlayModeApi.PlayClock());
                    }
                    else if (PlayModeSession.DeadlinePassed(EditorPlayModeApi.PlayClock))
                    {
                        // The enter never landed (a lease taken in the residual window deferred it,
                        // or it failed outright) - cleared, ExitPlayMode() called ZERO times
                        // (DESIGN-01 §9.3).
                        PlayModeSession.Clear();
                    }
                    break;

                case PlayModeSession.PhasePlaying:
                    if (!playing)
                    {
                        // Session ended by user Stop, project.end_script_editing's own recompile, or
                        // a crash - DESIGN-01 §9.1's "Playing, Hades-owned | ... | none - reconciled
                        // at next boot" rows, generalised to every tick.
                        PlayModeSession.Clear();
                    }
                    else if (PlayModeSession.DeadlinePassed(EditorPlayModeApi.PlayClock))
                    {
                        // Phase flips BEFORE calling ExitPlayMode() so a later tick before the
                        // transition completes does not call it again (DESIGN-01 §9.3 - the same
                        // de-duplication intent as ReloadGate._releasePending).
                        PlayModeSession.SetPhase(PlayModeSession.PhaseExitRequested);
                        EditorPlayModeApi.ExitPlayMode();
                    }
                    break;

                case PlayModeSession.PhaseExitRequested:
                    if (!playing) PlayModeSession.Clear();
                    break;
            }
        }
    }

    /// <summary>
    /// The seam over EditorApplication.EnterPlaymode/ExitPlaymode/isPaused/Step/isPlaying and the
    /// scheduling primitive 'enter'/'exit' use to defer past the current pump tick - DESIGN-01 §6.1:
    /// "not a testing nicety here - they are the ONLY way this is testable at all. A Unity EditMode
    /// test cannot enter Play Mode from inside itself." Same swappable-public-static-field convention
    /// as ProjectCommands.TriggerRecompile/RefreshAssets/StartTestRun/RecordingClock, including the
    /// stated hygiene rule: a test reassigning one of these MUST restore it in TearDown.
    /// </summary>
    public static class EditorPlayModeApi
    {
        public static Action EnterPlayMode = () => EditorApplication.EnterPlaymode();
        public static Action ExitPlayMode = () => EditorApplication.ExitPlaymode();
        public static Action<bool> SetPaused = paused => EditorApplication.isPaused = paused;
        public static Action StepFrame = () => EditorApplication.Step();
        public static Func<bool> IsPlaying = () => EditorApplication.isPlaying;
        public static Func<bool> IsPaused = () => EditorApplication.isPaused;
        public static Func<DateTime> PlayClock = () => DateTime.UtcNow;

        /// <summary>Defers <paramref name="action"/> past the pump tick currently executing - real
        /// implementation is EditorApplication.delayCall (DESIGN-01 §7.2 step 4). Not one of
        /// DESIGN-01 §6.1's six named seams, but required by the same testability argument: without
        /// it, 'enter'/'exit' could not be proven to stamp SessionState/return a response BEFORE
        /// actually calling EnterPlayMode()/ExitPlayMode() (DESIGN-01 §15.1 test 1).</summary>
        public static Action<Action> ScheduleAfterThisTick = action => EditorApplication.delayCall += () => action();
    }
}
