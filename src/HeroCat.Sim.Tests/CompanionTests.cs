using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Power;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>
    /// Companions (issues #161 and #156): content and derived magnitudes (04 section 8.1, 09 section 5.11), slots,
    /// equip commands and the MP_REGEN reserve (04 section 8.2), the inclusive mpSufficiency (03 section 7.1, issue #39),
    /// and the tick loop's MP pool and abilities (03 sections 2.1 and 2.4; 03 AC14, 04 AC11).
    /// </summary>
    public class CompanionTests
    {
        static ContentDb Mvp => TestContent.Mvp;

        // Ids by ability type, checked against the content in Content_PoolMixAndDefaults.
        const string E_DMG1 = "comp_e_01", E_DMG2 = "comp_e_02", E_DMG3 = "comp_e_03", E_DMG4 = "comp_e_04";
        const string E_BUFF_ATK = "comp_e_06", E_BUFF_GOLD = "comp_e_08", E_MP = "comp_e_09";
        const string C_DMG = "comp_c_01", C_MP = "comp_c_17";

        static Simulation Sim(int awakenRank = 3, ContentDb db = null)
        {
            var sim = new Simulation(TestContent.NewState(), db ?? Mvp);
            sim.Apply(new ApplyServerTime(TestContent.T0));
            sim.State.Hero.AwakenRank = awakenRank;
            sim.Query.MarkDirty();
            sim.DrainEvents();
            return sim;
        }

        static void Own(Simulation sim, params string[] ids)
        {
            foreach (var id in ids) sim.State.Companions.Items[id] = new OwnedItem { Copies = 1 };
            sim.Query.MarkDirty();
        }

        /// <summary>Puts <paramref name="ids"/> straight into slots 0.. (owned, cooldowns 0) without the commands.</summary>
        static void Slot(Simulation sim, params string[] ids)
        {
            Own(sim, ids.Where(i => i != null).ToArray());
            var l = sim.State.Loadout.Companions;
            for (int i = 0; i < l.Length; i++) l[i] = i < ids.Length ? ids[i] : null;
            Array.Clear(sim.State.Combat.CompanionCooldowns, 0, sim.State.Combat.CompanionCooldowns.Length);
            sim.Query.MarkDirty();
        }

        static void AssertRejectIsPure(Simulation sim, Command c, RejectCode code, string reason)
        {
            string hash = sim.StateHash();
            var r = sim.Apply(c);
            Assert.That((r.Code, r.Reason), Is.EqualTo((code, reason)), c.ToString());
            Assert.That(sim.StateHash(), Is.EqualTo(hash));
            Assert.That(sim.DrainEvents(), Is.Empty);
        }

        /// <summary>A Fighting non-boss enemy that never dies, so companions fire on every eligible tick.</summary>
        static FightState EndlessFight(Simulation sim)
        {
            Combat.AdvanceUntil<EnemySpawned>(sim);
            var f = sim.State.Combat.Fight;
            f.EnemyHp = f.EnemyMaxHp = 1e300;
            sim.DrainEvents();
            return f;
        }

        /// <summary>CompanionAbilityUsed per slot, one row per minute of game time.</summary>
        static List<int[]> CastsPerMinute(Simulation sim, int minutes)
        {
            var rows = new List<int[]>();
            int ticksPerMinute = sim.Db.Constants.TickHz * (int)Units.SecondsPerMinute;
            for (int m = 0; m < minutes; m++)
            {
                var row = new int[sim.Db.Constants.CompanionSlots];
                for (int t = 0; t < ticksPerMinute; t++)
                {
                    sim.Advance(1);
                    foreach (var e in sim.DrainEvents()) if (e is CompanionAbilityUsed u) row[u.Slot]++;
                }
                rows.Add(row);
            }
            return rows;
        }

        // ============================================================================================ content (04 8.1, 09 5.11)

        [Test]
        public void Content_PoolMixAndDefaults()
        {
            var all = Mvp.Companions.Values.ToList();
            Assert.That(all, Has.Count.EqualTo(50));
            Assert.That(new[] { Rarity.COMMON, Rarity.RARE, Rarity.EPIC, Rarity.LEGENDARY, Rarity.MYTHIC }.Select(r => all.Count(c => c.Rarity == r)),
                        Is.EqualTo(new[] { 18, 14, 10, 6, 2 }));
            int Count(AbilityType t) => all.Count(c => c.AbilityType == t);
            Assert.That(Count(AbilityType.DAMAGE), Is.EqualTo(25), "~50% DAMAGE");
            Assert.That(Count(AbilityType.BUFF_ATK), Is.InRange(9, 11), "~20% BUFF_ATK");
            Assert.That(Count(AbilityType.BUFF_GOLD), Is.InRange(7, 8), "~15% BUFF_GOLD");
            Assert.That(Count(AbilityType.MP_REGEN), Is.InRange(7, 8), "~15% MP_REGEN");
            Assert.That(all.Select(c => (c.MpCost, c.CooldownTicks)), Is.All.EqualTo((10, 100)));
            foreach (var r in new[] { Rarity.COMMON, Rarity.RARE })
                Assert.That(all.Any(c => c.Rarity == r && c.AbilityType == AbilityType.MP_REGEN), $"W011: an MP_REGEN at {r}");
            foreach (var c in all)
                Assert.That(c.DpsPct, Is.EqualTo(Mvp.Rarities[c.Rarity].CompanionDpsPct), c.Id);
            Assert.That(all.Select(c => c.Tier), Is.All.EqualTo(Tier.MVP));

            Assert.That((Mvp.Companions[E_DMG1].AbilityType, Mvp.Companions[E_BUFF_ATK].AbilityType, Mvp.Companions[E_BUFF_GOLD].AbilityType,
                         Mvp.Companions[E_MP].AbilityType, Mvp.Companions[C_DMG].AbilityType, Mvp.Companions[C_MP].AbilityType),
                        Is.EqualTo((AbilityType.DAMAGE, AbilityType.BUFF_ATK, AbilityType.BUFF_GOLD, AbilityType.MP_REGEN, AbilityType.DAMAGE,
                                    AbilityType.MP_REGEN)));
            Assert.That(Mvp.Companions["comp_r_05"].AbilityType, Is.EqualTo(AbilityType.MP_REGEN), "09 section 5.11's example row");
        }

        [Test]
        public void Content_DerivedMagnitudes_04Section8_1()
        {
            Assert.That(Mvp.Companions[E_DMG1].Magnitude, Is.EqualTo(64.0 * 100 / 10), "DAMAGE: dps_pct * cooldown / ATTACK_INTERVAL_TICKS");
            var ba = Mvp.Companions[E_BUFF_ATK];
            Assert.That(ba.Magnitude, Is.EqualTo(64.0 * 100 / ba.DurationTicks.Value), "BUFF_ATK: dps_pct * cooldown / duration");
            var bg = Mvp.Companions[E_BUFF_GOLD];
            Assert.That(bg.Magnitude, Is.EqualTo(2 * 64.0 * 100 / bg.DurationTicks.Value), "BUFF_GOLD: 2 * dps_pct * cooldown / duration");
            Assert.That(Mvp.Companions[E_MP].Magnitude, Is.EqualTo(16.0), "MP_REGEN: E restores 16 (dps_pct / 4)");
            Assert.That(Mvp.Companions[C_MP].Magnitude, Is.EqualTo(4.0));
        }

        [Test]
        public void M0Build_HasNoCompanions_AndADisabledBanner()   // 09 criterion 13
        {
            var m0 = TestContent.Db;
            Assert.That(m0.Companions, Is.Empty);
            Assert.That(m0.Banners[Banner.COMPANION].Enabled, Is.False);
            Assert.That(SummonQuery.PoolIds(m0, Banner.COMPANION), Is.Empty);
            Assert.That(PowerFormula.CompanionDpsPct(m0, TestContent.NewState()), Is.EqualTo(0.0));
            Assert.That(PowerFormula.MpSufficiency(m0, TestContent.NewState()), Is.EqualTo(1.0));

            var sim = TestContent.NewSim();
            sim.Advance(1000);
            Assert.That(sim.State.Combat.Mp, Is.EqualTo(0.0), "no MP pool in a tier without companions");
        }

        [Test]
        public void MvpBuild_EnablesTheCompanionBanner_WithTheFullPool()
        {
            var b = Mvp.Banners[Banner.COMPANION];
            Assert.That((b.Enabled, b.PullCostDiamonds, b.TicketCurrency), Is.EqualTo((true, 300, Currency.TICKET_SUMMON_COMPANION)));
            Assert.That(SummonQuery.PoolIds(Mvp, Banner.COMPANION), Has.Count.EqualTo(50));
            Assert.That(b.Pool[Rarity.MYTHIC], Is.EqualTo(new[] { "comp_m_01", "comp_m_02" }));
        }

        // ============================================================================================ equip (04 8.2)

        [Test]
        public void EquipCompanion_LockedSlotsRejected_UntilTheMatchingAwakeningRank()
        {
            for (int rank = 0; rank <= 3; rank++)
            {
                var sim = Sim(awakenRank: rank);
                Own(sim, E_DMG1, E_DMG2, E_DMG3, E_DMG4);
                string[] ids = { E_DMG1, E_DMG2, E_DMG3, E_DMG4 };
                for (int slot = 0; slot < 4; slot++)
                {
                    if (slot <= rank)
                    {
                        Assert.That(sim.Apply(new EquipCompanion(slot, ids[slot])).Ok, Is.True, $"rank {rank} slot {slot}");
                        sim.DrainEvents();
                    }
                    else
                        AssertRejectIsPure(sim, new EquipCompanion(slot, ids[slot]), RejectCode.Locked, CollectionRejectReasons.COMPANION_SLOT_LOCKED);
                }
                Assert.That(sim.Query.CompanionSlotsUnlocked, Is.EqualTo(rank + 1));
            }
        }

        [Test]
        public void EquipCompanion_Rejects_AndOnSuccess_PublishesItemEquipped_AndStartsTheCooldown()
        {
            var sim = Sim();
            AssertRejectIsPure(sim, new EquipCompanion(-1, E_DMG1), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            AssertRejectIsPure(sim, new EquipCompanion(4, E_DMG1), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            AssertRejectIsPure(sim, new EquipCompanion(0, "skill_e_01"), RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            AssertRejectIsPure(sim, new EquipCompanion(0, null), RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            AssertRejectIsPure(sim, new EquipCompanion(0, E_DMG1), RejectCode.NotOwned, CollectionRejectReasons.ITEM_NOT_OWNED);

            Own(sim, E_DMG1, E_DMG2);
            Assert.That(sim.Apply(new EquipCompanion(1, E_DMG1)).Ok, Is.True);
            var ev = sim.DrainEvents().OfType<ItemEquipped>().Single();
            Assert.That((ev.Banner, ev.Slot, ev.ItemId, ev.PreviousItemId, ev.Auto), Is.EqualTo((Banner.COMPANION, 1, E_DMG1, (string)null, false)));
            Assert.That(sim.State.Combat.CompanionCooldowns[1], Is.EqualTo(100), "a newly slotted companion starts on its full cooldown");
            AssertRejectIsPure(sim, new EquipCompanion(1, E_DMG1), RejectCode.InvalidTarget, CollectionRejectReasons.ALREADY_EQUIPPED);
            AssertRejectIsPure(sim, new EquipCompanion(2, E_DMG1), RejectCode.InvalidTarget, CollectionRejectReasons.COMPANION_IN_OTHER_SLOT);

            Assert.That(sim.Apply(new EquipCompanion(1, E_DMG2)).Ok, Is.True);
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Single().PreviousItemId, Is.EqualTo(E_DMG1));

            AssertRejectIsPure(sim, new UnequipCompanion(0), RejectCode.InvalidTarget, CollectionRejectReasons.SLOT_EMPTY);
            AssertRejectIsPure(sim, new UnequipCompanion(9), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            Assert.That(sim.Apply(new UnequipCompanion(1)).Ok, Is.True);
            Assert.That(sim.State.Loadout.Companions[1], Is.Null);
            Assert.That(sim.State.Combat.CompanionCooldowns[1], Is.EqualTo(0));
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Single().ItemId, Is.Null);
        }

        [Test]
        public void AutoEquip_FillsOpenSlotsBestFirst_AndRespectsLocks()
        {
            var sim = Sim(awakenRank: 1);
            Own(sim, C_DMG, E_DMG1, "comp_l_01", "comp_r_01");
            Assert.That(sim.Apply(new AutoEquipBestCompanions()).Ok, Is.True);
            Assert.That(sim.State.Loadout.Companions, Is.EqualTo(new[] { "comp_l_01", E_DMG1, null, null }), "two open slots at rank 1");
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Count(), Is.EqualTo(2));
            Assert.That(sim.Apply(new AutoEquipBestCompanions()).Ok, Is.True);
            Assert.That(sim.DrainEvents(), Is.Empty, "nothing changed, nothing published");
        }

        [Test]
        public void AutoEquip_ReservesSlot0_ForTheBestMpRegen_WhenThePureDamageLoadoutIsMpStarved()
        {
            var sim = Sim();
            Own(sim, E_DMG1, E_DMG2, E_DMG3, E_DMG4, E_MP, C_MP, E_BUFF_GOLD);
            sim.Apply(new AutoEquipBestCompanions());
            Assert.That(sim.State.Loadout.Companions, Is.EqualTo(new[] { E_MP, E_DMG1, E_DMG2, E_DMG3 }));
            Assert.That(sim.Query.Power.MpSufficiency, Is.EqualTo(156.0 / 180.0).Within(1e-12));

            // Enough Research regen that four damage companions are fully fed: no reserve.
            sim.State.Research.Level["res_mp_regen"] = 4;   // 2 + 4 * 0.5 = 4 MP/s, the demand of four
            sim.Query.MarkDirty();
            sim.Apply(new AutoEquipBestCompanions());
            Assert.That(sim.State.Loadout.Companions, Is.EqualTo(new[] { E_DMG1, E_DMG2, E_DMG3, E_DMG4 }));
            Assert.That(sim.Query.Power.MpSufficiency, Is.EqualTo(1.0));
        }

        [Test]
        public void AutoEquip_SkipsTheReserve_WhenTheBestMpRegenDrainsMoreThanItRestores()
        {
            var sim = Sim();
            Own(sim, E_DMG1, E_DMG2, E_DMG3, E_DMG4, C_MP);   // a level-1 Common restores 4 MP for 10
            sim.Apply(new AutoEquipBestCompanions());
            Assert.That(sim.State.Loadout.Companions, Is.EqualTo(new[] { E_DMG1, E_DMG2, E_DMG3, E_DMG4 }));

            var one = Sim(awakenRank: 0);
            Own(one, E_DMG1, E_MP);
            one.Apply(new AutoEquipBestCompanions());
            Assert.That(one.State.Loadout.Companions, Is.EqualTo(new[] { E_DMG1, null, null, null }), "one open slot: never reserved");
        }

        // ============================================================================================ rated layer (03 7.1, 04 8.2)

        [Test]
        public void CompanionLayer_InclusiveMpSufficiency_Issue39()
        {
            var sim = Sim();
            Slot(sim, E_DMG1, E_DMG2, E_DMG3, E_DMG4);
            var p = sim.Query.Power;
            Assert.That(p.CompanionDpsPct, Is.EqualTo(256.0));
            Assert.That(p.MpSufficiency, Is.EqualTo(0.5), "2 MP/s against 4 MP/s");
            Assert.That(p.Companions, Is.EqualTo(1 + 0.5 * 2.56).Within(1e-12));
            Assert.That(SummonQuery.LayerMult(Mvp, sim.State, Banner.COMPANION), Is.EqualTo(p.Companions));

            Slot(sim, E_MP, E_DMG1, E_DMG2, E_DMG3);
            p = sim.Query.Power;
            Assert.That(p.CompanionDpsPct, Is.EqualTo(192.0), "MP_REGEN's dps_pct counts as 0");
            Assert.That(p.MpSufficiency, Is.EqualTo((2.0 + 0.6) / 3.0).Within(1e-12), "net +6 MP per 10 s counts as regen");

            Slot(sim, C_MP);
            Assert.That(sim.Query.Power.MpSufficiency, Is.EqualTo(1.0), "no demand: 1");

            Slot(sim, E_BUFF_GOLD, E_BUFF_ATK, E_DMG1);
            p = sim.Query.Power;
            Assert.That(p.CompanionDpsPct, Is.EqualTo(128.0), "BUFF_GOLD deals no damage: counts 0; BUFF_ATK counts");
            Assert.That(p.MpSufficiency, Is.EqualTo(2.0 / 3.0).Within(1e-12), "but it draws MP like any other ability");

            Slot(sim, E_DMG1);
            sim.State.Companions.Items[E_DMG1].Copies = 3;   // level 2
            sim.Query.MarkDirty();
            Assert.That(sim.Query.Power.CompanionDpsPct, Is.EqualTo(64.0 * Mvp.LevelMult(2)).Within(1e-12));

            sim.State.Hero.AwakenRank = 0;
            Slot(sim, E_DMG1, E_DMG2);
            sim.State.Companions.Items[E_DMG1].Copies = 3;
            sim.Query.MarkDirty();
            Assert.That(sim.Query.Power.CompanionDpsPct, Is.EqualTo(64.0 * Mvp.LevelMult(2)).Within(1e-12), "a locked slot counts 0");
        }

        // ============================================================================================ tick loop (03 2.1, 2.4)

        [Test]
        public void MpRegen_Step1_CappedAtMpPoolMax_NotRefilledByDeepClone()
        {
            var sim = Sim();
            double perTick = sim.Query.MpRegenPerTick;
            Assert.That(perTick, Is.EqualTo(0.2).Within(1e-15));
            sim.Advance(10);
            Assert.That(sim.State.Combat.Mp, Is.EqualTo(10 * perTick).Within(1e-12));
            sim.Advance(1000);
            Assert.That(sim.State.Combat.Mp, Is.EqualTo(sim.Query.MpPoolMax));

            sim.State.Research.Level["res_mp_pool"] = 2;   // +40
            sim.Query.MarkDirty();
            sim.Advance(300);
            Assert.That(sim.State.Combat.Mp, Is.EqualTo(140.0).Within(1e-9));
        }

        [Test]
        public void AC14_FourDamageCompanions_Fire12PerMinute_And24AfterResMpRegen4()
        {
            var sim = Sim();
            Slot(sim, E_DMG1, E_DMG2, E_DMG3, E_DMG4);
            sim.State.Combat.Mp = sim.Query.MpPoolMax;
            EndlessFight(sim);
            var rows = CastsPerMinute(sim, 6);
            Assert.That(rows[0].Sum(), Is.GreaterThan(12), "the first minute drains the initial 100-MP burst");
            foreach (var r in rows.Skip(2)) Assert.That(r.Sum(), Is.InRange(11, 13), "steady state, MP_REGEN0 = 2");

            sim.State.Research.Level["res_mp_regen"] = 4;
            sim.Query.MarkDirty();
            rows = CastsPerMinute(sim, 4);
            foreach (var r in rows.Skip(1)) Assert.That(r.Sum(), Is.InRange(23, 25), "after res_mp_regen level 4");
        }

        [Test]
        public void AC11_AnEpicMpRegenInSlot0_RaisesTheOtherThree()
        {
            var sim = Sim();
            Slot(sim, E_MP, E_DMG1, E_DMG2, E_DMG3);
            EndlessFight(sim);
            var rows = CastsPerMinute(sim, 12);
            double others = rows.Skip(4).Average(r => r[1] + r[2] + r[3]);
            Assert.That(others, Is.EqualTo(15.6).Within(1.0), "04 criterion 11 as corrected by issue #39");
            Assert.That(others, Is.GreaterThan(12.5), "above the 12 of four damage companions");
            // The rated mpSufficiency (156 / 180) assumes the MP_REGEN fires every 10 s; in the loop it waits for 10 MP too,
            // so it fires every 12 s and the three realise 15.0, 4% under. 04 AC11's "within 1%" clause is an open question
            // for the user (recorded under 04 criterion 11); this test asserts the 15.6 +/- 1 band only.
        }

        [Test]
        public void Abilities_OneCastPerTick_SlotOrder_MpGated_NoCritDraws()
        {
            var sim = Sim();
            Slot(sim, E_DMG1, E_DMG2, E_DMG3, E_DMG4);
            sim.State.Combat.Mp = 25;   // two casts' worth
            var f = EndlessFight(sim);
            var used = new List<CompanionAbilityUsed>();
            for (int i = 0; i < 5; i++)
            {
                sim.Advance(1);
                used.AddRange(sim.DrainEvents().OfType<CompanionAbilityUsed>());
            }
            Assert.That(used.Select(u => u.Slot), Is.EqualTo(new[] { 0, 1 }), "one per tick in slot order, then out of MP");
            Assert.That(used[1].Tick, Is.EqualTo(used[0].Tick + 1));
            Assert.That(sim.State.Combat.CompanionCooldowns.Skip(2), Is.All.EqualTo(0), "waiting abilities keep a zero cooldown");
            Assert.That(sim.State.Combat.CompanionCooldowns[0], Is.EqualTo(96));
            Assert.That(sim.State.Rng[RngStreams.Crit].Counter, Is.EqualTo(sim.State.Stats.HeroHits), "companions draw no crit");

            double before = f.EnemyHp;
            sim.State.Combat.Mp = 10;
            sim.Advance(1);
            var hit = sim.DrainEvents().OfType<CompanionAbilityUsed>().Single();
            Assert.That((hit.Slot, hit.CompanionId), Is.EqualTo((2, E_DMG3)));
            Assert.That(hit.Mp, Is.EqualTo(0.2).Within(1e-12), "the pool after the cast");
        }

        [Test]
        public void Damage_IsCompanionUnitTimesMagnitude_ScaledByLevelMult()
        {
            var sim = Sim();
            sim.EmitHitLanded = true;
            Slot(sim, E_DMG1);
            sim.State.Companions.Items[E_DMG1].Copies = 6;   // level 3
            sim.State.Combat.Mp = 50;
            var f = EndlessFight(sim);
            double hp = f.EnemyHp = 1e12;
            sim.Advance(1);
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<CompanionAbilityUsed>().Count(), Is.EqualTo(1));
            double hero = ev.OfType<HitLanded>().Sum(h => h.Damage);
            Assert.That(hp - f.EnemyHp - hero, Is.EqualTo(sim.Query.CompanionUnit * 640.0 * Mvp.LevelMult(3) / 100).Within(1e-3));
        }

        [Test]
        public void AC20_RatedDps_MatchesMeasured_WithSkillsAndTwoDamageCompanionsAtFullMp()   // 03 AC20, issue #10
        {
            var sim = Sim();
            sim.EmitHitLanded = true;
            var s = sim.State;
            foreach (var id in new[] { "skill_e_02", "skill_r_03", "skill_r_08" }) Combat.Own(s.Skills, id, 3);
            s.Loadout.Skills[0] = "skill_e_02";   // INSTANT
            s.Loadout.Skills[1] = "skill_r_03";   // INSTANT
            s.Loadout.Skills[2] = "skill_r_08";   // DOT
            Slot(sim, E_DMG1, E_DMG2);            // 2 MP/s of demand against MP_REGEN0 = 2: full uptime
            s.Combat.Mp = sim.Query.MpPoolMax;
            s.Fish.Cells[0] = 3;
            s.Training.Level[(int)Stat.ATK_SPD] = 50;
            s.Training.Level[(int)Stat.CRIT_RATE] = 120;
            s.Training.Level[(int)Stat.CRIT_DMG] = 80;
            sim.Query.MarkDirty();
            Assert.That(sim.Query.Power.MpSufficiency, Is.EqualTo(1.0));
            Assert.That(sim.Query.Power.Companions, Is.EqualTo(2.28).Within(1e-12));
            Assert.That(PowerTests.MeasuredDps(sim, 100_000), Is.EqualTo(sim.Query.RatedDps).Within(1).Percent);
        }

        [Test]
        public void LockedSlots_AreSkippedByTheTickLoop()
        {
            var sim = Sim(awakenRank: 0);
            Slot(sim, null, E_DMG1);
            sim.State.Combat.Mp = 50;
            EndlessFight(sim);
            sim.Advance(200);
            Assert.That(sim.DrainEvents().OfType<CompanionAbilityUsed>(), Is.Empty);
        }

        [Test]
        public void BuffAtk_SharesTheSkillBuffSlot_BuffGold_MultipliesKillGold_MpRegen_Restores()
        {
            var sim = Sim();
            Slot(sim, E_BUFF_ATK);
            sim.State.Combat.Mp = 50;
            var f = EndlessFight(sim);
            sim.Advance(1);
            var ba = Mvp.Companions[E_BUFF_ATK];
            Assert.That((f.AtkBuffPct, f.AtkBuffTicksLeft), Is.EqualTo((ba.Magnitude, ba.DurationTicks.Value - 1)));
            f.AtkBuffPct = 1000; f.AtkBuffTicksLeft = 5;   // a stronger buff active: the companion's weaker one does not replace it
            sim.State.Combat.CompanionCooldowns[0] = 0;
            sim.Advance(1);
            Assert.That((f.AtkBuffPct, f.AtkBuffTicksLeft), Is.EqualTo((1000.0, 4)));

            Slot(sim, E_BUFF_GOLD);
            sim.State.Combat.Mp = 50;
            sim.Advance(1);
            var bg = Mvp.Companions[E_BUFF_GOLD];
            Assert.That((f.GoldBuffPct, f.GoldBuffTicksLeft), Is.EqualTo((bg.Magnitude, bg.DurationTicks.Value - 1)));
            double goldBefore = sim.State.Wallet.Gold;
            double expected = sim.Query.GoldPerKill(1) * sim.Query.GoldMult * (1 + bg.Magnitude / 100);
            f.EnemyHp = 0;
            sim.Advance(1);
            Assert.That(sim.State.Wallet.Gold - goldBefore, Is.EqualTo(expected).Within(1e-9));
            sim.Advance(bg.DurationTicks.Value);
            Assert.That((f.GoldBuffPct, f.GoldBuffTicksLeft), Is.EqualTo((0.0, 0)));

            Slot(sim, E_MP);
            EndlessFight(sim);
            sim.State.Combat.CompanionCooldowns[0] = 0;
            sim.State.Combat.Mp = 20;
            sim.Advance(1);
            Assert.That(sim.State.Combat.Mp, Is.EqualTo(20 + 0.2 - 10 + 16).Within(1e-12));
            sim.State.Combat.Mp = sim.Query.MpPoolMax;
            sim.State.Combat.CompanionCooldowns[0] = 0;
            sim.Advance(1);
            Assert.That(sim.State.Combat.Mp, Is.EqualTo(sim.Query.MpPoolMax), "restores never pass MpPoolMax");
        }

        [Test]
        public void DeepClone_MidFight_ContinuesIdentically()
        {
            var sim = Sim();
            Slot(sim, E_MP, E_DMG1, E_BUFF_ATK, E_BUFF_GOLD);
            sim = new Simulation(sim.State, Mvp);   // re-derive the OWN_COMPANIONS ladder the hand-filled collection skipped, as a load does
            sim.Advance(500);
            var clone = new Simulation(StateCopy.DeepClone(sim.State), Mvp);
            Assert.That(clone.StateHash(), Is.EqualTo(sim.StateHash()));
            sim.Advance(3000);
            clone.Advance(3000);
            Assert.That(clone.StateHash(), Is.EqualTo(sim.StateHash()));
        }

        // ============================================================================================ collections

        [Test]
        public void LimitBreak_AppliesToCompanions()
        {
            var sim = Sim();
            Own(sim, E_DMG1);
            sim.State.Wallet.RockCandy = 1000;
            Assert.That(sim.Query.NextLimitBreakCost(E_DMG1), Is.EqualTo(Mvp.Rarities[Rarity.EPIC].LimitBreakCost));
            Assert.That(sim.Apply(new LimitBreak(E_DMG1)).Ok, Is.True);
            Assert.That(sim.State.Companions.Items[E_DMG1].LimitBreaks, Is.EqualTo(1));
        }

        [Test]
        public void CollectionVm_ShowsCompanions_WithSlots()
        {
            var sim = Sim();
            Slot(sim, E_DMG2);
            var vm = SummonQuery.Collection(Mvp, sim.State, Banner.COMPANION);
            Assert.That((vm.PoolCount, vm.OwnedCount), Is.EqualTo((50, 1)));
            var item = vm.Items.Single(i => i.Id == E_DMG2);
            Assert.That((item.Equipped, item.Slot, item.ValueNow, item.Rarity), Is.EqualTo((true, (int?)0, 64.0, Rarity.EPIC)));
        }
    }
}
