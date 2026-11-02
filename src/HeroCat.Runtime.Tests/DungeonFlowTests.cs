using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Dungeons;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>The dungeon flow (01 section 8.8; issue #146): intent, seed seam, start, failures, kill recovery, sweep.</summary>
    public class DungeonFlowTests
    {
        const Dungeon Fish = Dungeon.FISH_COIN;

        sealed class Killed : Exception { }

        /// <summary>
        /// A fake remote seed service: an idempotent server keyed by requestId (the same id replays the same grant), which can
        /// fail before (offline) or after (timeout: the server stored the grant, the answer was lost) handling a request.
        /// </summary>
        sealed class FakeSeedServer : IDungeonSeedService
        {
            readonly Dictionary<string, DungeonSeedGrant> _byRequest = new Dictionary<string, DungeonSeedGrant>();
            ulong _next = 0x1234_5678_9abc_def0UL;
            public readonly List<DungeonSeedRequest> Requests = new List<DungeonSeedRequest>();
            public ServiceException FailBefore, FailAfter;
            public Func<long> ExpiresAt = () => long.MaxValue;

            public Task<DungeonSeedGrant> RequestSeedAsync(DungeonSeedRequest request, CancellationToken ct = default)
            {
                Requests.Add(request);
                if (FailBefore != null) throw FailBefore;
                if (!_byRequest.TryGetValue(request.RequestId, out var g))
                    _byRequest[request.RequestId] = g = new DungeonSeedGrant(_next++, Guid.NewGuid().ToString("D"), ExpiresAt(), LocalSeed: false);
                if (FailAfter != null) throw FailAfter;
                return Task.FromResult(g);
            }
        }

        sealed class SwitchedConfig : IConfigService
        {
            public SwitchedConfig(bool dungeons) =>
                Current = new ClientConfig("", null, MaintenanceInfo.None, KillSwitches.AllOn with { DungeonsEnabled = dungeons });

            public ClientConfig Current { get; }
            public KillSwitches Flags => Current.Flags;
            public Task<ClientConfig> FetchAsync(CancellationToken ct = default) => Task.FromResult(Current);
        }

        static readonly ServiceException Offline = new ServiceException(ErrorClass.Offline, ErrorClassifier.NET_OFFLINE);
        static readonly ServiceException Timeout = new ServiceException(ErrorClass.Timeout, ErrorClassifier.NET_TIMEOUT);

        /// <summary>An MVP session with dungeons unlocked (HighestCleared 30) and the day's tickets, optionally over a fake server.</summary>
        static TestSession Open(MemorySaveStore store = null, IDungeonSeedService seeds = null, IConfigService config = null)
        {
            var t = TestSession.Open(store: store ?? new MemorySaveStore(), content: TestContent.Mvp,
                                     services: s => s with { Dungeons = seeds ?? s.Dungeons, Config = config ?? s.Config });
            t.Sim.State.Progress.HighestCleared = Math.Max(30, t.Sim.State.Progress.HighestCleared);
            Assume.That(Tickets(t), Is.GreaterThan(0), "the first rollover tops the tickets up");
            return t;
        }

        static int Tickets(TestSession t) => t.Sim.State.Wallet.DungeonTicket[(int)Fish];

        static DungeonStartOutcome Start(TestSession t, int floor = 1) => t.Runner.Dungeons.StartAsync(Fish, floor).GetAwaiter().GetResult();

        static TestSession Relaunch(MemorySaveStore store, IDungeonSeedService seeds = null) =>
            Open(new MemorySaveStore { Primary = store.Primary?.ToArray(), Backup = store.Backup?.ToArray() }, seeds);

        static void PlayRun(TestSession t)
        {
            for (int i = 0; i < 1000 && t.Sim.State.Combat.Mode == CombatMode.Dungeon; i++) t.Frame(1);
            Assert.That(t.Sim.State.Combat.Mode, Is.EqualTo(CombatMode.Stage), "the run ends within BOSS_TIME_TICKS");
        }

        [Test]
        public void A_local_start_seeds_the_run_with_the_next_dungeon_draw_spends_nothing_and_clears_the_intent()
        {
            var t = Open();
            var s = t.Sim.State;
            ulong peek = LocalDungeonSeed.Peek(s);
            long counter = s.Rng[RngStreams.Dungeon].Counter;
            int tickets = Tickets(t);

            var r = Start(t);
            Assert.That(r.Ok, Is.True, r.ToString());
            Assert.That((r.Grant.LocalSeed, r.Grant.Seed, r.Grant.GrantId), Is.EqualTo((true, peek, "")));
            Assert.That(s.Combat.Mode, Is.EqualTo(CombatMode.Dungeon));
            Assert.That(s.Combat.DungeonRun.Seed, Is.EqualTo(peek));
            Assert.That(s.Rng[RngStreams.Dungeon].Counter, Is.EqualTo(counter + 1), "StartDungeon consumed the one draw");
            Assert.That(s.Pending.Dungeon, Is.Null, "the run state is the record once started");
            Assert.That(Tickets(t), Is.EqualTo(tickets), "no ticket at start (00 decision 16)");

            PlayRun(t);
            var last = s.Dungeons.RecentRuns.Last();
            Assert.That((last.GrantId, last.Kind, last.Floor), Is.EqualTo(("", Fish, 1)));
            Assert.That(Tickets(t), Is.EqualTo(last.Cleared ? tickets - 1 : tickets), "a clear debits one ticket, a fail nothing");
        }

        [Test]
        public void The_intent_is_on_disk_before_the_request_with_its_requestId()
        {
            var store = new MemorySaveStore();
            var server = new FakeSeedServer();
            var t = Open(store, server);
            PendingDungeonSeed onDisk = null;
            t.Runner.Dungeons.Checkpoint = p =>
            {
                if (p == DungeonFlowPoint.AfterIntentSave) onDisk = Relaunch(store).Sim.State.Pending.Dungeon;
            };
            Assert.That(Start(t, 1).Ok, Is.True);
            Assert.That(onDisk, Is.Not.Null);
            Assert.That((onDisk.Kind, onDisk.Floor), Is.EqualTo((Fish, 1)));
            Assert.That(onDisk.RequestId, Does.Match("^[0-9a-f]{32}$"));
            Assert.That(server.Requests.Single().RequestId, Is.EqualTo(onDisk.RequestId));
            Assert.That(Relaunch(store).Sim.State.Pending.Dungeon, Is.Null, "cleared with the start");
        }

        [Test]
        public void Offline_spends_nothing_keeps_the_intent_and_the_retry_resends_the_same_requestId()   // AC: seed offline
        {
            var server = new FakeSeedServer { FailBefore = Offline };
            var t = Open(seeds: server);
            int tickets = Tickets(t);

            var r = Start(t);
            Assert.That((r.Ok, r.Error, r.Code, r.Retryable), Is.EqualTo((false, DungeonStartError.Network, ErrorClassifier.NET_OFFLINE, true)));
            Assert.That(t.Sim.State.Combat.Mode, Is.EqualTo(CombatMode.Stage));
            Assert.That(Tickets(t), Is.EqualTo(tickets));
            Assert.That(t.Runner.Dungeons.Pending, Is.Not.Null);

            server.FailBefore = null;
            Assert.That(Start(t).Ok, Is.True);
            Assert.That(server.Requests.Select(q => q.RequestId).Distinct().Count(), Is.EqualTo(1), "the retry reuses the saved requestId");
        }

        [Test]
        public void A_timeout_then_retry_gets_the_same_seed_from_the_server_replay()   // AC: seed timeout then retry
        {
            var server = new FakeSeedServer { FailAfter = Timeout };
            var t = Open(seeds: server);
            var r = Start(t);
            Assert.That((r.Error, r.Code), Is.EqualTo((DungeonStartError.Network, ErrorClassifier.NET_TIMEOUT)));

            server.FailAfter = null;
            var again = Start(t);
            Assert.That(again.Ok, Is.True);
            Assert.That(server.Requests.Select(q => q.RequestId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(t.Sim.State.Combat.DungeonRun.Seed, Is.EqualTo(0x1234_5678_9abc_def0UL), "the seed the lost answer carried");
            Assert.That(t.Sim.State.Combat.DungeonRun.GrantId, Is.EqualTo(again.Grant.GrantId));
        }

        [Test]
        public void An_intent_left_by_a_kill_before_the_seed_is_replayed_for_the_same_floor_and_replaced_for_another()
        {
            var store = new MemorySaveStore();
            var t = Open(store, new FakeSeedServer());
            t.Runner.Dungeons.Checkpoint = p => { if (p == DungeonFlowPoint.AfterIntentSave) throw new Killed(); };
            Assert.Throws<Killed>(() => Start(t));
            string saved = Relaunch(store).Sim.State.Pending.Dungeon.RequestId;

            var other = new FakeSeedServer();
            var second = Relaunch(store, other);
            second.Sim.State.Dungeons.BestFloor[(int)Fish] = 1;
            Assert.That(second.Runner.Dungeons.StartAsync(Fish, 2).GetAwaiter().GetResult().Ok, Is.True);
            Assert.That(other.Requests.Single().RequestId, Is.Not.EqualTo(saved), "another floor: a new intent");

            var third = Relaunch(store, other = new FakeSeedServer());
            Assert.That(Start(third, 1).Ok, Is.True);
            Assert.That(other.Requests.Single().RequestId, Is.EqualTo(saved), "the same floor: the saved requestId");
        }

        [Test]
        public void A_server_refusal_drops_the_intent_and_spends_nothing()
        {
            var server = new FakeSeedServer { FailBefore = new ServiceException(ErrorClass.Rejected, "DUNGEON_SEED_CAP", status: 429) };
            var t = Open(seeds: server);
            int tickets = Tickets(t);
            var r = Start(t);
            Assert.That((r.Error, r.Code, r.Retryable), Is.EqualTo((DungeonStartError.Refused, "DUNGEON_SEED_CAP", false)));
            Assert.That(t.Runner.Dungeons.Pending, Is.Null);
            Assert.That((Tickets(t), t.Sim.State.Combat.Mode), Is.EqualTo((tickets, CombatMode.Stage)));
        }

        [Test]
        public void An_expired_replayed_grant_is_dropped_and_one_fresh_request_is_made()
        {
            var server = new FakeSeedServer();
            var t = Open(seeds: server);
            long now = t.Runner.Services.Clock.NowServerSeconds;
            int calls = 0;
            server.ExpiresAt = () => calls++ == 0 ? now - 1 : now + 600;
            var r = Start(t);
            Assert.That(r.Ok, Is.True, r.ToString());
            Assert.That(server.Requests.Select(q => q.RequestId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(r.Grant.ExpiresAtServerSeconds, Is.EqualTo(now + 600));
        }

        [Test]
        public void A_kill_mid_run_resets_to_stage_with_no_ticket_change_and_no_reward_and_tracks_abandoned()   // AC: kill mid-run
        {
            var store = new MemorySaveStore();
            var t = Open(store, new FakeSeedServer());
            var s = t.Sim.State;
            int tickets = Tickets(t);
            double fishCoins = s.Wallet.Get(Currency.FISH_COIN);
            Assert.That(Start(t).Ok, Is.True);
            t.Frame(1);
            Assume.That(s.Combat.Mode, Is.EqualTo(CombatMode.Dungeon));
            t.Runner.SaveNow(SaveTrigger.Periodic);   // the last save before the kill holds the run

            var analytics = new MemoryAnalytics();
            var relaunched = TestSession.Open(store: new MemorySaveStore { Primary = store.Primary?.ToArray(), Backup = store.Backup?.ToArray() },
                                              analytics: analytics, content: TestContent.Mvp);
            var r = relaunched.Sim.State;
            Assert.That((r.Combat.Mode, r.Combat.DungeonRun), Is.EqualTo((CombatMode.Stage, (DungeonRunState)null)));
            Assert.That(r.Wallet.DungeonTicket[(int)Fish], Is.EqualTo(tickets), "nothing to refund: no ticket was spent");
            Assert.That(r.Wallet.Get(Currency.FISH_COIN), Is.EqualTo(fishCoins).Within(1e-9), "no reward");
            Assert.That(r.Pending.Dungeon, Is.Null);
            var abandoned = analytics.Events.Where(e => e.Name == AnalyticsNames.DUNGEON_RUN).Select(e => e.Props["outcome"]).ToList();
            Assert.That(abandoned, Is.EqualTo(new object[] { "abandoned" }));
        }

        [Test]
        public void Remote_offline_blocks_a_new_run_but_sweeps_a_cleared_floor()   // AC: remote offline
        {
            var t = Open(seeds: new FakeSeedServer { FailBefore = Offline });
            var s = t.Sim.State;
            s.Dungeons.BestFloor[(int)Fish] = 3;
            int tickets = Tickets(t);
            double coins = s.Wallet.Get(Currency.FISH_COIN);

            Assert.That(Start(t, 4).Error, Is.EqualTo(DungeonStartError.Network));
            var sweep = t.Runner.Dungeons.Sweep(Fish);
            Assert.That(sweep.Ok, Is.True, sweep.ToString());
            Assert.That(Tickets(t), Is.EqualTo(tickets - 1));
            Assert.That(s.Wallet.Get(Currency.FISH_COIN), Is.GreaterThan(coins));
        }

        [Test]
        public void Preconditions_and_the_kill_switch_refuse_before_any_request()
        {
            var server = new FakeSeedServer();
            var off = Open(seeds: server, config: new SwitchedConfig(dungeons: false));
            Assert.That((Start(off).Error, Start(off).Code), Is.EqualTo((DungeonStartError.Disabled, DungeonFlow.FEATURE_DISABLED)));

            var t = Open(seeds: server);
            Assert.That((Start(t, 2).Error, Start(t, 2).Code), Is.EqualTo((DungeonStartError.Precondition, DungeonRejectReasons.DUNGEON_FLOOR_NOT_UNLOCKED)));
            t.Sim.State.Wallet.DungeonTicket[(int)Fish] = 0;
            Assert.That(Start(t, 1).Code, Is.EqualTo(CollectionRejectReasons.NotEnough(Currency.TICKET_FISH_COIN)));
            t.Sim.State.Progress.HighestCleared = 0;
            Assert.That(Start(t, 1).Code, Is.EqualTo(DungeonRejectReasons.DUNGEON_LOCKED));
            Assert.That(server.Requests, Is.Empty);
            Assert.That(t.Runner.Dungeons.Pending, Is.Null);
        }

        [Test]
        public void Local_runs_are_deterministic_across_two_sessions()   // AC: bots deterministic through LocalDungeonSeedService
        {
            string Play()
            {
                var t = Open();
                for (int floor = 1; floor <= 2; floor++)
                {
                    if (t.Runner.Dungeons.CheckStart(Fish, floor) != null) break;
                    Assert.That(Start(t, floor).Ok, Is.True);
                    PlayRun(t);
                }
                t.Play(30);
                return t.Sim.StateHash();
            }
            Assert.That(Play(), Is.EqualTo(Play()));
        }
    }
}
