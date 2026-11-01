using System;
using System.Collections.Generic;
using HeroCat.Runtime.Summons;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Fish;
using HeroCat.Sim.Power;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;

namespace HeroCat.Bots.Policies
{
    /// <summary>
    /// <c>greedy-f2p</c>: the reference player for every balance number (03 section 8.3, 10 section 3.4), for the M0
    /// systems. Plays two 60-minute sessions a day, taps nothing (03 section 2.5: the targets are idle-combat targets) and
    /// spends everything as soon as it can on whatever gives the most ln(DPS) per unit of currency. Step order, cross-
    /// referenced to 03 section 8.3 (tools/power_curve_model.py follows the same order):
    ///
    ///   OnSessionStart: offline reward (step 7: ClaimOfflineReward, as closing SCR_OFFLINE_REWARD does), tickets then
    ///                   diamonds (step 2), Fish Board (step 5), gold (step 1), boss (step 6).
    ///   Decide (every active minute): gold (step 1), Fish Board (step 5), boss (step 6). Steps 3-4 (Rock Candy, Fish
    ///                   Coins) are MVP systems (Awakening, limit breaks, Research): stubbed until the content tier has them
    ///                   (TODO(#214)).
    ///   OnSessionEnd:   diamonds (step 2: what first clears paid this session), gold (step 1), release the tank (step 5).
    ///
    /// Commands only (00 contract 5): the policy reads <see cref="BotContext.Query"/> and the state, and never writes it.
    /// </summary>
    public class GreedyF2PBot : IBotPolicy
    {
        /// <summary>05 section 8 / issue #19: the attentive player's merge cap.</summary>
        public const int MergesPerMinuteCap = 60;

        /// <summary>10 section 3.4: singles only when a 10-pull is unaffordable and pity is within this many pulls of PITY_HARD.</summary>
        public const int SinglePullPityWindow = 10;

        /// <summary>03 section 8.3 step 1: GOLD_GAIN and EXP_GAIN are valued at half of what their income increase buys.</summary>
        public const double IncomeStatWeight = 0.5;

        static readonly Stat[] TrainOrder = { Stat.ATK, Stat.CRIT_RATE, Stat.CRIT_DMG, Stat.ATK_SPD, Stat.GOLD_GAIN, Stat.EXP_GAIN };
        static readonly Banner[] BannerOrder = { Banner.WEAPON, Banner.SKILL, Banner.COMPANION };

        public virtual string Name => "greedy-f2p";

        public virtual void OnSessionStart(BotContext ctx)
        {
            ClaimOfflineReward(ctx);
            SpendTickets(ctx);
            SpendDiamonds(ctx);
            PlayFishBoard(ctx);
            SpendGold(ctx);
            RetryBoss(ctx);
        }

        public virtual void Decide(BotContext ctx)
        {
            SpendGold(ctx);
            PlayFishBoard(ctx);
            RetryBoss(ctx);
        }

        public virtual void OnSessionEnd(BotContext ctx)
        {
            SpendTickets(ctx);
            SpendDiamonds(ctx);
            SpendGold(ctx);
            if (ctx.Sim.FishCanRelease) ctx.Apply(new ReleaseMax());
        }

        // ============================================================================================ step 7: offline reward

        /// <summary>
        /// Claims the reward the session's catch-up stored (06 section 7.1, 03 section 8.3 step 7). The runner holds ticks
        /// while one is pending (01 section 8.7), so this comes before anything else the session does.
        /// </summary>
        public static void ClaimOfflineReward(BotContext ctx)
        {
            if (ctx.Sim.State.Offline.Pending != null) ctx.Apply(new ClaimOfflineReward());
        }

        // ============================================================================================ step 1: gold -> Training

        /// <summary>
        /// Buys one Training level at a time, always the affordable stat with the highest marginal ln(DPS) per gold
        /// (03 section 8.3 step 1), until nothing is affordable or nothing adds value.
        /// </summary>
        public static void SpendGold(BotContext ctx)
        {
            while (true)
            {
                var best = BestTraining(ctx, out _);
                if (best == null) return;
                if (!ctx.Apply(new TrainStat(best.Value, 1)).Ok) return;
            }
        }

        /// <summary>The affordable stat with the best value per gold, or null. <paramref name="value"/> is its ln(DPS) per gold.</summary>
        public static Stat? BestTraining(BotContext ctx, out double value)
        {
            Stat? best = null;
            value = 0;
            var q = ctx.Query;
            double gold = ctx.State.Wallet.Gold;
            foreach (var stat in TrainOrder)
            {
                var def = ctx.Content.Stat(stat);
                if (!def.Trainable || !ctx.Content.TrainingCurves.ContainsKey(stat)) continue;
                int level = q.TrainLevel(stat);
                if (def.Cap.HasValue && level >= def.Cap.Value) continue;
                double cost = q.TrainCost(stat);
                if (!(cost <= gold)) continue;
                double v = MarginalLnDps(ctx.Content, ctx.State, stat) / cost;
                if (v > value) { best = stat; value = v; }   // strict: ties keep the earlier stat in TrainOrder (ATK first)
            }
            return best;
        }

        /// <summary>
        /// ln(DPS) gained by one more level of <paramref name="stat"/> (03 section 5.1 closed forms). GOLD_GAIN and EXP_GAIN
        /// are valued at <see cref="IncomeStatWeight"/> of the ATK levels (hero levels) their income increase eventually
        /// buys: <c>ln(1 + g) / ln(TRAIN_COST_G)</c> ATK levels, <c>ln(1 + g) / ln(HERO_LVL_EXP growth)</c> hero levels.
        /// </summary>
        public static double MarginalLnDps(ContentDb db, GameState s, Stat stat)
        {
            int n = PowerFormula.Level(s, stat);
            double p = db.Stat(stat).PerLvl.GetValueOrDefault();
            switch (stat)
            {
                case Stat.ATK:
                case Stat.ATK_SPD:
                    return Math.Log((1 + p * (n + 1)) / (1 + p * n));
                case Stat.CRIT_RATE:
                {
                    double dmg = PowerFormula.CritDmg(db, s), rate = PowerFormula.CritRate(db, s);
                    return Math.Log(PowerFormula.CritFactor(rate + p, dmg) / PowerFormula.CritFactor(rate, dmg));
                }
                case Stat.CRIT_DMG:
                {
                    double dmg = PowerFormula.CritDmg(db, s), rate = PowerFormula.CritRate(db, s);
                    return Math.Log(PowerFormula.CritFactor(rate, dmg + p) / PowerFormula.CritFactor(rate, dmg));
                }
                case Stat.GOLD_GAIN:
                {
                    double income = Math.Log((1 + p * (n + 1)) / (1 + p * n));
                    double levels = income / Math.Log(db.TrainingCurves[Stat.ATK].Cost.Growth);
                    return IncomeStatWeight * levels * MarginalLnDps(db, s, Stat.ATK);
                }
                case Stat.EXP_GAIN:
                {
                    double income = Math.Log((1 + p * (n + 1)) / (1 + p * n));
                    double levels = income / Math.Log(db.HeroLevels.Exp.Growth);
                    return IncomeStatWeight * levels * Math.Log(db.HeroLevels.MultPerLevel.GetValueOrDefault(1.0));
                }
                default:
                    return 0;
            }
        }

        // ============================================================================================ step 2: tickets, diamonds

        /// <summary>Every summon ticket on its banner, 10 at a time while possible, then singles (03 section 8.3 step 2).</summary>
        public static void SpendTickets(BotContext ctx)
        {
            foreach (var b in BannerOrder)
            {
                if (!Open(ctx, b, out var def)) continue;
                while (true)
                {
                    double t = ctx.State.Wallet.Get(def.TicketCurrency);
                    int count = t >= def.GuaranteeBatchSize ? def.GuaranteeBatchSize : t >= 1 ? 1 : 0;
                    if (count == 0 || !ctx.Summon(b, count, SummonPayment.Tickets)) break;
                    AutoEquip(ctx, b);
                }
            }
        }

        /// <summary>
        /// Diamonds to the banner with the highest expected ln(DPS) gain per diamond (03 section 8.3 step 2, 10 section 3.4):
        /// a 10-pull when affordable; a single only when the 10-pull is not and pity is within
        /// <see cref="SinglePullPityWindow"/> of PITY_HARD; otherwise save up. Re-evaluated after every pull.
        /// </summary>
        public static void SpendDiamonds(BotContext ctx)
        {
            while (true)
            {
                var b = BestBanner(ctx, out _);
                if (b == null) return;
                var def = ctx.Content.Banners[b.Value];
                long dia = ctx.State.Wallet.Diamond;
                int count;
                if (dia >= (long)def.PullCostDiamonds * def.GuaranteeBatchSize) count = def.GuaranteeBatchSize;
                else if (dia >= def.PullCostDiamonds && def.PityHard - Pity(ctx, b.Value) <= SinglePullPityWindow) count = 1;
                else return;
                if (!ctx.Summon(b.Value, count, SummonPayment.Diamonds)) return;
                AutoEquip(ctx, b.Value);
            }
        }

        /// <summary>The open banner with the highest expected ln(DPS) gain per diamond (positive), or null.</summary>
        public static Banner? BestBanner(BotContext ctx, out double gainPerDiamond)
        {
            Banner? best = null;
            gainPerDiamond = 0;
            GameState scratch = null;
            foreach (var b in BannerOrder)
            {
                if (!Open(ctx, b, out var def) || def.PullCostDiamonds <= 0) continue;
                scratch ??= StateCopy.DeepClone(ctx.State);
                double g = ExpectedPullGain(ctx.Content, scratch, b) / def.PullCostDiamonds;
                if (g > gainPerDiamond) { best = b; gainPerDiamond = g; }
            }
            return best;
        }

        /// <summary>
        /// Expected ln(DPS) gain of one pull on <paramref name="b"/>: over the banner's current published rate table (the
        /// soft-pity L+ rate of the next pull folded into L and M), the mean over each rarity's pool of the gain of one more
        /// copy evaluated against the current collection, with the loadout re-picked as AutoEquipBest* would.
        /// <paramref name="scratch"/> is a clone the method mutates and restores.
        /// </summary>
        public static double ExpectedPullGain(ContentDb db, GameState scratch, Banner b)
        {
            var def = db.Banners[b];
            scratch.Banners.TryGetValue(b, out var bs);
            var rates = def.RateTable(def.LevelFor(bs?.Exp ?? 0));
            double lplusBase = 0;
            foreach (var kv in rates) if (kv.Key >= Rarity.LEGENDARY) lplusBase += kv.Value;
            double lplusNow = def.PityRate((bs?.Pity ?? 0) + 1);
            double lScale = lplusBase > 0 ? lplusNow / lplusBase : 1, restScale = lplusBase < 1 ? (1 - lplusNow) / (1 - lplusBase) : 0;

            double baseLn = Math.Log(PowerFormula.Compute(db, scratch).RatedDps);
            var col = CollectionRules.For(scratch, b);
            int maxCopies = PullAlgorithm.MaxCopies(db);
            double expected = 0;
            foreach (Rarity r in Enum.GetValues(typeof(Rarity)))
            {
                if (!rates.TryGetValue(r, out double rate) || rate <= 0 || !def.Pool.TryGetValue(r, out var ids) || ids.Count == 0) continue;
                rate *= r >= Rarity.LEGENDARY ? lScale : restScale;
                double sum = 0;
                foreach (var id in ids) sum += CopyGain(db, scratch, b, col, id, maxCopies, baseLn);
                expected += rate * sum / ids.Count;
            }
            return expected;
        }

        static double CopyGain(ContentDb db, GameState s, Banner b, CollectionState col, string id, int maxCopies, double baseLn)
        {
            col.Items.TryGetValue(id, out var item);
            if (item != null && item.Copies >= maxCopies) return 0;   // overflow converts to Rock Candy (MVP sink)
            bool added = item == null;
            if (added) col.Items[id] = item = new OwnedItem();
            item.Copies += 1;
            string weapon = s.Loadout.Weapon;
            string[] skills = s.Loadout.Skills;
            if (b == Banner.WEAPON) s.Loadout.Weapon = CollectionRules.BestWeapon(db, s);
            else if (b == Banner.SKILL) s.Loadout.Skills = CollectionRules.BestSkills(db, s);
            double gain = Math.Log(PowerFormula.Compute(db, s).RatedDps) - baseLn;
            s.Loadout.Weapon = weapon;
            s.Loadout.Skills = skills;
            item.Copies -= 1;
            if (added) col.Items.Remove(id);
            return gain > 0 ? gain : 0;
        }

        static bool Open(BotContext ctx, Banner b, out BannerDef def) =>
            ctx.Content.Banners.TryGetValue(b, out def) && def.Enabled && def.Levels.Count > 0 && ctx.State.Banners.ContainsKey(b);

        static int Pity(BotContext ctx, Banner b) => ctx.State.Banners.TryGetValue(b, out var bs) && bs != null ? bs.Pity : 0;

        /// <summary>AutoEquipBest* after every pull (10 section 3.4). Weapons are also auto-equipped by ApplySummonResult.</summary>
        static void AutoEquip(BotContext ctx, Banner b)
        {
            if (b == Banner.WEAPON && CollectionRules.BestWeapon(ctx.Content, ctx.State) != ctx.State.Loadout.Weapon) ctx.Apply(new AutoEquipBestWeapon());
            if (b == Banner.SKILL && CollectionRules.BetterSkillAvailable(ctx.Content, ctx.State)) ctx.Apply(new AutoEquipBestSkills());
        }

        // ============================================================================================ step 5: Fish Board

        /// <summary>
        /// 05 section 8's attentive policy (03 section 8.3 step 5): release until the board is full or the tank is empty
        /// (ReleaseMax), merge every equal pair lowest level first (MergeAll), repeat, at most <see cref="MergesPerMinuteCap"/>
        /// merges per call (one call per active minute; issue #19). Near the cap single MergeFish commands on the
        /// lowest-level pair (MergeAll's own order) keep the count exact. If the board is full with no merge and the tank is
        /// not empty, discard the lowest-level fish, but only when that can lead to a merge: a jammed board at its reachable
        /// ceiling (05 section 4.3) stays as it is, since every discard there is undone by the next release.
        /// </summary>
        public static void PlayFishBoard(BotContext ctx)
        {
            var sim = ctx.Sim;
            var fish = ctx.State.Fish;
            long mergesBefore = fish.TotalMerges;
            for (int guard = 0; guard < 256; guard++)
            {
                bool acted = false;
                if (sim.FishCanRelease) acted |= ctx.Apply(new ReleaseMax()).Ok;
                long budget = MergesPerMinuteCap - (fish.TotalMerges - mergesBefore);
                if (budget > 0 && sim.FishMergeablePairs > 0)
                {
                    if (budget >= fish.Cells.Length) acted |= ctx.Apply(new MergeAll()).Ok;   // one MergeAll makes fewer merges than cells
                    else
                    {
                        var pair = LowestPair(ctx);
                        if (pair.source >= 0) acted |= ctx.Apply(new MergeFish(pair.source, pair.target)).Ok;
                    }
                }
                if (!acted && fish.TankCount > 0 && FishBoard.EmptyCells(ctx.State) == 0 && sim.FishMergeablePairs == 0)
                {
                    int cell = DiscardCandidate(ctx);
                    if (cell >= 0) acted = ctx.Apply(new DiscardFish(cell)).Ok;
                }
                if (!acted) return;
            }
        }

        /// <summary>The pair MergeAll would merge first: the lowest mergeable level, its leftmost two fish (result in the second).</summary>
        public static (int source, int target) LowestPair(BotContext ctx)
        {
            var cells = ctx.State.Fish.Cells;
            int cap = ctx.Sim.FishMaxLvl;
            for (int level = 1; level < cap; level++)
            {
                int first = -1;
                for (int i = 0; i < cells.Length; i++)
                {
                    if (cells[i] != level) continue;
                    if (first < 0) first = i;
                    else return (first, i);
                }
            }
            return (-1, -1);
        }

        /// <summary>
        /// The lowest-level fish whose discard lets the next released fish merge (a level other than the release level
        /// while a release-level fish is on the board), or -1. At the reachable ceiling every discard is undone by the next
        /// release, so there is none.
        /// </summary>
        static int DiscardCandidate(BotContext ctx)
        {
            var s = ctx.State;
            var cells = s.Fish.Cells;
            int cap = ctx.Sim.FishMaxLvl;
            int release = FishBoard.ReleaseLevel(s, ctx.Content);
            int capFish = 0;
            bool hasRelease = false;
            foreach (int c in cells)
            {
                if (c >= cap) capFish++;
                if (c == release) hasRelease = true;
            }
            if (capFish >= cells.Length - cap + 1 || !hasRelease) return -1;   // ceiling (05 section 4.3), or nothing to pair with
            int best = -1;
            for (int i = 0; i < cells.Length; i++)
                if (cells[i] > release && cells[i] < cap && (best < 0 || cells[i] < cells[best])) best = i;
            return best;
        }

        // ============================================================================================ step 6: boss

        /// <summary>When walled, retry the frontier boss as soon as Query.BossClearable says it falls (10 section 3.4).</summary>
        public static void RetryBoss(BotContext ctx)
        {
            var c = ctx.State.Combat;
            if (c.Boss == BossPhase.Walled && ctx.Query.BossClearable(c.Stage)) ctx.Apply(new AttemptBoss());
        }
    }
}
