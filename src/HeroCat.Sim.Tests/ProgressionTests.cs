using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Power;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>03 section 5: Training (AC 6, 9; issue #83) and Hero Level (AC 11; issue #84).</summary>
    public class TrainingTests
    {
        static Constants K => Combat.K;

        static void AssertRejectIsPure(Simulation sim, Command c, RejectCode code, string reason)
        {
            sim.DrainEvents();
            string before = sim.StateHash();
            var r = sim.Apply(c);
            Assert.That((r.Ok, r.Code, r.Reason), Is.EqualTo((false, code, reason)), c.ToString());
            Assert.That(sim.StateHash(), Is.EqualTo(before), c.ToString());
            Assert.That(sim.DrainEvents(), Is.Empty, c.ToString());
        }

        [Test]
        public void AC9_TrainStat_AllOrNothing_ClosedFormCost_OneEvent()
        {
            var sim = Combat.NewSim();
            double cost10 = 50 * (Math.Pow(1.07, 10) - 1) / 0.07;   // 03 section 5.1's closed form, test side only
            Assert.That(sim.Query.TrainCost(Stat.ATK, 10), Is.EqualTo(cost10).Within(1e-9).Percent);
            Combat.Give(sim, Currency.GOLD, cost10 * 0.999);
            AssertRejectIsPure(sim, new TrainStat(Stat.ATK, 10), RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_GOLD);

            Combat.Give(sim, Currency.GOLD, cost10);
            double gold = sim.State.Wallet.Gold, quote = sim.Query.TrainCost(Stat.ATK, 10), mult = sim.Query.Power.Training;
            Assert.That(sim.Apply(new TrainStat(Stat.ATK, 10)).Ok, Is.True);
            Assert.That(sim.State.Wallet.Gold, Is.EqualTo(gold - quote), "charges exactly the quote");
            Assert.That(sim.Query.Power.Training - mult, Is.EqualTo(0.5).Within(1e-12));
            var ev = sim.DrainEvents();
            var st = ev.OfType<StatTrained>().Single();
            Assert.That((st.Stat, st.LevelFrom, st.NewLevel, st.GoldCost), Is.EqualTo((Stat.ATK, 0, 10, quote)));
            var cc = ev.OfType<CurrencyChanged>().Single();
            Assert.That((cc.Currency, cc.Delta, cc.Reason), Is.EqualTo((Currency.GOLD, -quote, CurrencyReason.TRAINING)));
            Assert.That(sim.State.Stats.TrainingLevelsBought, Is.EqualTo(10));
        }

        [Test]
        public void AC9_Rejects_Cap_NotTrainable_BadArguments()
        {
            var sim = Combat.NewSim();
            Combat.Give(sim, Currency.GOLD, 1e300);
            Combat.SetLevel(sim, Stat.CRIT_RATE, 300);
            AssertRejectIsPure(sim, new TrainStat(Stat.CRIT_RATE, 1), RejectCode.Capped, CombatRejectReasons.STAT_AT_CAP);
            AssertRejectIsPure(sim, new TrainStatMax(Stat.CRIT_RATE), RejectCode.Capped, CombatRejectReasons.STAT_AT_CAP);
            Combat.SetLevel(sim, Stat.CRIT_RATE, 295);
            AssertRejectIsPure(sim, new TrainStat(Stat.CRIT_RATE, 10), RejectCode.Capped, CombatRejectReasons.STAT_AT_CAP);
            AssertRejectIsPure(sim, new TrainStat(Stat.HP, 1), RejectCode.Locked, CombatRejectReasons.STAT_NOT_TRAINABLE);
            AssertRejectIsPure(sim, new TrainStatMax(Stat.DEF), RejectCode.Locked, CombatRejectReasons.STAT_NOT_TRAINABLE);
            AssertRejectIsPure(sim, new TrainStat(Stat.ATK, 0), RejectCode.BadArgument, CombatRejectReasons.INVALID_COUNT);
            AssertRejectIsPure(sim, new TrainStat((Stat)99, 1), RejectCode.BadArgument, CombatRejectReasons.INVALID_STAT);
            AssertRejectIsPure(sim, new TrainStat(Stat.ATK, int.MaxValue), RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_GOLD);
        }

        [Test]
        public void TrainStatMax_ChargesTheQuote_AndClampsAtTheCap()
        {
            var sim = Combat.NewSim();
            Combat.Give(sim, Currency.GOLD, 123_456);
            int m = sim.Query.MaxAffordableLevels(Stat.ATK);
            double quote = sim.Query.TrainCost(Stat.ATK, m);
            Assert.That(m, Is.GreaterThan(1));
            Assert.That(sim.Query.TrainCost(Stat.ATK, m + 1), Is.GreaterThan(sim.State.Wallet.Gold));
            double gold = sim.State.Wallet.Gold;
            Assert.That(sim.Apply(new TrainStatMax(Stat.ATK)).Ok, Is.True);
            Assert.That((sim.State.Training.Level[(int)Stat.ATK], sim.State.Wallet.Gold), Is.EqualTo((m, gold - quote)));
            AssertRejectIsPure(sim, new TrainStatMax(Stat.ATK), RejectCode.CannotAfford, CombatRejectReasons.NOT_ENOUGH_GOLD);

            Combat.Give(sim, Currency.GOLD, 1e300);
            Combat.SetLevel(sim, Stat.ATK_SPD, 95);
            Assert.That(sim.Apply(new TrainStatMax(Stat.ATK_SPD)).Ok, Is.True);
            Assert.That(sim.State.Training.Level[(int)Stat.ATK_SPD], Is.EqualTo(100));
        }

        [Test]
        public void AStatBoughtMidFight_AppliesToTheNextHit()   // 03 section 7.3
        {
            var sim = Combat.NewSim();
            Combat.AdvanceUntil<EnemySpawned>(sim);
            Combat.Fight(sim).EnemyHp = 1e9;
            var first = Combat.AdvanceUntil<HitLanded>(sim);
            Combat.Give(sim, Currency.GOLD, 1e6);
            Assert.That(sim.Apply(new TrainStat(Stat.ATK, 10)).Ok, Is.True);
            var second = Combat.AdvanceUntil<HitLanded>(sim);
            Assert.That(second.Damage / first.Damage, Is.EqualTo(1.5).Within(1e-12));
        }

        [Test]
        public void AC6_Crit_FrequencyAndMeanDamage_Over100kHits()
        {
            var sim = Combat.NewSim();
            Combat.SetLevel(sim, Stat.CRIT_RATE, 100);
            Combat.SetLevel(sim, Stat.CRIT_DMG, 50);
            Combat.SetLevel(sim, Stat.ATK_SPD, 100);
            Combat.AdvanceUntil<EnemySpawned>(sim);
            Combat.Fight(sim).EnemyHp = 1e300;
            double hit = sim.Query.HeroHit;
            Assert.That((sim.Query.CritRate, sim.Query.CritDmg), Is.EqualTo((0.25, 2.0)));
            long n = 0, crits = 0;
            double sum = 0;
            while (n < 100_000)
            {
                sim.Advance(1000);
                foreach (var h in sim.DrainEvents().OfType<HitLanded>())
                {
                    n++;
                    sum += h.Damage;
                    if (h.IsCrit) crits++;
                }
            }
            double p = 0.25, sigma = Math.Sqrt(p * (1 - p) / n);
            Assert.That((double)crits / n, Is.EqualTo(p).Within(3 * sigma));
            Assert.That(sum / n, Is.EqualTo(hit * 1.25).Within(1).Percent);
            Assert.That(sim.State.Rng[RngStreams.Crit].Counter, Is.EqualTo(sim.State.Stats.HeroHits));
        }

        [Test]
        public void AC11_OneGrantCrossingTwoThresholds_LevelsTwice()
        {
            var sim = Combat.NewSim();
            var db = Combat.Db;
            double grant = db.HeroLvlExp(1) + db.HeroLvlExp(2) + 1;
            Assert.That(Ledger.Apply(sim.State, new CurrencyDelta(Currency.HERO_EXP, grant), CurrencyReason.KILL, sim), Is.True);
            Assert.That(sim.State.Hero.Level, Is.EqualTo(3));
            Assert.That(sim.State.Hero.Exp, Is.EqualTo(1).Within(1e-9));
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<LevelUp>().Select(l => l.Level), Is.EqualTo(new[] { 2, 3 }));
            Assert.That(ev[0], Is.InstanceOf<CurrencyChanged>(), "the EXP delta first, then the level-ups");

            double m = 1.0;
            for (int i = 0; i < 3; i++) m *= 1.02;
            Assert.That(sim.Query.HeroMult, Is.EqualTo(m), "HERO_LVL_MULT^level by repeated multiplication, bit-identical");
            Assert.That(db.HeroLevels.MultPerLevel, Is.EqualTo(1.02));
        }

        [Test]
        public void HeroLevel_StopsAtTheCap_AndAnInfiniteThresholdNeverLoops()
        {
            int cap = Combat.Db.HeroLevels.LevelCap;
            var sim = Combat.NewSim();
            Assert.That(Ledger.Apply(sim.State, new CurrencyDelta(Currency.HERO_EXP, 1e308), CurrencyReason.KILL, sim), Is.True);
            Assert.That(sim.State.Hero.Level, Is.GreaterThan(1000).And.LessThan(cap), "stops where the threshold passes the EXP held");
            Assert.That(double.IsInfinity(Combat.Db.HeroLvlExp(cap - 1)), Is.True, "past here the threshold is infinite");

            var atCap = Combat.NewSim();
            atCap.State.Hero.Level = cap;
            atCap.State.Hero.Exp = 1e308;
            atCap.CheckHeroLevel();
            Assert.That(atCap.State.Hero.Level, Is.EqualTo(cap));
            Assert.That(atCap.DrainEvents().OfType<LevelUp>(), Is.Empty);
        }

        [Test]
        public void KillExp_GoesThroughTheLedger_AndLevelsTheHero()   // 03 section 2.6, issue #84
        {
            var sim = Combat.NewSim(hits: false);
            var ev = new List<Event>();
            while (sim.State.Hero.Level == 1) { sim.Advance(10); ev.AddRange(sim.DrainEvents()); }
            var expDeltas = ev.OfType<CurrencyChanged>().Where(c => c.Currency == Currency.HERO_EXP).ToList();
            Assert.That(expDeltas.All(c => c.Reason == CurrencyReason.KILL), Is.True);
            Assert.That(expDeltas.Sum(c => c.Delta), Is.EqualTo(ev.OfType<EnemyKilled>().Sum(k => k.Exp)).Within(1e-9));
            Assert.That(ev.OfType<LevelUp>().Single().Level, Is.EqualTo(2));
        }
    }

    /// <summary>03 section 7 and 04 sections 5-7: the layer functions and the rated/realised split (AC 10, 20; issues #85, #86).</summary>
    public class PowerTests
    {
        static Constants K => Combat.K;

        [Test]
        public void AC10_Factors_EqualHandComputedValues()
        {
            var sim = new Simulation(TestContent.NewState(), TestContent.Mvp);
            var s = sim.State;
            s.Training.Level[(int)Stat.CRIT_RATE] = 120;
            s.Training.Level[(int)Stat.CRIT_DMG] = 80;
            s.Training.Level[(int)Stat.ATK_SPD] = 100;
            s.Hero.Level = 50;
            s.Hero.AwakenRank = 2;
            sim.Query.MarkDirty();
            var q = sim.Query;
            double hero = 1;
            for (int i = 0; i < 50; i++) hero *= 1.02;
            Assert.That(q.CritRate, Is.EqualTo(0.3).Within(1e-12).Percent);
            Assert.That(q.CritDmg, Is.EqualTo(2.3).Within(1e-12).Percent);
            Assert.That(q.CritFactor, Is.EqualTo(1 + 0.3 * 1.3).Within(1e-12).Percent);
            Assert.That(q.SpeedFactor, Is.EqualTo(2.0).Within(1e-12).Percent);
            Assert.That(q.HeroMult, Is.EqualTo(hero).Within(1e-12).Percent);
            Assert.That(q.AwakenMult, Is.EqualTo(2.25).Within(1e-12).Percent);
            Assert.That(q.ResearchPct, Is.EqualTo(0.0), "TODO(#158): res_atk 7 once Research lands");
        }

        [Test]
        public void Units_PinPercentPointsVersusFractions()   // 03 section 7.2
        {
            var sim = Combat.NewSim();
            var s = sim.State;
            s.Weapons.Items.Clear();
            Combat.Own(s.Weapons, "weapon_e_01", 1);
            s.Loadout.Weapon = "weapon_e_01";
            Combat.Own(s.Skills, "skill_e_02", 1);
            s.Loadout.Skills[0] = "skill_e_02";
            s.Fish.Cells[0] = 1;
            s.Fish.Cells[3] = 2;
            s.Training.Level[(int)Stat.ATK] = 4;
            s.Training.Level[(int)Stat.CRIT_RATE] = 40;
            s.Training.Level[(int)Stat.ATK_SPD] = 30;
            sim.Query.MarkDirty();
            var p = sim.Query.Power;
            Assert.That(p.EquippedWeaponPct, Is.EqualTo(120), "percentage points");
            Assert.That(p.WeaponPassivePct, Is.EqualTo(4));
            Assert.That(p.Weapons, Is.EqualTo(1 + 124 / 100.0));
            Assert.That(p.SkillDpsPct, Is.EqualTo(40));
            Assert.That(p.Skills, Is.EqualTo(1.4));
            double board = Combat.Db.FishLevels[0].BoardDpsPct + Combat.Db.FishLevels[1].BoardDpsPct;
            Assert.That(p.FishBoardDpsPct, Is.EqualTo(board));
            Assert.That(board, Is.EqualTo(0.5 * 5 + 0.5 * 7.5).Within(1e-12));
            Assert.That(p.CritRate, Is.EqualTo(0.1).Within(1e-12), "a fraction");
            Assert.That(p.Training, Is.EqualTo(1.2).Within(1e-12));
            Assert.That(p.Speed, Is.EqualTo(1.3).Within(1e-12));
            Assert.That(p.Companions, Is.EqualTo(1.0), "MVP");
            Assert.That(p.Research, Is.EqualTo(1.0), "MVP");
            Assert.That(p.Runes, Is.EqualTo(1.0), "P1");
            Assert.That(p.Awakening, Is.EqualTo(1.0), "no awakening rows in an M0 build");

            Assert.That(p.HeroHit, Is.EqualTo(K.BaseAtk * p.Training * p.Hero * p.Weapons * (1 + board / 100)).Within(1e-12).Percent);
            Assert.That(p.SkillUnit, Is.EqualTo(p.HeroHit * p.Crit * p.Speed));
            Assert.That(p.CompanionUnit, Is.EqualTo(p.SkillUnit * p.Skills));
            double rated = 1;
            foreach (PowerLayer l in Enum.GetValues(typeof(PowerLayer))) rated *= p.Factor(l);
            Assert.That(p.RatedDps, Is.EqualTo(rated).Within(1e-12).Percent);
            Assert.That(p.RatedDps, Is.EqualTo(p.SkillUnit * p.Skills).Within(1e-12).Percent, "board is in both; no missiles");
        }

        [Test]
        public void Weapons_04AC8_Fixture()
        {
            var sim = Combat.NewSim();
            var s = sim.State;
            s.Weapons.Items.Clear();
            Combat.Own(s.Weapons, "weapon_l_01", 6);   // level 3
            for (int i = 1; i <= 10; i++) Combat.Own(s.Weapons, $"weapon_c_{i:00}", 3);   // level 2
            for (int i = 1; i <= 4; i++) Combat.Own(s.Weapons, $"weapon_r_{i:00}", 1);    // level 1
            s.Loadout.Weapon = "weapon_l_01";
            sim.Query.MarkDirty();
            Assert.That(sim.Query.Power.Weapons, Is.EqualTo(5.512).Within(1e-12));

            s.Weapons.Items["weapon_l_01"].LimitBreaks = 2;   // +2 effective levels (04 section 5.2)
            sim.Query.MarkDirty();
            Assert.That(sim.Query.Power.EquippedWeaponPct, Is.EqualTo(300 * 1.8).Within(1e-9));
            Assert.That(PowerFormula.ItemLevelMult(Combat.Db, null), Is.EqualTo(0));
        }

        [Test]
        public void WeaponPassive_DoesNotDependOnInsertionOrder()
        {
            var a = Combat.NewSim();
            var b = Combat.NewSim();
            var ids = Combat.Db.Weapons.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
            a.State.Weapons.Items.Clear();
            b.State.Weapons.Items.Clear();
            for (int i = 0; i < ids.Count; i++) Combat.Own(a.State.Weapons, ids[i], 1 + i % 7, i % 3);
            for (int i = ids.Count - 1; i >= 0; i--) Combat.Own(b.State.Weapons, ids[i], 1 + i % 7, i % 3);
            a.Query.MarkDirty();
            b.Query.MarkDirty();
            Assert.That(BitConverter.DoubleToInt64Bits(b.Query.Power.WeaponPassivePct), Is.EqualTo(BitConverter.DoubleToInt64Bits(a.Query.Power.WeaponPassivePct)));
        }

        [Test]
        public void SkillLayer_ScalesWithLevel_UnownedCountsZero()
        {
            var sim = Combat.NewSim();
            var s = sim.State;
            s.Loadout.Skills[0] = "skill_e_02";
            s.Loadout.Skills[1] = "skill_r_02";
            Combat.Own(s.Skills, "skill_e_02", 10);   // level 4
            sim.Query.MarkDirty();
            Assert.That(sim.Query.Power.SkillDpsPct, Is.EqualTo(40 * 1.6).Within(1e-12));
        }

        [Test]
        public void FishBoardLayer_FollowsTheBoard_ThroughTheDpsCache()   // 05 section 12 rule 5, OnFishBoardDpsChanged
        {
            var sim = Combat.NewSim(hits: false);
            double before = sim.Query.HeroHit;
            Assert.That(sim.Query.Power.FishBoardDpsPct, Is.EqualTo(sim.FishBoardDpsPct));
            sim.State.Fish.TankCount = 2;
            Assert.That(sim.Apply(new ReleaseMax()).Ok, Is.True);
            double pct = sim.FishBoardDpsPct;
            Assert.That(pct, Is.GreaterThan(0));
            Assert.That(sim.Query.Power.FishBoardDpsPct, Is.EqualTo(pct), "the cache saw the board change");
            Assert.That(sim.Query.HeroHit, Is.EqualTo(before * (1 + pct / 100.0)).Within(1e-9 * before));
        }

        [Test]
        public void MissileDpsPct_ClosedForm_And_RatedWithMissiles()   // 05 section 5.1
        {
            var db = Combat.Db;
            Assert.That(PowerFormula.MissileHitsPerFish(db, 10), Is.EqualTo(0.05 * 0.75 * (1 - Math.Pow(0.75, 9)) / 0.25).Within(1e-12));
            Assert.That(PowerFormula.MissileDpsPct(db, 10, 10, 1), Is.EqualTo(2.3).Within(0.05));
            var sim = Combat.NewSim();
            double rated = sim.Query.RatedDps;
            Assert.That(sim.Query.RatedDpsWithMissiles(0), Is.EqualTo(rated).Within(1e-12).Percent);
            Assert.That(sim.Query.RatedDpsWithMissiles(10), Is.EqualTo(rated * 1.1).Within(1e-12).Percent);
        }

        [Test]
        public void BossClearable_IsTheThreeSecondRule()   // 03 section 2.8
        {
            var sim = Combat.NewSim();
            double dps = sim.Query.RatedDps;
            int s = 1;
            while (Combat.Db.EnemyHp.Eval(s + 1) / dps <= 3.0) s++;
            Assert.That(sim.Query.BossClearable(s), Is.True);
            Assert.That(sim.Query.BossClearable(s + 1), Is.False);
        }

        /// <summary>
        /// The damage the loop deals over <paramref name="ticks"/> against an unkillable enemy, per second, with the hero
        /// attack counted at its expected crit value (03 AC20).
        /// </summary>
        static double MeasuredDps(Simulation sim, int ticks)
        {
            Combat.AdvanceUntil<EnemySpawned>(sim);
            var f = Combat.Fight(sim);
            double total = 0, heroActual = 0;
            long heroHits = 0;
            const double Hp = 1e9;
            for (int i = 0; i < ticks; i++)
            {
                f.EnemyHp = Hp;
                sim.Advance(1);
                total += Hp - f.EnemyHp;
                foreach (var h in sim.DrainEvents().OfType<HitLanded>()) { heroActual += h.Damage; heroHits++; }
            }
            Assert.That(f.Phase, Is.EqualTo(FightPhase.Fighting));
            double heroExpected = heroHits * sim.Query.HeroHit * sim.Query.CritFactor;
            return (total - heroActual + heroExpected) / (ticks / (double)Combat.K.TickHz);
        }

        [Test]
        public void AC20_RatedDps_MatchesMeasuredDps_Within1Pct()   // issue #10; companions are MVP (#156)
        {
            var sim = Combat.NewSim();
            var s = sim.State;
            foreach (var id in new[] { "skill_e_02", "skill_r_03", "skill_r_08" }) Combat.Own(s.Skills, id, 3);
            s.Loadout.Skills[0] = "skill_e_02";   // INSTANT
            s.Loadout.Skills[1] = "skill_r_03";   // INSTANT
            s.Loadout.Skills[2] = "skill_r_08";   // DOT
            s.Fish.Cells[0] = 3;                  // static board, no merges
            s.Fish.Cells[7] = 5;
            s.Training.Level[(int)Stat.ATK_SPD] = 50;
            s.Training.Level[(int)Stat.CRIT_RATE] = 120;
            s.Training.Level[(int)Stat.CRIT_DMG] = 80;
            sim.Query.MarkDirty();
            double measured = MeasuredDps(sim, 100_000);
            Assert.That(measured, Is.EqualTo(sim.Query.RatedDps).Within(1).Percent);
        }

        [Test]
        public void Skill_04AC9_EpicInstant_FourSkillUnitsPerCast_FortyPctOfHeroDamage()
        {
            var sim = Combat.NewSim();
            var s = sim.State;
            Combat.Own(s.Skills, "skill_e_02", 1);    // EPIC, INSTANT, dps 40, cooldown 100
            s.Loadout.Skills[0] = "skill_e_02";
            sim.Query.MarkDirty();
            Assert.That(Combat.Db.Skills["skill_e_02"].DamagePctPerCast, Is.EqualTo(400));

            var spawn = Combat.AdvanceUntil<EnemySpawned>(sim);
            var f = Combat.Fight(sim);
            const double Hp = 1e9;
            f.EnemyHp = Hp;
            sim.Advance(1);   // first Fighting tick: the skill (off cooldown since spawn) fires
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<SkillCast>().Single().Tick, Is.EqualTo(spawn.Tick + 1));
            Assert.That(Hp - f.EnemyHp, Is.EqualTo(4.0 * sim.Query.SkillUnit).Within(1e-6));
            Assert.That(sim.Query.SkillUnit, Is.EqualTo(sim.Query.HeroHit), "speed 1, no crit");

            double skill = 0, hero = 0;
            for (int i = 0; i < 600; i++)
            {
                f.EnemyHp = Hp;
                sim.Advance(1);
                double d = Hp - f.EnemyHp;
                var e = sim.DrainEvents();
                double h = e.OfType<HitLanded>().Sum(x => x.Damage);
                hero += h;
                skill += d - h;
            }
            Assert.That(skill / hero, Is.EqualTo(0.40).Within(1).Percent);
        }

        [Test]
        public void Skills_OneCastPerTick_SlotZeroFirst_CooldownTicksWhileSpawning()
        {
            var sim = Combat.NewSim(hits: false);
            var s = sim.State;
            Combat.Own(s.Skills, "skill_e_05", 1);   // cooldown 60
            Combat.Own(s.Skills, "skill_e_01", 1);   // cooldown 80
            s.Loadout.Skills[0] = "skill_e_05";
            s.Loadout.Skills[1] = "skill_e_01";
            var spawn = Combat.AdvanceUntil<EnemySpawned>(sim);
            Combat.Fight(sim).EnemyHp = 1e12;
            var casts = new List<SkillCast>();
            for (int i = 0; i < 4; i++) { sim.Advance(1); casts.AddRange(sim.DrainEvents().OfType<SkillCast>()); }
            Assert.That(casts.Select(c => (c.Tick, c.Slot)), Is.EqualTo(new[] { (spawn.Tick + 1, 0), (spawn.Tick + 2, 1) }));

            // a cooldown that expires during Spawning fires on the first tick of the next fight
            var f = Combat.Fight(sim);
            sim.Advance(55);
            sim.DrainEvents();
            f.EnemyHp = 0.0;   // dies on the next tick
            var kill = Combat.AdvanceUntil<EnemyKilled>(sim, 2);
            Assert.That(s.Combat.SkillCooldowns[0], Is.LessThanOrEqualTo(K.SpawnDelayTicks));
            var next = Combat.AdvanceUntil<EnemySpawned>(sim, 20);
            Assert.That(s.Combat.SkillCooldowns[0], Is.EqualTo(0));
            sim.Advance(1);
            Assert.That(sim.DrainEvents().OfType<SkillCast>().Single().Slot, Is.EqualTo(0));
            Assert.That(kill.Tick, Is.LessThan(next.Tick));
        }

        [Test]
        public void Buffs_StrongestApplies_AWeakerCastNeverShortensIt()   // 04 AC10
        {
            var sim = Combat.NewSim();
            var s = sim.State;
            Combat.Own(s.Skills, "skill_e_08", 1);   // BUFF 120% for 50 ticks, cooldown 150
            Combat.Own(s.Skills, "skill_c_13", 1);   // BUFF 30% for 50 ticks, cooldown 150
            s.Loadout.Skills[0] = "skill_e_08";
            s.Loadout.Skills[1] = "skill_c_13";
            Combat.AdvanceUntil<EnemySpawned>(sim);
            var f = Combat.Fight(sim);
            f.EnemyHp = 1e12;
            sim.Advance(1);
            Assert.That((f.AtkBuffPct, f.AtkBuffTicksLeft), Is.EqualTo((120.0, 49)));
            sim.Advance(1);   // the weaker buff casts and is dropped
            Assert.That((f.AtkBuffPct, f.AtkBuffTicksLeft), Is.EqualTo((120.0, 48)));
            var hit = Combat.AdvanceUntil<HitLanded>(sim);
            Assert.That(hit.Damage, Is.EqualTo(sim.Query.HeroHit * 2.2).Within(1e-9));
            sim.Advance(60);
            Assert.That((f.AtkBuffPct, f.AtkBuffTicksLeft), Is.EqualTo((0.0, 0)));
        }
    }
}
