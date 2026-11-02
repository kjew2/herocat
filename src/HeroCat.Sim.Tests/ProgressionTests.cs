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
            Assert.That((sim.Query.CritRate, sim.Query.CritDmg), Is.EqualTo((0.25, 2.0)));
            long n = 0, crits = 0;
            double sum = 0, nonCrit = 0;
            while (n < 100_000)
            {
                sim.Advance(1000);
                foreach (var h in sim.DrainEvents().OfType<HitLanded>())
                {
                    n++;
                    sum += h.Damage;
                    nonCrit += sim.Query.SlotHit(h.Slot);   // every party slot attacks (00 decision 109)
                    if (h.IsCrit) crits++;
                }
            }
            double p = 0.25, sigma = Math.Sqrt(p * (1 - p) / n);
            Assert.That((double)crits / n, Is.EqualTo(p).Within(3 * sigma));
            Assert.That(sum / nonCrit, Is.EqualTo(1.25).Within(1).Percent);
            Assert.That(sim.State.Rng[RngStreams.Crit].Counter, Is.EqualTo(sim.State.Stats.PartyHits));
        }

        [Test]
        public void AC11_OneGrantCrossingTwoThresholds_LevelsTwice()
        {
            var sim = Combat.NewSim();
            var db = Combat.Db;
            double grant = db.PartyLvlExp(1) + db.PartyLvlExp(2) + 1;
            Assert.That(Ledger.Apply(sim.State, new CurrencyDelta(Currency.PARTY_EXP, grant), CurrencyReason.KILL, sim), Is.True);
            Assert.That(sim.State.Party.Level, Is.EqualTo(3));
            Assert.That(sim.State.Party.Exp, Is.EqualTo(1).Within(1e-9));
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<LevelUp>().Select(l => l.Level), Is.EqualTo(new[] { 2, 3 }));
            Assert.That(ev[0], Is.InstanceOf<CurrencyChanged>(), "the EXP delta first, then the level-ups");

            double m = 1.0;
            for (int i = 0; i < 3; i++) m *= 1.02;
            Assert.That(sim.Query.PartyMult, Is.EqualTo(m), "PARTY_LVL_MULT^level by repeated multiplication, bit-identical");
            Assert.That(db.PartyLevels.MultPerLevel, Is.EqualTo(1.02));
        }

        [Test]
        public void PartyLevel_StopsAtTheCap_AndAnInfiniteThresholdNeverLoops()
        {
            int cap = Combat.Db.PartyLevels.LevelCap;
            var sim = Combat.NewSim();
            Assert.That(Ledger.Apply(sim.State, new CurrencyDelta(Currency.PARTY_EXP, 1e308), CurrencyReason.KILL, sim), Is.True);
            Assert.That(sim.State.Party.Level, Is.GreaterThan(1000).And.LessThan(cap), "stops where the threshold passes the EXP held");
            Assert.That(double.IsInfinity(Combat.Db.PartyLvlExp(cap - 1)), Is.True, "past here the threshold is infinite");

            var atCap = Combat.NewSim();
            atCap.State.Party.Level = cap;
            atCap.State.Party.Exp = 1e308;
            atCap.CheckPartyLevel();
            Assert.That(atCap.State.Party.Level, Is.EqualTo(cap));
            Assert.That(atCap.DrainEvents().OfType<LevelUp>(), Is.Empty);
        }

        [Test]
        public void KillExp_GoesThroughTheLedger_AndLevelsTheParty()   // 03 section 2.6, issue #84
        {
            var sim = Combat.NewSim(hits: false);
            var ev = new List<Event>();
            while (sim.State.Party.Level == 1) { sim.Advance(10); ev.AddRange(sim.DrainEvents()); }
            var expDeltas = ev.OfType<CurrencyChanged>().Where(c => c.Currency == Currency.PARTY_EXP).ToList();
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
            s.Party.Level = 50;
            s.Party.Slots[0].AwakenRank = 2;
            s.Research.Level["res_atk"] = 7;
            sim.Query.MarkDirty();
            var q = sim.Query;
            double hero = 1;
            for (int i = 0; i < 50; i++) hero *= 1.02;
            Assert.That(q.CritRate, Is.EqualTo(0.3).Within(1e-12).Percent);
            Assert.That(q.CritDmg, Is.EqualTo(2.3).Within(1e-12).Percent);
            Assert.That(q.CritFactor, Is.EqualTo(1 + 0.3 * 1.3).Within(1e-12).Percent);
            Assert.That(q.SpeedFactor, Is.EqualTo(2.0).Within(1e-12).Percent);
            Assert.That(q.PartyMult, Is.EqualTo(hero).Within(1e-12).Percent);
            Assert.That(q.AwakenMult(0), Is.EqualTo(2.25).Within(1e-12).Percent);
            Assert.That(q.AwakenMult(1), Is.EqualTo(1.0), "per slot (00 decision 109)");
            Assert.That(q.ResearchPct, Is.EqualTo(0.35).Within(1e-12).Percent, "res_atk 7 x 0.05");
            Assert.That(q.Power.Research, Is.EqualTo(1.35).Within(1e-12).Percent);
        }

        [Test]
        public void Units_PinPercentPointsVersusFractions()   // 03 section 7.2
        {
            var sim = Combat.NewSim();
            var s = sim.State;
            Combat.Solo(sim, "comp_c_01");   // one DAMAGE companion, dps 16
            s.Weapons.Items.Clear();
            Combat.Own(s.Weapons, "weapon_e_01", 1);
            s.Party.Slots[0].Weapon = "weapon_e_01";
            Combat.Own(s.Skills, "skill_e_02", 1);
            s.Party.Slots[0].Skill = "skill_e_02";
            s.Fish.Cells[0] = 1;
            s.Fish.Cells[3] = 2;
            s.Training.Level[(int)Stat.ATK] = 4;
            s.Training.Level[(int)Stat.CRIT_RATE] = 40;
            s.Training.Level[(int)Stat.ATK_SPD] = 30;
            sim.Query.MarkDirty();
            var p = sim.Query.Power;
            Assert.That(p.EquippedWeaponPct(0), Is.EqualTo(120), "percentage points");
            Assert.That(p.WeaponPassivePct, Is.EqualTo(4));
            Assert.That(p.WeaponFactor(0), Is.EqualTo(1 + 124 / 100.0));
            Assert.That(p.SkillDpsPct(0), Is.EqualTo(40));
            Assert.That(p.SkillFactor(0), Is.EqualTo(1.4));
            double board = Combat.Db.FishLevels[0].BoardDpsPct + Combat.Db.FishLevels[1].BoardDpsPct;
            Assert.That(p.FishBoardDpsPct, Is.EqualTo(board));
            double factor = Combat.Db.Constants.FishBoardDpsFactor;
            Assert.That(board, Is.EqualTo(factor * 5 + factor * 7.5).Within(1e-12));
            Assert.That(p.CritRate, Is.EqualTo(0.1).Within(1e-12), "a fraction");
            Assert.That(p.MpSufficiency(0), Is.EqualTo(0.5), "a fraction: 0.5 MP/s per pool against 1 MP/s (00 decision 110)");
            Assert.That(p.Training, Is.EqualTo(1.2).Within(1e-12));
            Assert.That(p.Speed, Is.EqualTo(1.3).Within(1e-12));
            Assert.That(p.OwnFactor(0), Is.EqualTo(1 + 0.5 * 20 / 100.0).Within(1e-12), "the slot's own DAMAGE ability (COMPANION_DPS_PCT[C] = 20, 00 decision 111)");
            Assert.That(p.PartyBuff, Is.EqualTo(1.0), "no BUFF_ATK companion");
            Assert.That(p.Research, Is.EqualTo(1.0), "MVP");
            Assert.That(p.Runes, Is.EqualTo(1.0), "P1");
            Assert.That(p.Awakening(0), Is.EqualTo(1.0), "no awakening rows in an M0 build");
            Assert.That((p.Occupied(0), p.Occupied(1), p.SlotHit(1), p.SlotDps(1)), Is.EqualTo((true, false, 0.0, 0.0)), "an empty slot");

            Assert.That(K.BaseAtk, Is.EqualTo(2.0), "one companion's auto-attack (00 decision 109)");
            Assert.That(p.CompanionAtkMult(0), Is.EqualTo(1.0), "a Common's rarity attack multiplier");
            Assert.That(p.SlotHit(0), Is.EqualTo(K.BaseAtk * p.Training * p.Party * p.CompanionAtkMult(0) * p.WeaponFactor(0) * (1 + board / 100)).Within(1e-12).Percent);
            Assert.That(p.PartyHit, Is.EqualTo(p.SlotHit(0)));
            Assert.That(p.SlotUnit(0), Is.EqualTo(p.SlotHit(0) * p.Crit * p.Speed));
            Assert.That(p.CompanionUnit(0), Is.EqualTo(p.SlotUnit(0) * p.SkillFactor(0)));
            Assert.That(p.RatedDps, Is.EqualTo(p.CompanionUnit(0) * p.OwnFactor(0)).Within(1e-12).Percent, "board is in the hit; no missiles");
            double rated = 1;
            foreach (PowerLayer l in Enum.GetValues(typeof(PowerLayer))) rated *= HeroCat.Runtime.Power.LayerFactors.Factor(p, l);
            Assert.That(p.RatedDps, Is.EqualTo(rated).Within(1e-12).Percent, "03 section 7.4's factors multiply back to RatedDps");
        }

        [Test]
        public void RatedDps_IsTheSumOfTheSlots_TimesThePartyBuff()   // 03 section 7.1, 00 decision 109
        {
            var sim = new Simulation(TestContent.NewState(), TestContent.Mvp);
            var s = sim.State;
            Combat.Own(s.Companions, "comp_e_06", 1);   // BUFF_ATK
            s.Party.Slots[4].Companion = "comp_e_06";
            s.Party.Slots[2].AwakenRank = 1;
            sim.Query.MarkDirty();
            var p = sim.Query.Power;
            double sum = 0, hit = 0, buff = 0;
            for (int i = 0; i < 5; i++)
            {
                sum += p.SlotDps(i);
                hit += p.SlotHit(i);
                buff += p.MpSufficiency(i) * p.BuffAtkPct(i);
            }
            Assert.That(p.OccupiedCount, Is.EqualTo(5));
            Assert.That(p.PartyHit, Is.EqualTo(hit).Within(1e-12).Percent);
            Assert.That(p.PartyBuff, Is.EqualTo(1 + buff / 100).Within(1e-12));
            Assert.That(p.RatedDps, Is.EqualTo(sum * p.PartyBuff).Within(1e-12).Percent);
            Assert.That(p.SlotHit(2), Is.EqualTo(p.SlotHit(3) * 1.5).Within(1e-12).Percent, "Awakening multiplies its slot only");
            Assert.That(p.SlotHit(0), Is.EqualTo(p.SlotHit(1)), "five equal starters, each with a Common starter weapon (00 decision 111)");
            Assert.That(p.CompanionAtkMult(4), Is.EqualTo(1.25), "comp_e_06 is Epic: COMPANION_ATK_MULT[E] (00 decision 111)");
            Assert.That(p.SlotHit(4), Is.EqualTo(p.SlotHit(1) * 1.25).Within(1e-12).Percent);
        }

        [Test]
        public void Weapons_04AC8_Fixture()
        {
            var sim = Combat.NewSim();
            var s = sim.State;
            s.Weapons.Items.Clear();
            foreach (var x in s.Party.Slots) x.Weapon = null;
            Combat.Own(s.Weapons, "weapon_l_01", 6);   // level 3
            for (int i = 1; i <= 10; i++) Combat.Own(s.Weapons, $"weapon_c_{i:00}", 3);   // level 2
            for (int i = 1; i <= 4; i++) Combat.Own(s.Weapons, $"weapon_r_{i:00}", 1);    // level 1
            s.Party.Slots[0].Weapon = "weapon_l_01";
            sim.Query.MarkDirty();
            Assert.That(sim.Query.Power.WeaponFactor(0), Is.EqualTo(5.512).Within(1e-12));
            Assert.That(sim.Query.Power.WeaponFactor(1), Is.EqualTo(1.312).Within(1e-12), "an unarmed slot gets the party passive only");

            s.Weapons.Items["weapon_l_01"].LimitBreaks = 2;   // +2 effective levels (04 section 5.2)
            sim.Query.MarkDirty();
            Assert.That(sim.Query.Power.EquippedWeaponPct(0), Is.EqualTo(300 * 1.8).Within(1e-9));
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
            s.Party.Slots[0].Skill = "skill_e_02";
            s.Party.Slots[1].Skill = "skill_r_02";
            Combat.Own(s.Skills, "skill_e_02", 10);   // level 4
            sim.Query.MarkDirty();
            Assert.That(sim.Query.Power.SkillDpsPct(0), Is.EqualTo(40 * 1.6).Within(1e-12));
            Assert.That(sim.Query.Power.SkillDpsPct(1), Is.EqualTo(0.0), "unowned counts zero");
        }

        [Test]
        public void FishBoardLayer_FollowsTheBoard_ThroughTheDpsCache()   // 05 section 12 rule 5, OnFishBoardDpsChanged
        {
            var sim = Combat.NewSim(hits: false);
            double before = sim.Query.PartyHit;
            Assert.That(sim.Query.Power.FishBoardDpsPct, Is.EqualTo(sim.FishBoardDpsPct));
            sim.State.Fish.TankCount = 2;
            Assert.That(sim.Apply(new ReleaseMax()).Ok, Is.True);
            double pct = sim.FishBoardDpsPct;
            Assert.That(pct, Is.GreaterThan(0));
            Assert.That(sim.Query.Power.FishBoardDpsPct, Is.EqualTo(pct), "the cache saw the board change");
            Assert.That(sim.Query.PartyHit, Is.EqualTo(before * (1 + pct / 100.0)).Within(1e-9 * before));
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
            var p = sim.Query.Power;
            Assert.That(sim.Query.RatedDpsWithMissiles(10), Is.EqualTo(rated + p.PartyHit * p.Speed * 0.1).Within(1e-12).Percent,
                        "missiles in PartyHit units (05 section 5.1, 03 section 7.1)");
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
        /// The damage the loop deals over <paramref name="ticks"/> against an unkillable enemy, per second, with every slot hit
        /// counted at its expected crit value (03 AC20). Fixtures carry no ATK buffs, so a slot hit's expected value is
        /// <c>SlotHit(slot) * CritFactor</c>.
        /// </summary>
        internal static double MeasuredDps(Simulation sim, int ticks)
        {
            Combat.AdvanceUntil<EnemySpawned>(sim);
            var f = Combat.Fight(sim);
            double total = 0, heroActual = 0, heroExpected = 0;
            long heroHits = 0;
            const double Hp = 1e9;
            for (int i = 0; i < ticks; i++)
            {
                f.EnemyHp = Hp;
                sim.Advance(1);
                total += Hp - f.EnemyHp;
                foreach (var h in sim.DrainEvents().OfType<HitLanded>())
                {
                    heroActual += h.Damage;
                    heroHits++;
                    heroExpected += sim.Query.SlotHit(h.Slot) * sim.Query.CritFactor;
                }
            }
            Assert.That(f.Phase, Is.EqualTo(FightPhase.Fighting));
            Assert.That(heroHits, Is.GreaterThan(0));
            return (total - heroActual + heroExpected) / (ticks / (double)Combat.K.TickHz);
        }

        [Test]
        public void AC20_RatedDps_MatchesMeasuredDps_Within1Pct()   // issue #10; the companion half is CompanionTests (MVP tier)
        {
            var sim = Combat.NewSim();
            var s = sim.State;
            foreach (var id in new[] { "skill_e_02", "skill_r_03", "skill_r_08" }) Combat.Own(s.Skills, id, 3);
            s.Party.Slots[0].Skill = "skill_e_02";   // INSTANT, every party slot fights (the starter party)
            s.Party.Slots[1].Skill = "skill_r_03";   // INSTANT
            s.Party.Slots[3].Skill = "skill_r_08";   // DOT
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
        public void Skill_04AC9_EpicInstant_FourSlotUnitsPerCast_FortyPctOfItsSlotsDamage()
        {
            var sim = Combat.NewSim();
            var s = sim.State;
            Combat.Solo(sim);                         // one attacker whose ability deals no damage
            Combat.Own(s.Skills, "skill_e_02", 1);    // EPIC, INSTANT, dps 40, cooldown 100
            s.Party.Slots[0].Skill = "skill_e_02";
            sim.Query.MarkDirty();
            Assert.That(Combat.Db.Skills["skill_e_02"].DamagePctPerCast, Is.EqualTo(400));

            var spawn = Combat.AdvanceUntil<EnemySpawned>(sim);
            var f = Combat.Fight(sim);
            const double Hp = 1e9;
            f.EnemyHp = Hp;
            sim.Advance(1);   // first Fighting tick: the skill (off cooldown since spawn) fires
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<SkillCast>().Single().Tick, Is.EqualTo(spawn.Tick + 1));
            Assert.That(Hp - f.EnemyHp, Is.EqualTo(4.0 * sim.Query.SlotUnit(0)).Within(1e-6));
            Assert.That(sim.Query.SlotUnit(0), Is.EqualTo(sim.Query.SlotHit(0)), "speed 1, no crit");

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
        public void Skills_OneCastPerSlotPerTick_SlotOrder_CooldownTicksWhileSpawning()
        {
            var sim = Combat.NewSim(hits: false);
            var s = sim.State;
            Combat.Own(s.Skills, "skill_e_05", 1);   // cooldown 60
            Combat.Own(s.Skills, "skill_e_01", 1);   // cooldown 80
            s.Party.Slots[0].Skill = "skill_e_05";
            s.Party.Slots[1].Skill = "skill_e_01";
            var spawn = Combat.AdvanceUntil<EnemySpawned>(sim);
            Combat.Fight(sim).EnemyHp = 1e12;
            var casts = new List<SkillCast>();
            for (int i = 0; i < 4; i++) { sim.Advance(1); casts.AddRange(sim.DrainEvents().OfType<SkillCast>()); }
            Assert.That(casts.Select(c => (c.Tick, c.Slot)), Is.EqualTo(new[] { (spawn.Tick + 1, 0), (spawn.Tick + 1, 1) }),
                        "each slot casts its own skill in the same tick; no party-wide one-cast limit (00 decision 109)");

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
        public void Buffs_ASlotsBuffSkillBuffsThatSlotOnly_StrongestApplies_AWeakerCastNeverShortensIt()   // 04 AC10, 03 AC24
        {
            var sim = Combat.NewSim();
            var s = sim.State;
            Combat.Own(s.Skills, "skill_e_08", 1);   // BUFF 120% for 50 ticks, cooldown 150
            s.Party.Slots[1].Skill = "skill_e_08";
            Combat.AdvanceUntil<EnemySpawned>(sim);
            var f = Combat.Fight(sim);
            f.EnemyHp = 1e12;
            sim.Advance(1);
            Assert.That((f.SlotAtkBuffPct[1], f.SlotAtkBuffTicksLeft[1]), Is.EqualTo((120.0, 49)));
            Assert.That(f.SlotAtkBuffPct.Where((_, i) => i != 1), Is.All.EqualTo(0.0), "the other slots are not buffed");
            Assert.That(f.AtkBuffPct, Is.EqualTo(0.0), "a skill never sets the party buff");
            f.SlotAtkBuffPct[1] = 30; f.SlotAtkBuffTicksLeft[1] = 10;
            s.Combat.SkillCooldowns[1] = 0;
            sim.Advance(1);   // the stronger 120% replaces 30%
            Assert.That((f.SlotAtkBuffPct[1], f.SlotAtkBuffTicksLeft[1]), Is.EqualTo((120.0, 49)));
            f.SlotAtkBuffPct[1] = 500; f.SlotAtkBuffTicksLeft[1] = 5;
            s.Combat.SkillCooldowns[1] = 0;
            sim.Advance(1);   // a weaker cast never shortens a stronger one
            Assert.That((f.SlotAtkBuffPct[1], f.SlotAtkBuffTicksLeft[1]), Is.EqualTo((500.0, 4)));
            f.SlotAtkBuffPct[1] = 120; f.SlotAtkBuffTicksLeft[1] = 40;
            var hits = new List<HitLanded>();
            while (!hits.Any(h => h.Slot == 1) || !hits.Any(h => h.Slot == 2)) { sim.Advance(1); hits.AddRange(sim.DrainEvents().OfType<HitLanded>()); }
            var h1 = hits.First(h => h.Slot == 1);
            var h2 = hits.First(h => h.Slot == 2);
            Assert.That(h1.Damage, Is.EqualTo(sim.Query.SlotHit(1) * 2.2).Within(1e-9), "slot 1's hit carries its buff");
            Assert.That(h2.Damage, Is.EqualTo(sim.Query.SlotHit(2)).Within(1e-9), "slot 2's does not");
            sim.Advance(60);
            Assert.That((f.SlotAtkBuffPct[1], f.SlotAtkBuffTicksLeft[1]), Is.EqualTo((0.0, 0)));
        }
    }
}
