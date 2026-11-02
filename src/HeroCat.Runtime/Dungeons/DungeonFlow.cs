using System;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Dungeons
{
    /// <summary>The points of the start flow where a process kill can land (test hook, <see cref="DungeonFlow.Checkpoint"/>).</summary>
    public enum DungeonFlowPoint { BeforeIntentSave, AfterIntentSave, AfterServiceReturned, AfterStart }

    /// <summary>Why <see cref="DungeonFlow.StartAsync"/> did not start a run.</summary>
    public enum DungeonStartError
    {
        None,

        /// <summary>The <c>dungeonsEnabled</c> kill switch is off (01 section 8.12): <see cref="DungeonFlow.FEATURE_DISABLED"/>.</summary>
        Disabled,

        /// <summary>Another start is waiting for its seed.</summary>
        InFlight,

        /// <summary>A view-model precondition failed (01 section 8.8 step 1); Code is the sim's reject reason. No request was made.</summary>
        Precondition,

        /// <summary>The intent could not be saved, so no seed was requested.</summary>
        SaveFailed,

        /// <summary>
        /// Offline, timed out or a retryable server answer (Code NET_OFFLINE, NET_TIMEOUT, ...). The intent stays saved:
        /// starting the same kind and floor again re-sends its requestId, and the server replays the same seed.
        /// </summary>
        Network,

        /// <summary>The server refused the seed (Code DUNGEON_SEED_CAP, DUNGEON_LOCKED, CONTENT_TOO_OLD, ...). The intent is dropped.</summary>
        Refused,

        /// <summary>The sim rejected StartDungeon with the seed in hand (Code is its reason); nothing was spent.</summary>
        StartRejected,
    }

    /// <summary>The end of one start: Ok with the grant the run was seeded with, or the error and its code.</summary>
    public sealed record DungeonStartOutcome(bool Ok, DungeonStartError Error, string Code, DungeonSeedGrant Grant)
    {
        /// <summary>Starting again (same kind and floor) is the right next step: the failure was the network.</summary>
        public bool Retryable => Error == DungeonStartError.Network;

        internal static DungeonStartOutcome Fail(DungeonStartError e, string code) => new DungeonStartOutcome(false, e, code, null);
    }

    /// <summary>
    /// The Runtime dungeon flow of 01 section 8.8 (issue #146):
    /// <list type="number">
    /// <item>view-model preconditions (<see cref="CheckStart"/>): the DUNGEONS unlock, no run or boss attempt active, the
    /// floor unlocked (<c>floor &lt;= BestFloor + 1</c>) and a ticket held, each failing with the sim's own reason;</item>
    /// <item>persist <c>Pending.Dungeon = {requestId, kind, floor}</c> and save before the request (a failed save refuses);</item>
    /// <item>ask <see cref="IDungeonSeedService"/>: Local, the peeked next <c>dungeon</c> draw; Remote, <c>POST /dungeon/seed</c>;</item>
    /// <item>apply the logged <c>StartDungeon{kind, floor, seed, grantId, localSeed}</c>, clear the intent, publish, save.</item>
    /// </list>
    /// No ticket moves at start: the sim debits one on a clear only (00 decision 16), so a failed seed request, a failed
    /// run and a run abandoned by a process kill (02 P16 on the next load) all cost nothing. The run itself ticks in the
    /// main loop and ends in DungeonCleared or DungeonFailed, which append to <c>Dungeons.RecentRuns</c> for the server's
    /// correlation (08 section 7.3); the next save upload carries it.
    ///
    /// Decisions (issue #146): the intent is cleared when StartDungeon is applied, not at the run's end, so no save taken
    /// during a run carries Runtime bookkeeping a command-log replay cannot reproduce (08 section 7.3; the run itself is
    /// <c>Combat.DungeonRun</c>, recovered by P16). An intent left by a kill before the seed came back is kept: starting
    /// the same kind and floor re-sends its requestId (the server replays the same seed, so a kill cannot re-roll a
    /// seed), another kind or floor replaces it, and P17 drops it after PENDING_STALE_DAYS. A replayed grant that has
    /// already expired is dropped and one fresh request is made.
    /// Main thread only, like <see cref="HeroCat.Runtime.Summons.SummonFlow"/>.
    /// </summary>
    public sealed class DungeonFlow
    {
        /// <summary>01 section 8.12's code for a button a kill switch disables.</summary>
        public const string FEATURE_DISABLED = "FEATURE_DISABLED";

        /// <summary>09 section 5.34's feature_unlocks row for dungeons (the sim's gate).</summary>
        public const string DungeonsFeature = "DUNGEONS";

        readonly SessionRunner _runner;
        bool _inFlight;

        public DungeonFlow(SessionRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        /// <summary>Test hook: called at each kill point. A test simulates a kill by snapshotting the store there.</summary>
        internal Action<DungeonFlowPoint> Checkpoint { get; set; }

        /// <summary>The saved intent (a start waiting for, or killed before, its seed), or null.</summary>
        public PendingDungeonSeed Pending => _runner.Sim.State.Pending.Dungeon;

        /// <summary>True while a start waits for its seed.</summary>
        public bool InFlight => _inFlight;

        /// <summary>
        /// The view-model's preconditions (01 section 8.8 step 1), in the sim's order: null when StartDungeon(kind, floor)
        /// would be accepted, else its reject reason (DUNGEON_LOCKED, INVALID_DUNGEON, DUNGEON_RUN_IN_PROGRESS,
        /// BOSS_ATTEMPT_IN_PROGRESS, DUNGEON_FLOOR_NOT_UNLOCKED, NOT_ENOUGH_TICKET_&lt;kind&gt;). The kill switch is separate.
        /// </summary>
        public string CheckStart(Dungeon kind, int floor)
        {
            var sim = _runner.Sim;
            var s = sim.State;
            if (!sim.Db.IsUnlocked(DungeonsFeature, s)) return DungeonRejectReasons.DUNGEON_LOCKED;
            var def = sim.Db.DungeonOf(kind);
            if (def?.TicketCurrency == null) return DungeonRejectReasons.INVALID_DUNGEON;
            var c = s.Combat;
            if (c.Mode == CombatMode.Dungeon) return DungeonRejectReasons.DUNGEON_RUN_IN_PROGRESS;
            if (c.Fight != null && c.Fight.Phase == FightPhase.Fighting && c.Fight.EnemyIsBoss) return DungeonRejectReasons.BOSS_ATTEMPT_IN_PROGRESS;
            int best = s.Dungeons.BestFloor[(int)kind];
            if (floor < 1 || floor > best + 1 || def.Floor(floor) == null) return DungeonRejectReasons.DUNGEON_FLOOR_NOT_UNLOCKED;
            var ticket = def.TicketCurrency.Value;
            if (!s.Wallet.CanAfford(ticket, 1)) return CollectionRejectReasons.NotEnough(ticket);
            return null;
        }

        /// <summary>True when the dungeons kill switch allows a new run (Local mode: always).</summary>
        public bool Enabled => _runner.Services.Config?.Flags?.DungeonsEnabled ?? true;

        /// <summary>SCR_DUNGEONS' Enter button: requests a seed and starts the run (class summary).</summary>
        public async Task<DungeonStartOutcome> StartAsync(Dungeon kind, int floor, CancellationToken ct = default)
        {
            if (_inFlight) return DungeonStartOutcome.Fail(DungeonStartError.InFlight, null);
            if (!Enabled) return DungeonStartOutcome.Fail(DungeonStartError.Disabled, FEATURE_DISABLED);
            string reason = CheckStart(kind, floor);
            if (reason != null) return DungeonStartOutcome.Fail(DungeonStartError.Precondition, reason);
            _inFlight = true;
            try
            {
                var first = await AttemptAsync(kind, floor, ct).ConfigureAwait(false);
                if (first.expired) return (await AttemptAsync(kind, floor, ct).ConfigureAwait(false)).outcome;   // a fresh requestId
                return first.outcome;
            }
            finally
            {
                _inFlight = false;
            }
        }

        async Task<(DungeonStartOutcome outcome, bool expired)> AttemptAsync(Dungeon kind, int floor, CancellationToken ct)
        {
            var svc = _runner.Services.Dungeons ?? throw new InvalidOperationException("no dungeon seed service in this GameServices");
            var state = _runner.Sim.State;
            var previous = state.Pending.Dungeon;
            var intent = previous != null && previous.Kind == kind && previous.Floor == floor
                ? previous   // a start killed or failed before its seed came back: same requestId, the server replays its seed
                : new PendingDungeonSeed(Guid.NewGuid().ToString("N"), kind, floor, _runner.Services.Clock.NowServerSeconds);
            Hit(DungeonFlowPoint.BeforeIntentSave);
            state.Pending.Dungeon = intent;
            if (!_runner.SaveNow(SaveTrigger.Dungeon))
            {
                state.Pending.Dungeon = previous;   // the intent is not on disk, so no request may go out
                return (DungeonStartOutcome.Fail(DungeonStartError.SaveFailed, null), false);
            }
            Hit(DungeonFlowPoint.AfterIntentSave);

            DungeonSeedGrant grant;
            try
            {
                grant = await svc.RequestSeedAsync(new DungeonSeedRequest(intent.RequestId, kind, floor), ct).ConfigureAwait(false);
            }
            catch (ServiceException e)
            {
                if (e.Retryable) return (DungeonStartOutcome.Fail(DungeonStartError.Network, e.Code), false);   // keep the intent
                Clear();
                return (DungeonStartOutcome.Fail(DungeonStartError.Refused, e.Code), false);
            }
            Hit(DungeonFlowPoint.AfterServiceReturned);
            if (!grant.LocalSeed && grant.ExpiresAtServerSeconds > 0 && grant.ExpiresAtServerSeconds <= _runner.Services.Clock.NowServerSeconds)
            {
                Clear();   // a replay of a grant issued more than DUNGEON_SEED_TTL_S ago: ask for a new one
                return (DungeonStartOutcome.Fail(DungeonStartError.Refused, null), true);
            }

            // The run state is the record from here on (Combat.DungeonRun, P16 on a kill), so the intent goes with the start.
            state.Pending.Dungeon = null;
            var applied = _runner.Issue(new StartDungeon(kind, floor, grant.Seed, grant.GrantId ?? "", grant.LocalSeed));
            _runner.PublishDrained();
            _runner.SaveNow(SaveTrigger.Dungeon);
            Hit(DungeonFlowPoint.AfterStart);
            return applied.Ok
                ? (new DungeonStartOutcome(true, DungeonStartError.None, null, grant), false)
                : (DungeonStartOutcome.Fail(DungeonStartError.StartRejected, applied.Reason), false);
        }

        /// <summary>SCR_DUNGEONS' Sweep button (01 section 8.8 step 5): no seed, so it works offline. Returns the sim's result.</summary>
        public ApplyResult Sweep(Dungeon kind)
        {
            var r = _runner.Issue(new SweepDungeon(kind));
            if (!r.Ok) return r;
            _runner.PublishDrained();
            _runner.SaveNow(SaveTrigger.Dungeon);
            return r;
        }

        void Clear()
        {
            _runner.Sim.State.Pending.Dungeon = null;
            _runner.SaveNow(SaveTrigger.Dungeon);
        }

        void Hit(DungeonFlowPoint p) => Checkpoint?.Invoke(p);
    }
}
