using System;
using System.Linq;
using HeroCat.Runtime.Profile;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>
    /// Dungeons (06 sections 6.1-6.4 and 4.2; acceptance 4, 13-17; issues #172, #173): content, the ticked run, the preview,
    /// the seed seam, tickets, StartDungeon / SweepDungeon and the floor rewards.
    /// </summary>
    public class DungeonTests
    {
        const long Day = 86_400;
        static readonly long T0 = TestContent.T0;
        const Dungeon Fish = Dungeon.FISH_COIN;

        static ContentDb Mvp => TestContent.Mvp;

        static GameState NewState(ContentDb db, long now) =>
            ProfileFactory.Create(db, Guid.Empty, now, new Guid("de71ce00-0000-4000-8000-000000000003"), TestContent.Root);

        /// <summary>An MVP profile anchored at <paramref name="now"/> (the first rollover tops the tickets up), dungeons unlocked.</summary>
        static Simulation NewSim(long now = -1, bool unlocked = true, ContentDb db = null)
        {
            db ??= Mvp;
            long t = now < 0 ? T0 : now;
            var sim = new Simulation(NewState(db, t), db);
            sim.Apply(new ApplyServerTime(t));
            if (unlocked) sim.State.Progress.HighestCleared = 30;
            sim.DrainEvents();
            return sim;
        }

        static void SetAtk(Simulation sim, int level)
        {
            sim.State.Training.Level[(int)Stat.ATK] = level;
            sim.Query.MarkDirty();
        }

        /// <summary>Strong enough to clear floor 1 well inside the time limit.</summary>
        static Simulation StrongSim(long now = -1)
        {
            var sim = NewSim(now);
            SetAtk(sim, 300);
            Assume.That(sim.PreviewDungeon(Fish, 1).Cleared, Is.True, "ATK 300 clears floor 1");
            return sim;
        }

        static int Tickets(Simulation sim, Dungeon d) => sim.State.Wallet.DungeonTicket[(int)d];

        static StartDungeon Local(Simulation sim, Dungeon d, int floor) =>
            new StartDungeon(d, floor, LocalDungeonSeed.Peek(sim.State), "", LocalSeed: true);

        /// <summary>Ticks until the run ends; returns the ticks taken.</summary>
        static int RunToEnd(Simulation sim)
        {
            int n = 0;
            while (sim.State.Combat.Mode == CombatMode.Dungeon)
            {
                sim.Advance(1);
                Assert.That(++n, Is.LessThanOrEqualTo(sim.Db.Constants.BossTimeTicks), "a run ends within BOSS_TIME_TICKS");
            }
            return n;
        }

        static void AssertRejectIsPure(Simulation sim, Command c, RejectCode code, string reason)
        {
            sim.DrainEvents();
            string before = sim.StateHash();
            var r = sim.Apply(c);
            Assert.That((r.Ok, r.Code, r.Reason), Is.EqualTo((false, code, reason)), c.ToString());
            Assert.That(sim.StateHash(), Is.EqualTo(before), c.ToString());
            Assert.That(sim.DrainEvents(), Is.Empty, c.ToString());
        }

        static double Rel(double a, double b) => Math.Abs(a - b) / Math.Abs(b);

        // ------------------------------------------------------------------------------------------------ content

        [Test]
        public void Content_ThreeDungeons_TwoTicketsADay_Step20_FortyFloors_M0HasNone()
        {
            foreach (var (kind, id, ticket) in new[]
                     {
                         (Dungeon.FISH_COIN, "dungeon_fish", Currency.TICKET_FISH_COIN),
                         (Dungeon.ROCK_CANDY, "dungeon_candy", Currency.TICKET_ROCK_CANDY),
                         (Dungeon.EXP, "dungeon_exp", Currency.TICKET_EXP),
                     })
            {
                var d = Mvp.DungeonOf(kind);
                Assert.That((d.Id, d.Kind, d.TicketCurrency, d.TicketsPerDay, d.FloorStageStep, d.Floors, d.SweepAllowed),
                            Is.EqualTo((id, (Dungeon?)kind, (Currency?)ticket, 2, 20, 40, true)), "06 sections 6.1-6.4, 09 section 5.24");
                Assert.That(d.FloorDefs.Select(f => f.Floor), Is.EqualTo(Enumerable.Range(1, 40)));
                Assert.That(d.FloorDefs.Select(f => f.StageEquiv), Is.EqualTo(Enumerable.Range(1, 40).Select(f => 20 * f)));
                Assert.That(Mvp.Dungeons[id], Is.SameAs(d));
            }
            Assert.That(TestContent.Db.Dungeons, Is.Empty, "dungeons are MVP");
            Assert.That(TestContent.Db.DungeonOf(Fish), Is.Null);
            Assert.That(Mvp.DungeonOf((Dungeon)7), Is.Null);
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(12)]
        [TestCase(27)]
        public void AC17_FishCoinAndRockCandyFloors_AreLinear(int f)
        {
            var fish = Simulation.RewardOf(Mvp.DungeonOf(Dungeon.FISH_COIN).Floor(f));
            var candy = Simulation.RewardOf(Mvp.DungeonOf(Dungeon.ROCK_CANDY).Floor(f));
            Assert.That((fish.Currency, fish.Amount), Is.EqualTo((Currency.FISH_COIN, 50.0 * f)), "FISH_COIN_FLOOR(f) = 50 f");   // TODO(#165): the curve may change
            Assert.That((candy.Currency, candy.Amount), Is.EqualTo((Currency.ROCK_CANDY, 10.0 * f)), "ROCK_CANDY_FLOOR(f) = 10 f");
        }

        [Test]
        public void AC17_ExpFloor_Is300KillsAtStage20f_WithTheCurrentExpCurve()
        {
            var exp = Simulation.RewardOf(Mvp.DungeonOf(Dungeon.EXP).Floor(4));
            Assert.That(exp.Currency, Is.EqualTo(Currency.HERO_EXP));
            Assert.That(Rel(exp.Amount, 300 * 5 * Math.Pow(1.025, 80)), Is.LessThan(1e-9), "EXP_FLOOR(4) = 300 * ExpPerKill(80), EXP0 5, EXP_G 1.025");
            Assert.That(exp.Amount, Is.EqualTo(300 * Mvp.ExpPerKill.Eval(80)));
            Assert.That(Rel(Simulation.RewardOf(Mvp.DungeonOf(Dungeon.EXP).Floor(1)).Amount, 2458.0), Is.LessThan(1e-3), "06 section 6.3 table: 2,458");
        }

        // ------------------------------------------------------------------------------------------------ the fight (06 6.1)

        [Test]
        public void FloorBossHp_IsTheStage20fBoss_ClosedFormAtFloor1_FiniteAtFloor40()
        {
            var sim = NewSim();
            Assert.That(sim.Apply(Local(sim, Fish, 1)).Ok, Is.True);
            double hp1 = sim.State.Combat.DungeonRun.Fight.EnemyMaxHp;
            Assert.That(Rel(hp1, 20 * Math.Pow(1.04, 20) * 10), Is.LessThan(1e-9), "HP0 * HP_G^(20 * 1) * BOSS_HP_MULT");
            Assert.That(sim.State.Combat.DungeonRun.TicksLeft, Is.EqualTo(sim.Db.Constants.BossTimeTicks));

            var p40 = Simulation.PreviewDungeon(sim.State, Mvp, Fish, 40, seed: 1);
            Assert.That(double.IsFinite(p40.Result.BossMaxHp) && p40.Result.BossMaxHp > 0, Is.True, "floor 40 = stage 800 does not overflow");
            Assert.That(Rel(p40.Result.BossMaxHp, 10 * Mvp.EnemyHp.Eval(800)), Is.LessThan(1e-12));
            Assert.That(p40.Cleared, Is.False);
        }

        [Test]
        public void Run_IsTickedWithCritsOnTheRunStream_FrontierFrozen_NoKillPay()
        {
            var sim = StrongSim();
            sim.Advance(5);   // a frontier mob is up and partly hit
            var frontier = StateCopy.DeepClone(sim.State).Combat;
            long crit = sim.State.Rng[RngStreams.Crit].Counter, hits = sim.State.Stats.HeroHits, kills = sim.State.Stats.Kills;
            double gold = sim.State.Wallet.Gold;
            sim.DrainEvents();

            Assert.That(sim.Apply(Local(sim, Fish, 1)).Ok, Is.True);
            Assert.That(sim.DrainEvents().OfType<DungeonStarted>().Single().Floor, Is.EqualTo(1));
            Assert.That(sim.GetSnapshot().Mode, Is.EqualTo(CombatMode.Dungeon));
            Assert.That(sim.GetSnapshot().EnemyHpMax, Is.EqualTo(sim.State.Combat.DungeonRun.Fight.EnemyMaxHp), "the HUD shows the run's boss");
            sim.Advance(1);
            Assert.That(sim.State.Combat.DungeonRun.StreamCounter, Is.GreaterThanOrEqualTo(0));
            RunToEnd(sim);

            var c = sim.State.Combat;
            Assert.That((c.Mode, c.DungeonRun), Is.EqualTo((CombatMode.Stage, (DungeonRunState)null)));
            Assert.That((c.Stage, c.KillsInStage, c.Boss, c.Fight.EnemyHp, c.Fight.SpawnTimer, c.Fight.Phase),
                        Is.EqualTo((frontier.Stage, frontier.KillsInStage, frontier.Boss, frontier.Fight.EnemyHp, frontier.Fight.SpawnTimer, frontier.Fight.Phase)),
                        "the frontier resumes unchanged (02 section 3.3)");
            Assert.That((sim.State.Rng[RngStreams.Crit].Counter, sim.State.Stats.HeroHits, sim.State.Stats.Kills), Is.EqualTo((crit, hits, kills)),
                        "dungeon hits draw from dungeon_run, not crit (03 section 10)");
            Assert.That(sim.State.Wallet.Gold, Is.EqualTo(gold), "the boss pays only the floor reward");
            Assert.That(sim.LastDungeonRun.StreamCounter, Is.GreaterThan(0), "one dungeon_run draw per hero hit");
        }

        [Test]
        public void Run_MissilesWait_TapZapAndAttemptBossRejected_BoardLocked()
        {
            var sim = StrongSim();
            sim.State.Combat.MissileQueue.Add(0.5);
            Assert.That(sim.Apply(Local(sim, Fish, 1)).Ok, Is.True);
            sim.Advance(3);
            Assert.That(sim.State.Combat.MissileQueue, Is.EqualTo(new[] { 0.5 }), "the queue is not drained in a run (03 section 2.5)");
            AssertRejectIsPure(sim, new TapZap(3), RejectCode.NotAvailable, CombatRejectReasons.DUNGEON_RUN_IN_PROGRESS);
            AssertRejectIsPure(sim, new AttemptBoss(), RejectCode.NotAvailable, CombatRejectReasons.DUNGEON_RUN_IN_PROGRESS);
            AssertRejectIsPure(sim, new ReleaseFish(), RejectCode.Locked, "BOARD_LOCKED");
        }

        // ------------------------------------------------------------------------------------------------ determinism, preview, seeds

        // The x64/ARM64 half of AC 15 is the bot golden (10): TODO(#214): the greedy bot runs dungeons, so the seed-7 golden
        // log and its day hashes cover runs on every nightly OS.
        [Test]
        public void AC15_SameSaveSameSeedSameFloor_IdenticalOutcomeAndStreamCounter()
        {
            var save = StrongSim().State;
            var a = new Simulation(StateCopy.DeepClone(save), Mvp);
            var b = new Simulation(StateCopy.DeepClone(save), Mvp);
            ulong seed = 0x1234_5678_9abc_def0UL;
            foreach (var s in new[] { a, b })
            {
                Assert.That(s.Apply(new StartDungeon(Fish, 1, seed, "g1", LocalSeed: false)).Ok, Is.True);
                RunToEnd(s);
            }
            Assert.That(b.LastDungeonRun, Is.EqualTo(a.LastDungeonRun));
            Assert.That(b.StateHash(), Is.EqualTo(a.StateHash()));
        }

        [Test]
        public void Preview_LeavesTheStateUnchanged_AndAgreesWithTheLocalRun()
        {
            var sim = StrongSim();
            sim.Advance(7);
            string before = sim.StateHash();
            var p = sim.PreviewDungeon(Fish, 1);
            Assert.That(sim.StateHash(), Is.EqualTo(before), "PreviewDungeon changes nothing");
            Assert.That(p.Cleared, Is.True);
            Assert.That(p.DpsRatioNeeded, Is.EqualTo((double)p.Result.TicksUsed / Mvp.Constants.BossTimeTicks));

            Assert.That(sim.Apply(Local(sim, Fish, 1)).Ok, Is.True);
            RunToEnd(sim);
            Assert.That(sim.LastDungeonRun, Is.EqualTo(p.Result), "same seed, same outcome, same dungeon_run counter");
        }

        [Test]
        public void Preview_Failed_ReportsRemainingHpAndTheDpsMultipleNeeded()
        {
            var sim = NewSim();
            var p = sim.PreviewDungeon(Fish, 1, seed: 42);
            Assume.That(p.Cleared, Is.False, "a fresh hero cannot clear the stage-20 boss");
            Assert.That(p.RemainingFraction, Is.GreaterThan(0).And.LessThan(1));
            Assert.That(p.DpsRatioNeeded, Is.EqualTo(1.0 / (1.0 - p.RemainingFraction)).Within(1e-12));
            Assert.That(p.Result.TicksUsed, Is.EqualTo(Mvp.Constants.BossTimeTicks));
            Assert.That(sim.PreviewDungeon(Fish, 41), Is.Null, "no floor 41 in MVP");
            Assert.That(Simulation.PreviewDungeon(sim.State, TestContent.Db, Fish, 1), Is.Null, "no dungeons in M0");
        }

        [Test]
        public void Seeds_LocalRunAdvancesTheDungeonStreamByOne_ServerSeededRunLeavesItAlone()
        {
            var sim = StrongSim();
            var stream = sim.State.Rng[RngStreams.Dungeon];
            long c0 = stream.Counter;
            ulong peek = LocalDungeonSeed.Peek(sim.State);
            Assert.That(LocalDungeonSeed.Peek(sim.State), Is.EqualTo(peek), "a peek consumes nothing");
            Assert.That(sim.Apply(Local(sim, Fish, 1)).Ok, Is.True);
            Assert.That(sim.State.Combat.DungeonRun.Seed, Is.EqualTo(peek));
            Assert.That(stream.Counter, Is.EqualTo(c0 + 1), "M0: exactly one dungeon draw per run");
            RunToEnd(sim);
            Assert.That(stream.Counter, Is.EqualTo(c0 + 1));

            Assert.That(sim.Apply(new StartDungeon(Fish, 1, 99, "grant-1", LocalSeed: false)).Ok, Is.True);
            RunToEnd(sim);
            Assert.That(stream.Counter, Is.EqualTo(c0 + 1), "MVP: the server seed leaves the persistent stream untouched");
            Assert.That(sim.State.Dungeons.RecentRuns.Last(), Is.EqualTo(new DungeonRunRecord("grant-1", Fish, 1, true)));
        }

        // ------------------------------------------------------------------------------------------------ tickets (06 4.2, AC 4)

        [TestCase(0, 2)]
        [TestCase(1, 2)]
        [TestCase(2, 2)]
        [TestCase(5, 5)]
        public void AC4_Rollover_TopsTicketsUpToTwo_NeverDown(int have, int after)
        {
            var sim = NewSim();
            Assert.That(Tickets(sim, Fish), Is.EqualTo(2), "a new profile's first day");
            sim.State.Wallet.DungeonTicket[(int)Fish] = have;
            sim.DrainEvents();
            sim.Apply(new ApplyServerTime(T0 + Day + 60));
            Assert.That(Tickets(sim, Fish), Is.EqualTo(after));
            var cc = sim.DrainEvents().OfType<CurrencyChanged>().Where(e => e.Currency == Currency.TICKET_FISH_COIN).ToList();
            if (have < 2) Assert.That(cc.Select(e => (e.Delta, e.Reason)), Is.EqualTo(new[] { (2.0 - have, CurrencyReason.DAILY_RESET) }));
            else Assert.That(cc, Is.Empty);
        }

        [Test]
        public void Rollover_M0HasNoTickets()
        {
            var sim = NewSim(db: TestContent.Db);
            Assert.That(sim.State.Wallet.DungeonTicket, Is.EqualTo(new[] { 0, 0, 0 }));
        }

        // ------------------------------------------------------------------------------------------------ StartDungeon (AC 13, 14)

        [Test]
        public void AC14_StartDungeon_Rejections()
        {
            var sim = NewSim(unlocked: false);
            sim.State.Progress.HighestCleared = 29;
            AssertRejectIsPure(sim, Local(sim, Fish, 1), RejectCode.Locked, DungeonRejectReasons.DUNGEON_LOCKED);
            AssertRejectIsPure(sim, new SweepDungeon(Fish), RejectCode.Locked, DungeonRejectReasons.DUNGEON_LOCKED);
            sim.State.Progress.HighestCleared = 30;

            AssertRejectIsPure(sim, Local(sim, Fish, 2), RejectCode.Locked, DungeonRejectReasons.DUNGEON_FLOOR_NOT_UNLOCKED);   // bestFloor + 2
            AssertRejectIsPure(sim, Local(sim, Fish, 0), RejectCode.Locked, DungeonRejectReasons.DUNGEON_FLOOR_NOT_UNLOCKED);
            AssertRejectIsPure(sim, Local(sim, (Dungeon)9, 1), RejectCode.BadArgument, DungeonRejectReasons.INVALID_DUNGEON);
            sim.State.Wallet.DungeonTicket[(int)Fish] = 0;
            AssertRejectIsPure(sim, Local(sim, Fish, 1), RejectCode.CannotAfford, "NOT_ENOUGH_TICKET_FISH_COIN");
            sim.State.Wallet.DungeonTicket[(int)Fish] = 2;

            sim.State.Combat.Fight = new FightState { Phase = FightPhase.Fighting, EnemyIsBoss = true, EnemyHp = 1, EnemyMaxHp = 1, BossTicksLeft = 10 };
            AssertRejectIsPure(sim, Local(sim, Fish, 1), RejectCode.NotAvailable, DungeonRejectReasons.BOSS_ATTEMPT_IN_PROGRESS);
            sim.State.Combat.Fight.Phase = FightPhase.Spawning;   // a boss just killed, the next spawn pending: allowed
            Assert.That(sim.Apply(Local(sim, Fish, 1)).Ok, Is.True);

            AssertRejectIsPure(sim, Local(sim, Fish, 1), RejectCode.NotAvailable, DungeonRejectReasons.DUNGEON_RUN_IN_PROGRESS);
            AssertRejectIsPure(sim, new SweepDungeon(Fish), RejectCode.NotAvailable, DungeonRejectReasons.DUNGEON_RUN_IN_PROGRESS);
        }

        [Test]
        public void AC14_M0_IsLocked()
        {
            var sim = NewSim(db: TestContent.Db);
            AssertRejectIsPure(sim, new StartDungeon(Fish, 1, 1, "", false), RejectCode.Locked, DungeonRejectReasons.DUNGEON_LOCKED);
        }

        [Test]
        public void AC13_FailedRun_CostsNothing_ReturnsToStage()
        {
            var sim = NewSim();
            Assume.That(sim.PreviewDungeon(Fish, 1).Cleared, Is.False);
            var wallet = (sim.State.Wallet.FishCoin, Tickets(sim, Fish));
            Assert.That(sim.Apply(Local(sim, Fish, 1)).Ok, Is.True);
            Assert.That(Tickets(sim, Fish), Is.EqualTo(2), "checked at start, not debited");
            sim.DrainEvents();
            RunToEnd(sim);
            var ev = sim.DrainEvents();
            var failed = ev.OfType<DungeonFailed>().Single();
            Assert.That((failed.Dungeon, failed.Floor, failed.TicksUsed), Is.EqualTo((Fish, 1, Mvp.Constants.BossTimeTicks)));
            Assert.That(failed.RemainingFraction, Is.EqualTo(sim.LastDungeonRun.RemainingFraction));
            Assert.That(ev.OfType<CurrencyChanged>().Where(e => e.Reason == CurrencyReason.DUNGEON), Is.Empty);
            Assert.That((sim.State.Wallet.FishCoin, Tickets(sim, Fish)), Is.EqualTo(wallet));
            Assert.That((sim.State.Combat.Mode, sim.State.Dungeons.BestFloor[(int)Fish]), Is.EqualTo((CombatMode.Stage, 0)));
            Assert.That(sim.State.Dungeons.RecentRuns.Single(), Is.EqualTo(new DungeonRunRecord("", Fish, 1, false)));
            Assert.That(sim.State.Stats.DungeonClears, Is.EqualTo(0));
        }

        [Test]
        public void AC13_ClearedRun_DebitsOneTicket_GrantsTheFloor_SetsBestFloor_CountsQuests()
        {
            var sim = StrongSim();
            double coins = sim.State.Wallet.FishCoin;
            Assert.That(sim.Apply(Local(sim, Fish, 1)).Ok, Is.True);
            sim.DrainEvents();
            RunToEnd(sim);
            var ev = sim.DrainEvents();
            var dungeonCc = ev.OfType<CurrencyChanged>().Where(e => e.Reason == CurrencyReason.DUNGEON).Select(e => (e.Currency, e.Delta)).ToList();
            Assert.That(dungeonCc, Is.EqualTo(new[] { (Currency.TICKET_FISH_COIN, -1.0), (Currency.FISH_COIN, 50.0) }));
            Assert.That(ev.OfType<DungeonFloorFirstCleared>().Single(), Is.EqualTo(new DungeonFloorFirstCleared(Fish, 1) { Tick = sim.Tick }));
            var cleared = ev.OfType<DungeonCleared>().Single();
            Assert.That((cleared.Dungeon, cleared.Floor, cleared.RewardCurrency, cleared.RewardAmount), Is.EqualTo((Fish, 1, Currency.FISH_COIN, 50.0)));
            Assert.That(ev.Last(), Is.SameAs(cleared), "DungeonCleared closes the run (06 section 6.2)");
            Assert.That((Tickets(sim, Fish), sim.State.Wallet.FishCoin), Is.EqualTo((1, coins + 50)));
            Assert.That((sim.State.Dungeons.BestFloor[(int)Fish], sim.State.Stats.DungeonClears, sim.State.Dungeons.EntriesToday[(int)Fish]),
                        Is.EqualTo((1, 1L, 1)));
            Assert.That(sim.QuestProgressOf(Mvp.Quest("d_dungeon_fish")), Is.EqualTo(1));
            Assert.That(sim.QuestProgressOf(Mvp.Quest("d_dungeon_candy")), Is.EqualTo(0), "the daily row is filtered by dungeon");
            Assert.That(sim.QuestProgressOf(Mvp.Quest("w_dungeons")), Is.EqualTo(1), "the weekly row counts any dungeon");

            // Re-clearing a cleared floor pays again without a new best.
            Assert.That(sim.Apply(Local(sim, Fish, 1)).Ok, Is.True);
            sim.DrainEvents();
            RunToEnd(sim);
            ev = sim.DrainEvents();
            Assert.That(ev.OfType<DungeonFloorFirstCleared>(), Is.Empty);
            Assert.That((Tickets(sim, Fish), sim.State.Dungeons.BestFloor[(int)Fish]), Is.EqualTo((0, 1)));
        }

        [Test]
        public void ExpDungeonClear_GrantsHeroExp_ThroughTheOneExpPath()
        {
            var sim = StrongSim();
            int level = sim.State.Hero.Level;
            Assert.That(sim.Apply(Local(sim, Dungeon.EXP, 1)).Ok, Is.True);
            sim.DrainEvents();
            RunToEnd(sim);
            var cc = sim.DrainEvents().OfType<CurrencyChanged>().Single(e => e.Currency == Currency.HERO_EXP);
            Assert.That((cc.Delta, cc.Reason), Is.EqualTo((300 * Mvp.ExpPerKill.Eval(20), CurrencyReason.DUNGEON)));
            Assert.That(sim.State.Hero.Level, Is.GreaterThanOrEqualTo(level));
        }

        [Test]
        public void RunAcrossMidnight_DebitsFromTheNewDaysAllotment()
        {
            // A hero who needs more than one second (10 ticks) for floor 1, started one second before midnight.
            Simulation sim = null;
            foreach (int atk in new[] { 40, 60, 80, 100, 150, 200, 300 })
            {
                var s = NewSim(T0 + Day - 1);
                SetAtk(s, atk);
                var p = s.PreviewDungeon(Fish, 1);
                if (p.Cleared && p.Result.TicksUsed > 20) { sim = s; break; }
            }
            Assume.That(sim, Is.Not.Null, "an ATK level that clears floor 1 slowly");
            Assert.That(sim.Apply(new SweepDungeon(Fish)).Code, Is.EqualTo(RejectCode.NotAvailable), "nothing to sweep yet");
            sim.State.Wallet.DungeonTicket[(int)Fish] = 1;   // one of today's two already used
            int day = sim.State.Time.DayKey;
            Assert.That(sim.Apply(Local(sim, Fish, 1)).Ok, Is.True);
            RunToEnd(sim);
            Assert.That(sim.State.Time.DayKey, Is.EqualTo(day + 1), "the run crossed midnight and was not interrupted");
            Assert.That(sim.LastDungeonRun.Cleared, Is.True);
            Assert.That(Tickets(sim, Fish), Is.EqualTo(1), "topped up to 2 at midnight, then the clear debited 1");
        }

        // ------------------------------------------------------------------------------------------------ SweepDungeon (AC 16), floors past 40

        [Test]
        public void AC16_Sweep_GrantsExactlyTheBestFloor_RejectedAtBestFloorZero()
        {
            var sim = NewSim();
            AssertRejectIsPure(sim, new SweepDungeon(Fish), RejectCode.NotAvailable, DungeonRejectReasons.NOTHING_TO_SWEEP);
            sim.State.Dungeons.BestFloor[(int)Fish] = 12;
            sim.State.Combat.Fight = new FightState { Phase = FightPhase.Fighting, EnemyIsBoss = true, EnemyHp = 1, EnemyMaxHp = 1, BossTicksLeft = 10 };
            double coins = sim.State.Wallet.FishCoin;
            Assert.That(sim.Apply(new SweepDungeon(Fish)).Ok, Is.True, "a sweep has no fight and is allowed during a boss");
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<DungeonSwept>().Single(), Is.EqualTo(new DungeonSwept(Fish, 12, Currency.FISH_COIN, 600) { Tick = sim.Tick }));
            Assert.That(ev.OfType<CurrencyChanged>().Select(e => (e.Currency, e.Delta, e.Reason)),
                        Is.EqualTo(new[] { (Currency.TICKET_FISH_COIN, -1.0, CurrencyReason.DUNGEON), (Currency.FISH_COIN, 600.0, CurrencyReason.DUNGEON) }));
            Assert.That((sim.State.Wallet.FishCoin, Tickets(sim, Fish), sim.State.Dungeons.SweepsToday[(int)Fish]), Is.EqualTo((coins + 600, 1, 1)));
            Assert.That(sim.QuestProgressOf(Mvp.Quest("d_dungeon_fish")), Is.EqualTo(1), "a sweep counts as a clear");
            Assert.That(sim.State.Dungeons.RecentRuns, Is.Empty, "no run, no seed to correlate");

            Assert.That(sim.Apply(new SweepDungeon(Fish)).Ok, Is.True);
            AssertRejectIsPure(sim, new SweepDungeon(Fish), RejectCode.CannotAfford, "NOT_ENOUGH_TICKET_FISH_COIN");
        }

        [Test]
        public void Floor40_IsTheLast_StartingFloor41IsRejected_SweepPaysFloor40()
        {
            var sim = NewSim();
            sim.State.Dungeons.BestFloor[(int)Dungeon.ROCK_CANDY] = 40;
            AssertRejectIsPure(sim, Local(sim, Dungeon.ROCK_CANDY, 41), RejectCode.Locked, DungeonRejectReasons.DUNGEON_FLOOR_NOT_UNLOCKED);
            Assert.That(sim.Apply(new SweepDungeon(Dungeon.ROCK_CANDY)).Ok, Is.True);
            Assert.That(sim.DrainEvents().OfType<DungeonSwept>().Single().RewardAmount, Is.EqualTo(400));
        }
    }
}
