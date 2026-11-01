using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Gacha;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    public enum Reject
    {
        None = 0, NotEnoughGold, NotEnoughGems, EmptySlot, EnhanceMaxed, HeroNotOwned, HeroAlreadyEquipped,
        NoBossToAttempt, BossAlreadyPending, StageCap, ChestNotReady, BannerDisabled, InvalidCount,
    }

    public enum PaidWith { None, FreeDaily, FreeBanked, Gems }

    // Readonly structs rather than record structs: the sim compiles as C# 9 (Unity).

    public readonly struct CommandResult
    {
        public CommandResult(Reject reason) { Reason = reason; }
        public Reject Reason { get; }
        public bool Ok => Reason == Reject.None;
        public static readonly CommandResult Success = new CommandResult(Reject.None);
        public override string ToString() => Ok ? "Ok" : Reason.ToString();
    }

    public readonly struct EnhanceResult
    {
        public EnhanceResult(Reject reason, bool success, int level, double cost)
        {
            Reason = reason; Success = success; Level = level; Cost = cost;
        }

        public Reject Reason { get; }
        public bool Success { get; }
        public int Level { get; }
        public double Cost { get; }
        public bool Ok => Reason == Reject.None;
        public override string ToString() => Ok ? $"{(Success ? "Success" : "Fail")} -> +{Level} (cost {Cost})" : Reason.ToString();
    }

    public readonly struct PullOutcome
    {
        public PullOutcome(Reject reason, PullResult[] results, PaidWith paidWith, long gemsSpent)
        {
            Reason = reason; Results = results; PaidWith = paidWith; GemsSpent = gemsSpent;
        }

        public Reject Reason { get; }
        public PullResult[] Results { get; }
        public PaidWith PaidWith { get; }
        public long GemsSpent { get; }
        public bool Ok => Reason == Reject.None;
    }

    /// <summary>Result of the closed-form offline credit (specs/03 section 11, specs/06 section 9).</summary>
    public readonly struct OfflineResult
    {
        public OfflineResult(long elapsedSec, long creditedSec, bool capped, double gold, int stage, double killTimeSec)
        {
            ElapsedSec = elapsedSec; CreditedSec = creditedSec; Capped = capped; Gold = gold; Stage = stage; KillTimeSec = killTimeSec;
        }

        public long ElapsedSec { get; }
        public long CreditedSec { get; }
        public bool Capped { get; }
        public double Gold { get; }
        public int Stage { get; }
        public double KillTimeSec { get; }
    }

    public sealed class InvariantViolationException : Exception
    {
        public InvariantViolationException(string message) : base(message) { }
    }

    /// <summary>
    /// Commands (specs/03 section 13). They validate, mutate, publish and return; they never throw for a rule
    /// violation (returned Reject), only for malformed input (bad slot index, unknown hero id).
    /// Do not call from inside an event handler during Tick().
    /// </summary>
    public sealed partial class Simulation
    {
        // ------------------------------------------------------------ stat levels (specs/04 section 4)

        public CommandResult BuyStatLevel() => BuyStatLevels(1) ? CommandResult.Success : new CommandResult(Reject.NotEnoughGold);

        /// <summary>All-or-nothing purchase of m levels. False (and no change) if unaffordable or m &lt;= 0.</summary>
        public bool BuyStatLevels(int m)
        {
            Log(CommandKind.BuyStatLevels, m);
            if (m <= 0) return false;
            var p = State.Progression;
            double cost = Query.BulkStatCost(m);
            if (State.Currencies.Gold < cost) return false;
            State.Currencies.Gold -= cost;
            p.StatLevel += m;
            MarkDpsDirty();
            if (Wants(EventKinds.StatLeveled)) _sink.Publish(new StatLeveled(State.Tick, p.StatLevel, m, cost));
            return true;
        }

        /// <summary>The Max button / greedy bot: buys as many levels as affordable. Returns the count bought.</summary>
        public int BuyStatLevelsMax()
        {
            int m = Query.MaxAffordableStatLevels();
            return m > 0 && BuyStatLevels(m) ? m : 0;
        }

        // ------------------------------------------------------------ gear (specs/04 section 5.6)

        public EnhanceResult Enhance(int slot)
        {
            CheckGearSlot(slot);
            Log(CommandKind.Enhance, slot);
            var piece = State.Gear.Slots[slot];
            if (piece == null) return new EnhanceResult(Reject.EmptySlot, false, 0, 0);
            if (piece.Enhance >= _k.EnhMax) return new EnhanceResult(Reject.EnhanceMaxed, false, piece.Enhance, 0);
            double cost = Query.EnhanceCost;
            if (State.Currencies.Gold < cost) return new EnhanceResult(Reject.NotEnoughGold, false, piece.Enhance, cost);

            State.Currencies.Gold -= cost;
            State.Lifetime.EnhanceAttempts += 1;
            bool success = _enhance.NextDouble() < _k.EnhSuccess(piece.Enhance);
            int level = piece.Enhance;
            if (success)
            {
                level += 1;
                State.Gear.Slots[slot] = piece with { Enhance = level };
                State.Lifetime.EnhanceSuccesses += 1;
                MarkDpsDirty();
            }
            if (Wants(EventKinds.EnhanceAttempted)) _sink.Publish(new EnhanceAttempted(State.Tick, slot, success, level, cost));
            return new EnhanceResult(Reject.None, success, level, cost);
        }

        void CheckGearSlot(int slot)
        {
            if (slot < 0 || slot >= Content.GearSlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        }

        // ------------------------------------------------------------ gacha (specs/01, specs/04 section 7.5)

        /// <summary>One pull: daily free credit, else banked credit, else PULL_COST_GEMS.</summary>
        public PullOutcome Pull()
        {
            Log(CommandKind.Pull);
            return PullBatch(1);
        }

        /// <summary>A guaranteed batch (GuaranteeBatchSize, 10): banked credits if enough, else 10 * PULL_COST_GEMS.</summary>
        public PullOutcome Pull10()
        {
            Log(CommandKind.Pull10);
            return PullBatch(_banner.GuaranteeBatchSize);
        }

        PullOutcome PullBatch(int count)
        {
            EnsureDayCore(Query.NowServerSec);
            if (!_banner.Enabled) return new PullOutcome(Reject.BannerDisabled, Array.Empty<PullResult>(), PaidWith.None, 0);
            var cur = State.Currencies;
            PaidWith with;
            long gems = 0;
            if (count == 1)
            {
                if (cur.FreePullsDaily > 0) { cur.FreePullsDaily -= 1; with = PaidWith.FreeDaily; }
                else if (cur.FreePullsBanked > 0) { cur.FreePullsBanked -= 1; with = PaidWith.FreeBanked; }
                else if (cur.Gems >= _banner.PullCostGems) { gems = _banner.PullCostGems; with = PaidWith.Gems; }
                else return new PullOutcome(Reject.NotEnoughGems, Array.Empty<PullResult>(), PaidWith.None, 0);
            }
            else
            {
                if (cur.FreePullsBanked >= count) { cur.FreePullsBanked -= count; with = PaidWith.FreeBanked; }
                else if (cur.Gems >= (long)_banner.PullCostGems * count) { gems = (long)_banner.PullCostGems * count; with = PaidWith.Gems; }
                else return new PullOutcome(Reject.NotEnoughGems, Array.Empty<PullResult>(), PaidWith.None, 0);
            }
            cur.Gems -= gems;

            var results = _gacha.Pull(Content, _banner, State.HeroBanner, State.Heroes, cur, _gachaHero, count);
            if (Wants(EventKinds.HeroPulled))
                foreach (var r in results) _sink.Publish(new HeroPulled(State.Tick, r, with != PaidWith.Gems));
            QuestProgress(QuestType.DoPulls, results.Length);
            MarkDpsDirty();   // copy gain on an equipped hero changes HeroPct
            if (State.Heroes.AutoEquipBest) AutoEquipInternal();
            return new PullOutcome(Reject.None, results, with, gems);
        }

        // ------------------------------------------------------------ party (specs/03 section 9)

        /// <summary>Put an owned hero in the party. Turns off AutoEquipBest.</summary>
        public CommandResult EquipHero(int slot, string heroId)
        {
            CheckHeroSlot(slot);
            if (heroId == null || !Content.Heroes.ContainsKey(heroId)) throw new ArgumentOutOfRangeException(nameof(heroId));
            if (Logging) Log(CommandKind.EquipHero, slot, HeroIndexOf(heroId));
            if (!State.Heroes.Copies.TryGetValue(heroId, out int c) || c <= 0) return new CommandResult(Reject.HeroNotOwned);
            var eq = State.Heroes.Equipped;
            for (int i = 0; i < eq.Length; i++)
                if (i != slot && eq[i] == heroId) return new CommandResult(Reject.HeroAlreadyEquipped);
            State.Heroes.AutoEquipBest = false;
            if (eq[slot] == heroId) return CommandResult.Success;
            eq[slot] = heroId;
            MarkDpsDirty();
            if (Wants(EventKinds.HeroEquipped)) _sink.Publish(new HeroEquipped(State.Tick, slot, heroId));
            return CommandResult.Success;
        }

        public CommandResult UnequipHero(int slot)
        {
            CheckHeroSlot(slot);
            Log(CommandKind.UnequipHero, slot);
            State.Heroes.AutoEquipBest = false;
            var eq = State.Heroes.Equipped;
            if (eq[slot] == null) return CommandResult.Success;
            eq[slot] = null;
            MarkDpsDirty();
            if (Wants(EventKinds.HeroEquipped)) _sink.Publish(new HeroEquipped(State.Tick, slot, null));
            return CommandResult.Success;
        }

        /// <summary>Fill the party with the HERO_SLOTS best owned heroes (HeroPct desc, id ordinal). Turns AutoEquipBest on.</summary>
        public int AutoEquipBestHeroes()
        {
            Log(CommandKind.AutoEquipBest, 1);
            State.Heroes.AutoEquipBest = true;
            return AutoEquipInternal();
        }

        public void SetAutoEquipHeroes(bool on)
        {
            Log(CommandKind.AutoEquipBest, on ? 1 : 0);
            State.Heroes.AutoEquipBest = on;
            if (on) AutoEquipInternal();
        }

        /// <summary>Re-pick the party without recording a command (callers already recorded theirs).</summary>
        internal int AutoEquipUnlogged() => AutoEquipInternal();

        int AutoEquipInternal()
        {
            var best = State.Heroes.Copies.Where(kv => kv.Value > 0 && Content.Heroes.ContainsKey(kv.Key))
                .Select(kv => kv.Key)
                .OrderByDescending(id => HeroPct(id))
                .ThenBy(id => id, StringComparer.Ordinal)
                .Take(_k.HeroSlots)
                .ToList();
            var eq = State.Heroes.Equipped;
            int changed = 0;
            for (int i = 0; i < eq.Length; i++)
            {
                string want = i < best.Count ? best[i] : null;
                if (eq[i] == want) continue;
                eq[i] = want;
                changed++;
                if (Wants(EventKinds.HeroEquipped)) _sink.Publish(new HeroEquipped(State.Tick, i, want));
            }
            if (changed > 0) MarkDpsDirty();
            return changed;
        }

        void CheckHeroSlot(int slot)
        {
            if (slot < 0 || slot >= _k.HeroSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        }

        // ------------------------------------------------------------ boss retry (specs/03 section 13)

        /// <summary>Retry a walled boss: the current mob despawns (no gold/drop/credit), the next spawn is the boss.</summary>
        public CommandResult AttemptBoss()
        {
            Log(CommandKind.AttemptBoss);
            var stg = State.Stage;
            if (stg.Boss == BossPhase.Pending) return new CommandResult(Reject.BossAlreadyPending);
            if (!_k.IsBossStage(stg.Stage) || stg.Boss != BossPhase.Walled) return new CommandResult(Reject.NoBossToAttempt);
            stg.Boss = BossPhase.Pending;
            var cb = State.Combat;
            cb.Phase = CombatPhase.Spawning;
            cb.SpawnTimer = _k.SpawnDelayTicks;
            cb.EnemyIsBoss = false;
            cb.EnemyHp = 0;
            cb.BossTimer = 0;
            return CommandResult.Success;
        }

        // ------------------------------------------------------------ gems: idle chest, tutorial (specs/04 sections 7.4, 8)

        public CommandResult ClaimIdleChest()
        {
            Log(CommandKind.ClaimChest);
            long now = Query.NowServerSec;
            EnsureDayCore(now);
            if (now < Query.ChestReadyAtServerSec) return new CommandResult(Reject.ChestNotReady);
            AddGems(_k.IdleChestGems);
            State.Progression.ChestTimerStart = now;
            if (Wants(EventKinds.IdleChestClaimed)) _sink.Publish(new IdleChestClaimed(State.Tick, _k.IdleChestGems));
            return CommandResult.Success;
        }

        /// <summary>
        /// One-time profile_created grants (1000 gems, 10 banked free pulls in P0 content). Guarded by
        /// Progression.TutorialGranted. Returns false if already granted. Bots that mirror the Python model skip this.
        /// </summary>
        public bool ApplyTutorialGrants()
        {
            Log(CommandKind.TutorialGrants);
            var p = State.Progression;
            if (p.TutorialGranted) return false;
            foreach (var g in Content.TutorialGrants)
            {
                if (g.Trigger != GrantTrigger.ProfileCreated) continue;
                switch (g.GrantType)
                {
                    case GrantType.Gems: AddGems(g.Amount); break;
                    case GrantType.FreePulls: State.Currencies.FreePullsBanked += g.Amount; break;
                    case GrantType.Gold: AddGold(g.Amount); break;
                    case GrantType.Hero:
                        State.Heroes.Copies.TryGetValue(g.GrantRef, out int c);
                        State.Heroes.Copies[g.GrantRef] = c + g.Amount;
                        MarkDpsDirty();
                        if (State.Heroes.AutoEquipBest) AutoEquipInternal();
                        break;
                }
            }
            p.TutorialGranted = true;
            return true;
        }

        // ------------------------------------------------------------ invariants (specs/03 section 15, subset)

        public void Validate()
        {
            var s = State;
            void Check(bool ok, string what) { if (!ok) throw new InvariantViolationException(what); }
            Check(s.Currencies.Gold >= 0 && s.Currencies.Gems >= 0 && s.Currencies.Shards >= 0, "currencies >= 0");
            Check(s.Progression.StatLevel >= 0, "stat level >= 0");
            Check(s.HeroBanner.PityCounter >= 0 && s.HeroBanner.PityCounter < _banner.PityHard, "pity counter range");
            Check(s.Stage.Stage >= 1 && s.Stage.Stage <= _k.StageCap, "stage range");
            Check(s.Stage.KillsInStage >= 0 && s.Stage.KillsInStage < _k.KillsPerStage, "kills in stage range");
            Check(s.Stage.Boss == BossPhase.None || (_k.IsBossStage(s.Stage.Stage) && s.Stage.KillsInStage == _k.KillsPerStage - 1),
                  "boss phase only on boss stage at KILLS_PER_STAGE-1");
            int expectedAdv = Content.ClassAdvances.Count(c => c.Stage <= s.Stage.Stage);
            Check(s.Progression.ClassAdvances == expectedAdv, "class advances match stage");
            Check(s.Gear.Slots.Length == Content.GearSlotCount, "gear slot count");
            foreach (var p in s.Gear.Slots)
                if (p != null) Check(p.Enhance >= 0 && p.Enhance <= _k.EnhMax && p.Tier % _k.TierWidth == 0, "gear piece valid");
            var eq = s.Heroes.Equipped.Where(x => x != null).ToList();
            Check(eq.Distinct().Count() == eq.Count, "equipped heroes distinct");
            Check(eq.All(id => s.Heroes.Copies.TryGetValue(id, out int c) && c > 0), "equipped heroes owned");
            Check(_gearDrop.Counter == (ulong)s.Lifetime.Kills, "gear_drop counter == kills");
            Check(_gearRarity.Counter == (ulong)(2 * s.Lifetime.GearDrops), "gear_rarity counter == 2 * drops");
            Check(_enhance.Counter == (ulong)s.Lifetime.EnhanceAttempts, "enhance counter == attempts");
            Check(_gachaHero.Counter == (ulong)(3 * s.HeroBanner.TotalPulls), "gacha_hero counter == 3 * pulls");
            Check(s.Rng.Streams[Rng.StreamNames.Combat].Counter == 0, "combat counter == 0");
            Check((s.Lifetime.FirstWallStage == 0) == (s.Lifetime.BossFails == 0), "first wall stage iff boss fails");
            Check(s.Clock.TickAtLastSync <= s.Tick, "clock anchor <= tick");
            Check(!double.IsNaN(Query.Dps) && !double.IsInfinity(Query.Dps) && Query.Dps > 0, "dps finite and > 0");
        }
    }
}
