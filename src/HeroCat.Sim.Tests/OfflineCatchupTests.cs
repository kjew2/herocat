using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Save;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Content.Dto;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Fish;
using HeroCat.Sim.Power;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>06 section 7 two-phase Offline Catch-up (issue #108; 00 contract 7, decision 53). AC numbers are 06's.</summary>
    public class OfflineCatchupTests
    {
        static ContentDb Db => TestContent.Db;
        static long T0 => TestContent.T0;
        const long Hour = 3600;

        static Simulation Sim()
        {
            var sim = TestContent.NewSim();
            sim.DrainEvents();
            return sim;
        }

        /// <summary>The shipped content built at MVP: it has the res_offline_cap row (+2 h per level, 6 levels: 03 section 6).</summary>
        static readonly Lazy<ContentDb> WithResearch = new Lazy<ContentDb>(() => TestContent.Mvp);

        static double Expected(Simulation sim, long creditedSec, bool gold)
        {
            int stage = sim.State.Combat.Stage;
            var q = sim.Query;
            double perKill = gold ? q.GoldPerKill(stage) * q.GoldMult : q.ExpPerKill(stage) * q.ExpMult;
            return creditedSec * Db.Constants.OfflineRate * perKill / q.KillTimeSeconds(stage);
        }

        // ------------------------------------------------------------------------------------------------ AC 9

        [Test]
        public void Gap45s_NoRecord_NoCurrency_ButTheTankRefills()   // AC 9 first part, 05 section 7
        {
            var sim = Sim();
            var s = sim.State;
            s.Fish.TankCount = 0;
            s.Fish.TankRefillTick = 0;
            var probe = StateCopy.DeepClone(s);
            var (added, after) = FishBoard.CatchUpTank(probe, Db, 45L * Db.Constants.TickHz);
            double gold = s.Wallet.Gold, exp = s.Hero.Exp;

            Assert.That(sim.Apply(new ComputeOfflineCatchup(T0 + 45)).Ok, Is.True);
            Assert.That(s.Offline.Pending, Is.Null);
            Assert.That((s.Wallet.Gold, s.Hero.Exp), Is.EqualTo((gold, exp)));
            Assert.That((s.Fish.TankCount, s.Fish.TankRefillTick), Is.EqualTo((added, after)), "CatchUpTank over 450 ticks, applied directly");
            Assert.That(added, Is.GreaterThan(0));
            Assert.That(s.Time.LastTickServerTime, Is.EqualTo(T0 + 45));
            Assert.That(sim.DrainEvents().OfType<OfflineRewardComputed>(), Is.Empty, "no modal");
        }

        [Test]
        public void Amount_TheWorkedNumbers_7200sAt3point5sAnd161point5()   // AC 9 second part (06 section 7.2 fixture)
        {
            double gold = Catchup.Amount(7200, 0.5, 161.5, 3.5);
            Assert.That(gold, Is.EqualTo(166114.2857142857).Within(1e-6).Percent);
            Assert.That(Math.Floor(gold), Is.EqualTo(166114));
            Assert.That(Catchup.Amount(7200, 0.5, 161.5, 0), Is.Zero, "no kill rate, nothing earned");
            Assert.That(Catchup.Amount(7200, 0.5, 161.5, double.PositiveInfinity), Is.Zero);
        }

        [Test]
        public void TwoHours_GoldAndExpAtOfflineRateTimesTheKillRate()   // AC 9 on the live content
        {
            var sim = Sim();
            sim.Apply(new ComputeOfflineCatchup(T0 + 2 * Hour));
            var p = sim.State.Offline.Pending;
            Assert.That((p.ElapsedSec, p.CreditedSec, p.Capped), Is.EqualTo((2 * Hour, 2 * Hour, false)));
            Assert.That(p.Gold, Is.EqualTo(Expected(sim, 2 * Hour, gold: true)).Within(1e-12).Percent);
            Assert.That(p.Exp, Is.EqualTo(Expected(sim, 2 * Hour, gold: false)).Within(1e-12).Percent);
            Assert.That(p.Gold, Is.GreaterThan(0));
            Assert.That(p.Stage, Is.EqualTo(sim.State.Combat.Stage));
            Assert.That(p.KillTimeSec, Is.EqualTo(sim.Query.KillTimeSeconds(sim.State.Combat.Stage)));
            Assert.That(p.ComputedServerTime, Is.EqualTo(T0 + 2 * Hour));
            Assert.That(sim.DrainEvents().OfType<OfflineRewardComputed>().Single().Pending, Is.EqualTo(p));
        }

        [Test]
        public void GoldGainTraining_ScalesOfflineGold()   // 06 section 7.1: GoldPerKill includes GOLD_GAIN
        {
            var a = Sim();
            var b = Sim();
            b.State.Training.Level[(int)Stat.GOLD_GAIN] = 10;
            b.Query.MarkDirty();
            a.Apply(new ComputeOfflineCatchup(T0 + Hour));
            b.Apply(new ComputeOfflineCatchup(T0 + Hour));
            Assert.That(b.State.Offline.Pending.Gold / a.State.Offline.Pending.Gold, Is.EqualTo(b.Query.GoldMult).Within(1e-12).Percent);
            Assert.That(b.Query.GoldMult, Is.GreaterThan(1.0));
        }

        [Test]
        public void ThirtyHours_CapsAt24h()   // AC 9 third part
        {
            var sim = Sim();
            sim.Apply(new ComputeOfflineCatchup(T0 + 30 * Hour));
            var p = sim.State.Offline.Pending;
            Assert.That((p.ElapsedSec, p.CreditedSec, p.Capped), Is.EqualTo((30 * Hour, 86400L, true)));
            Assert.That(Catchup.CapSeconds(Db, sim.State), Is.EqualTo((long)(Db.Constants.OfflineCapH * Hour)).And.EqualTo(86400));
        }

        [Test]
        public void KillTimeSeconds_WholeAttacksAtTheRatedPerAttackDamage_PlusTheSpawnDelay()   // 06 section 7.2's shape
        {
            var sim = Sim();
            var k = Db.Constants;
            var q = sim.Query;
            foreach (int stage in new[] { 1, 50, 200 })
            {
                double attackSec = (double)k.AttackIntervalTicks / k.TickHz / q.SpeedFactor;
                double hits = Math.Ceiling(q.EnemyHp(stage) / (q.RatedDps * attackSec));
                Assert.That(q.KillTimeSeconds(stage), Is.EqualTo(Math.Max(1, hits) * attackSec + (double)k.SpawnDelayTicks / k.TickHz), $"stage {stage}");
            }
            Assert.That(q.KillTimeSeconds(200), Is.GreaterThan(q.KillTimeSeconds(1)));
        }

        // ------------------------------------------------------------------------------------------------ AC 10, 01 AC 9

        [Test]
        public void ComputeSaveReload_SamePending_ClaimGrantsOnce()   // AC 10, 01 AC 9
        {
            var sim = Sim();
            sim.Apply(new ComputeOfflineCatchup(T0 + 3 * Hour));
            var p = sim.State.Offline.Pending;

            // crash before the claim: the save holds the pending record and the moved anchor
            var (back, _, _) = SaveSystem.DecodeFull(SaveSerializer.Encode(sim.State), Db);
            Assert.That(back.Offline.Pending, Is.EqualTo(p));
            var relaunched = new Simulation(back, Db);
            relaunched.Apply(new ApplyServerTime(T0 + 3 * Hour));
            relaunched.Apply(new ComputeOfflineCatchup(T0 + 3 * Hour));   // the boot catch-up after the relaunch
            Assert.That(relaunched.State.Offline.Pending, Is.EqualTo(p), "the window is not computed twice");

            relaunched.DrainEvents();
            double gold = relaunched.State.Wallet.Gold;
            Assert.That(relaunched.Apply(new ClaimOfflineReward()).Ok, Is.True);
            Assert.That(relaunched.State.Wallet.Gold, Is.EqualTo(gold + p.Gold));
            var r = relaunched.Apply(new ClaimOfflineReward());
            Assert.That((r.Code, r.Reason), Is.EqualTo((RejectCode.NotAvailable, OfflineRejectReasons.NO_PENDING_OFFLINE_REWARD)));
            Assert.That(relaunched.State.Wallet.Gold, Is.EqualTo(gold + p.Gold), "granted once");
        }

        [Test]
        public void SecondGapWhilePending_SumsIntoTheRecord_KeepingTheLatestStage()   // AC 10, issue #21
        {
            var sim = Sim();
            sim.Apply(new ComputeOfflineCatchup(T0 + 2 * Hour));
            var first = sim.State.Offline.Pending;
            sim.State.Combat.Stage = 40;   // a later stage for the second gap's display
            sim.Query.MarkDirty();
            sim.Apply(new ComputeOfflineCatchup(T0 + 4 * Hour));
            var p = sim.State.Offline.Pending;
            Assert.That((p.ElapsedSec, p.CreditedSec, p.Capped), Is.EqualTo((4 * Hour, 4 * Hour, false)));
            Assert.That(p.Gold, Is.EqualTo(first.Gold + Expected(sim, 2 * Hour, gold: true)).Within(1e-12).Percent);
            Assert.That(p.Exp, Is.EqualTo(first.Exp + Expected(sim, 2 * Hour, gold: false)).Within(1e-12).Percent);
            Assert.That(p.Stage, Is.EqualTo(40));
            Assert.That(p.KillTimeSec, Is.EqualTo(sim.Query.KillTimeSeconds(40)));
            Assert.That(sim.DrainEvents().OfType<OfflineRewardComputed>().Count(), Is.EqualTo(2));
        }

        [Test]
        public void TwoStacked20hGaps_CapTheSum()   // AC 27 third part
        {
            var sim = Sim();
            sim.Apply(new ComputeOfflineCatchup(T0 + 20 * Hour));
            sim.State.Combat.Stage = 7;
            sim.Apply(new ComputeOfflineCatchup(T0 + 40 * Hour));
            var p = sim.State.Offline.Pending;
            Assert.That((p.ElapsedSec, p.CreditedSec, p.Capped, p.Stage), Is.EqualTo((144000L, 86400L, true, 7)));
        }

        // ------------------------------------------------------------------------------------------------ AC 11, 12

        [Test]
        public void BackwardsClock_EmitsClockWentBackwards_GrantsNothing_KeepsTheAnchor()   // AC 11
        {
            var sim = Sim();
            sim.Apply(new ComputeOfflineCatchup(T0 + 2 * Hour));
            sim.DrainEvents();
            string hash = sim.StateHash();
            Assert.That(sim.Apply(new ComputeOfflineCatchup(T0 + Hour)).Ok, Is.True);
            Assert.That(sim.DrainEvents().OfType<ClockWentBackwards>().Single().DeltaSeconds, Is.EqualTo(Hour));
            Assert.That(sim.StateHash(), Is.EqualTo(hash));
            Assert.That(sim.State.Time.LastTickServerTime, Is.EqualTo(T0 + 2 * Hour));
        }

        [Test]
        public void Compute_TouchesOnlyOfflinePendingAndTheAnchor()   // AC 12, 02 AC 11
        {
            var sim = Sim();
            sim.Advance(600);   // some combat, so streams, stats and the fight have moved
            sim.Apply(new ApplyServerTime(sim.VirtualNowServerSeconds));
            var before = StateCopy.DeepClone(sim.State);
            sim.Apply(new ComputeOfflineCatchup(sim.VirtualNowServerSeconds + 5 * Hour));
            Assert.That(sim.State.Offline.Pending, Is.Not.Null);

            var after = StateCopy.DeepClone(sim.State);
            after.Offline.Pending = before.Offline.Pending;
            after.Time.LastTickServerTime = before.Time.LastTickServerTime;
            Assert.That(Replay.StateHasher.Hash(after), Is.EqualTo(Replay.StateHasher.Hash(before)), "no stream, quest, stage, tank or wallet change");
            Assert.That(after.Rng.Values.Select(r => r.Counter), Is.EqualTo(before.Rng.Values.Select(r => r.Counter)));
        }

        // ------------------------------------------------------------------------------------------------ claim, AC 27

        [Test]
        public void Claim_CreditsGoldAndExpWithReasonOffline_AddsTheTankBoundedByTankCap()   // AC 27, 05 AC 11
        {
            var sim = Sim();
            var s = sim.State;
            s.Fish.TankCount = 0;
            s.Fish.TankRefillTick = 0;
            sim.Apply(new ComputeOfflineCatchup(T0 + 2 * Hour));
            var p = s.Offline.Pending;
            Assert.That(p.TankFill, Is.EqualTo(FishBoard.TankCap(s, Db)), "a 2 h gap fills the tank");
            Assert.That(s.Fish.TankCount, Is.Zero, "the tank is unchanged until the claim");
            s.Fish.TankCount = 3;   // the player had released some; the claim still clamps at TankCap
            var cells = (int[])s.Fish.Cells.Clone();
            double gold = s.Wallet.Gold;
            long fishCoin = (long)s.Wallet.FishCoin;
            sim.DrainEvents();

            Assert.That(sim.Apply(new ClaimOfflineReward()).Ok, Is.True);
            Assert.That(s.Fish.TankCount, Is.EqualTo(FishBoard.TankCap(s, Db)));
            Assert.That(s.Fish.TankRefillTick, Is.Zero, "a full tank holds no refill progress");
            Assert.That(s.Wallet.Gold, Is.EqualTo(gold + p.Gold));
            Assert.That(s.Fish.Cells, Is.EqualTo(cells));
            Assert.That((long)s.Wallet.FishCoin, Is.EqualTo(fishCoin));
            Assert.That(s.Offline.Pending, Is.Null);
            Assert.That(s.Offline.LifetimeOfflineSecCredited, Is.EqualTo(2 * Hour));

            var ev = sim.DrainEvents();
            var cc = ev.OfType<CurrencyChanged>().ToList();
            Assert.That(cc.Select(c => (c.Currency, c.Reason)), Is.EquivalentTo(new[] { (Currency.GOLD, CurrencyReason.OFFLINE), (Currency.HERO_EXP, CurrencyReason.OFFLINE) }));
            Assert.That(ev.OfType<OfflineRewardClaimed>().Single().Reward, Is.EqualTo(p));
        }

        [Test]
        public void Claim_WithNothingPending_RejectsAndChangesNothing()   // 06 section 1.3, AC 29
        {
            var sim = Sim();
            string hash = sim.StateHash();
            var r = sim.Apply(new ClaimOfflineReward());
            Assert.That((r.Code, r.Reason), Is.EqualTo((RejectCode.NotAvailable, OfflineRejectReasons.NO_PENDING_OFFLINE_REWARD)));
            Assert.That(sim.StateHash(), Is.EqualTo(hash));
            Assert.That(sim.DrainEvents(), Is.Empty);
        }

        [Test]
        public void ResearchOfflineCapAtLevel6_40hGapCredits36h()   // AC 27 second part, 00 decision 12
        {
            var db = WithResearch.Value;
            var s = TestContent.NewState();
            s.Research.Level["res_offline_cap"] = 6;
            var sim = new Simulation(s, db);
            sim.Apply(new ApplyServerTime(T0));
            Assert.That(sim.Query.OfflineCapH, Is.EqualTo(36.0));
            Assert.That(sim.Query.ResearchLevel(ResearchEffect.OFFLINE_CAP), Is.EqualTo(6));
            sim.Apply(new ComputeOfflineCatchup(T0 + 40 * Hour));
            Assert.That((s.Offline.Pending.CreditedSec, s.Offline.Pending.Capped), Is.EqualTo((129600L, true)));

            s.Research.Level["res_offline_cap"] = 99;   // above max_level: clamped
            Assert.That(PowerFormula.OfflineCapH(db, s), Is.EqualTo(36.0));
            Assert.That(PowerFormula.OfflineCapH(Db, s), Is.EqualTo(Db.Constants.OfflineCapH), "no research row in M0 content: the base cap");
        }

        // ------------------------------------------------------------------------------------------------ the anchor

        [Test]
        public void TickedTime_IsNeverCountedAsOffline()   // 00 contract 5: the anchor follows the ticks, not a save
        {
            var sim = Sim();
            int hz = Db.Constants.TickHz;
            sim.Advance(90 * hz);   // a 90 s session
            Assert.That(sim.State.Time.LastTickServerTime, Is.EqualTo(T0 + 90));
            sim.Apply(new ComputeOfflineCatchup(T0 + 90 + 30));   // back after 30 s
            Assert.That(sim.State.Offline.Pending, Is.Null, "30 s offline is below OFFLINE_MIN_S; the 90 ticked seconds do not count");
        }
    }
}
