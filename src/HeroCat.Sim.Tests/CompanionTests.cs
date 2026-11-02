using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Profile;
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
    /// Companions (issues #161 and #156; 00 decisions 109, 110): content and derived magnitudes (04 section 8.1, 09 section
    /// 5.11), the five always-open party slots and their equip commands (04 section 8.2), the per-pool mpSufficiency (03
    /// section 7.1), and the tick loop's per-slot MP pools and abilities with party-wide supports (03 sections 2.1 and 2.4;
    /// 03 AC14, 04 AC11).
    /// </summary>
    public class CompanionTests
    {
        static ContentDb Mvp => TestContent.Mvp;

        // Ids by ability type, checked against the content in Content_PoolMixAndDefaults.
        const string E_DMG1 = "comp_e_01", E_DMG2 = "comp_e_02", E_DMG3 = "comp_e_03", E_DMG4 = "comp_e_04";
        const string E_BUFF_ATK = "comp_e_06", E_BUFF_GOLD = "comp_e_08", E_MP = "comp_e_09";
        const string C_DMG = "comp_c_01", C_MP = "comp_c_17";

        static Simulation Sim(ContentDb db = null)
        {
            var sim = new Simulation(TestContent.NewState(), db ?? Mvp) { CombatEvents = CombatEventMask.Default | CombatEventMask.CompanionAbilityUsed | CombatEventMask.SkillCast };
            sim.Apply(new ApplyServerTime(TestContent.T0));
            sim.Query.MarkDirty();
            sim.DrainEvents();
            return sim;
        }

        static void Own(Simulation sim, params string[] ids)
        {
            foreach (var id in ids) sim.State.Companions.Items[id] = new OwnedItem { Copies = 1 };
            sim.Query.MarkDirty();
        }

        /// <summary>Puts <paramref name="ids"/> straight into party slots 0.. (owned, cooldowns 0) without the commands; the rest empty.</summary>
        static void Slot(Simulation sim, params string[] ids)
        {
            Own(sim, ids.Where(i => i != null).ToArray());
            var l = sim.State.Party.Slots;
            for (int i = 0; i < l.Length; i++) l[i].Companion = i < ids.Length ? ids[i] : null;
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
                var row = new int[sim.Db.Constants.PartySlots];
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
            Assert.That(all.Select(c => c.Tier), Is.All.EqualTo(Tier.M0), "00 decision 107");

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
        public void M0Build_HasTheCompanionPool_AndAnEnabledBanner()   // 00 decision 107: companions are M0
        {
            var m0 = TestContent.Db;
            Assert.That(m0.Companions, Has.Count.EqualTo(50));
            Assert.That(m0.Banners[Banner.COMPANION].Enabled, Is.True);
            Assert.That(SummonQuery.PoolIds(m0, Banner.COMPANION), Has.Count.EqualTo(50));
            Assert.That(m0.IsUnlocked("COMPANIONS", TestContent.NewState()), Is.True);
        }

        [Test]
        public void TierFilter_ACompanionPoolAboveTheBuildTier_FailsTheStarterPartyRule()   // 09 criterion 13, E066
        {
            var dtos = ContentLoader.LoadDtos(new FileContentSource(TestContent.Dir));
            foreach (var c in dtos.Companions) c.Tier = "MVP";
            foreach (var bn in dtos.Banners) if (bn.Id == "COMPANION") bn.Tier = "MVP";
            var ex = Assert.Throws<ContentBuildException>(() => ContentDb.Build(dtos, Tier.M0));
            Assert.That(ex.Errors.Select(e => e.Code), Does.Contain("E066").Or.Contain("E022"),
                        "every tier needs its five starter companions (00 decision 109)");
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
        public void StarterParty_FiveCommonsInSlotsZeroToFour_AndTheWeaponInSlotZero()   // 00 decision 109, 02 section 3.5
        {
            var sim = Sim();
            var slots = sim.State.Party.Slots;
            Assert.That(slots.Select(x => x.Companion), Is.EqualTo(Mvp.TutorialCompanionIds));
            Assert.That(Mvp.TutorialCompanionIds, Has.Count.EqualTo(Mvp.Constants.PartySlots));
            Assert.That(Mvp.TutorialCompanionIds.Select(id => Mvp.Companions[id].Rarity), Is.All.EqualTo(Rarity.COMMON));
            Assert.That(slots.Select(x => x.Weapon), Is.EqualTo(new[] { Mvp.TutorialWeaponId, null, null, null, null }));
            Assert.That(sim.Query.Power.OccupiedCount, Is.EqualTo(5));
        }

        [Test]
        public void EverySlotIsOpen_AtRankZero()   // 00 decision 109: no Awakening gate on party slots
        {
            var sim = Sim();
            Own(sim, E_DMG1, E_DMG2, E_DMG3, E_DMG4, E_MP);
            string[] ids = { E_DMG1, E_DMG2, E_DMG3, E_DMG4, E_MP };
            for (int slot = 0; slot < 5; slot++) Assert.That(sim.Apply(new EquipCompanion(slot, ids[slot])).Ok, Is.True, "slot " + slot);
            Assert.That(sim.State.Party.Slots.Select(x => x.Companion), Is.EqualTo(ids));
        }

        [Test]
        public void EquipCompanion_Rejects_AndOnSuccess_PublishesItemEquipped_AndKeepsTheSlotsProgress()
        {
            var sim = Sim();
            Slot(sim);   // an empty party
            AssertRejectIsPure(sim, new EquipCompanion(-1, E_DMG1), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            AssertRejectIsPure(sim, new EquipCompanion(5, E_DMG1), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            AssertRejectIsPure(sim, new EquipCompanion(0, "skill_e_01"), RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            AssertRejectIsPure(sim, new EquipCompanion(0, null), RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            AssertRejectIsPure(sim, new EquipCompanion(0, E_DMG1), RejectCode.NotOwned, CollectionRejectReasons.ITEM_NOT_OWNED);

            Own(sim, E_DMG1, E_DMG2);
            var x = sim.State.Party.Slots[1];
            x.AwakenRank = 2; x.Weapon = Mvp.TutorialWeaponId;
            sim.State.Combat.Mp[1] = 42;
            Assert.That(sim.Apply(new EquipCompanion(1, E_DMG1)).Ok, Is.True);
            var ev = sim.DrainEvents().OfType<ItemEquipped>().Single();
            Assert.That((ev.Banner, ev.Slot, ev.ItemId, ev.PreviousItemId, ev.Auto), Is.EqualTo((Banner.COMPANION, 1, E_DMG1, (string)null, false)));
            Assert.That(sim.State.Combat.CompanionCooldowns[1], Is.EqualTo(100), "a newly slotted companion starts on its full cooldown");
            AssertRejectIsPure(sim, new EquipCompanion(1, E_DMG1), RejectCode.InvalidTarget, CollectionRejectReasons.ALREADY_EQUIPPED);
            AssertRejectIsPure(sim, new EquipCompanion(2, E_DMG1), RejectCode.InvalidTarget, CollectionRejectReasons.COMPANION_IN_OTHER_SLOT);

            Assert.That(sim.Apply(new EquipCompanion(1, E_DMG2)).Ok, Is.True);
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Single().PreviousItemId, Is.EqualTo(E_DMG1));
            Assert.That((x.AwakenRank, x.Weapon, sim.State.Combat.Mp[1]), Is.EqualTo((2, Mvp.TutorialWeaponId, 42.0)),
                        "the slot keeps its rank, weapon and MP pool across a swap (00 decision 109)");

            AssertRejectIsPure(sim, new UnequipCompanion(9), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            Assert.That(sim.Apply(new UnequipCompanion(0)).Ok, Is.True, "an already empty slot is accepted");
            Assert.That(sim.DrainEvents(), Is.Empty, "with no event");
            Assert.That(sim.Apply(new UnequipCompanion(1)).Ok, Is.True);
            Assert.That(sim.State.Party.Slots[1].Companion, Is.Null);
            Assert.That(sim.State.Combat.CompanionCooldowns[1], Is.EqualTo(0));
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Single().ItemId, Is.Null);
        }

        [Test]
        public void AutoEquip_FillsEverySlot_DamageAndBuffAtkFirst()
        {
            var sim = Sim();
            Slot(sim);
            sim.State.Companions.Items.Clear();
            Own(sim, C_DMG, E_DMG1, "comp_l_01", "comp_r_01", E_BUFF_GOLD, E_BUFF_ATK);
            sim.State.Research.Level["res_mp_regen"] = 1;   // 1 MP/s per pool: no pool runs short, so no MP_REGEN swap
            sim.Query.MarkDirty();
            Assert.That(sim.Apply(new AutoEquipBestCompanions()).Ok, Is.True);
            var picked = sim.State.Party.Slots.Select(x => x.Companion).ToArray();
            Assert.That(picked, Is.EquivalentTo(new[] { "comp_l_01", E_DMG1, E_BUFF_ATK, "comp_r_01", C_DMG }),
                        "the five best DAMAGE and BUFF_ATK companions; BUFF_GOLD only fills");
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Count(), Is.EqualTo(5));
            Assert.That(sim.Apply(new AutoEquipBestCompanions()).Ok, Is.True);
            Assert.That(sim.DrainEvents(), Is.Empty, "nothing changed, nothing published");
        }

        [Test]
        public void AutoEquip_SwapsInAnMpRegen_WhenItRaisesRatedDps()
        {
            var sim = Sim();
            Slot(sim);
            sim.State.Companions.Items.Clear();
            Own(sim, E_DMG1, E_DMG2, E_DMG3, E_DMG4, "comp_e_05", E_MP, E_BUFF_GOLD);
            sim.Apply(new AutoEquipBestCompanions());
            var picked = sim.State.Party.Slots.Select(x => x.Companion).ToArray();
            Assert.That(picked, Does.Contain(E_MP), "an Epic MP_REGEN restores 16 to every pool: four fed companions beat five starved ones");
            for (int i = 0; i < 5; i++) Assert.That(sim.Query.MpSufficiency(i), Is.EqualTo(1.0));
        }

        [Test]
        public void AutoEquip_KeepsAnMpRegen_OnlyWhileItRaisesRatedDps()
        {
            var sim = Sim();
            Slot(sim);
            sim.State.Companions.Items.Clear();
            Own(sim, E_DMG1, E_DMG2, E_DMG3, E_DMG4, "comp_e_05", C_MP);   // a level-1 Common restores 4 MP for 10
            sim.Apply(new AutoEquipBestCompanions());
            Assert.That(sim.State.Party.Slots.Select(x => x.Companion), Does.Contain(C_MP),
                        "even a net-draining Common lifts four starved pools, and it still auto-attacks (00 decision 109)");

            sim.State.Research.Level["res_mp_regen"] = 1;   // 1 MP/s per pool: nobody is short, so no restorer is swapped in
            sim.Query.MarkDirty();
            sim.Apply(new AutoEquipBestCompanions());
            Assert.That(sim.State.Party.Slots.Select(x => x.Companion), Does.Not.Contain(C_MP));
        }

        // ============================================================================================ rated layer (03 7.1, 04 8.2)

        [Test]
        public void PerPoolMpSufficiency_03Section7_1()
        {
            var sim = Sim();
            Slot(sim, E_DMG1, E_DMG2, E_DMG3, E_DMG4);
            var p = sim.Query.Power;
            for (int i = 0; i < 4; i++) Assert.That(p.MpSufficiency(i), Is.EqualTo(0.5), "0.5 MP/s per pool against 1 MP/s (00 decision 110)");
            Assert.That(p.MpSufficiency(4), Is.EqualTo(1.0), "an empty slot reads 1");
            Assert.That(p.DamagePct(0), Is.EqualTo(64.0));
            Assert.That(p.OwnFactor(0), Is.EqualTo(1 + 0.5 * 0.64).Within(1e-12));

            Slot(sim, E_MP, E_DMG1, E_DMG2, E_DMG3);
            p = sim.Query.Power;
            Assert.That(p.DamagePct(0), Is.EqualTo(0.0), "MP_REGEN deals no damage");
            for (int i = 0; i < 4; i++)
                Assert.That(p.MpSufficiency(i), Is.EqualTo(1.0), "16 MP per 10 s into every pool, its own included, covers 1 MP/s");

            Slot(sim, C_MP, E_DMG1);
            p = sim.Query.Power;
            double uptime = 0.5 / ((10 - 4) / 10.0), restore = uptime * 4 / 10.0;
            Assert.That(p.MpSufficiency(1), Is.EqualTo(0.5 + restore).Within(1e-12), "a net-draining restorer at what its own regen sustains");
            Assert.That(p.MpSufficiency(0), Is.EqualTo(0.5 + restore).Within(1e-12), "its own pool reads the same expression");

            Slot(sim, E_BUFF_GOLD, E_BUFF_ATK, E_DMG1);
            p = sim.Query.Power;
            Assert.That((p.DamagePct(0), p.BuffAtkPct(1), p.DamagePct(2)), Is.EqualTo((0.0, 64.0, 64.0)), "BUFF_GOLD counts 0; BUFF_ATK is the party term");
            Assert.That(p.MpSufficiency(0), Is.EqualTo(0.5), "but BUFF_GOLD draws MP like any other ability");
            Assert.That(p.PartyBuff, Is.EqualTo(1 + 0.5 * 0.64).Within(1e-12));

            Slot(sim, E_DMG1);
            sim.State.Companions.Items[E_DMG1].Copies = 3;   // level 2
            sim.Query.MarkDirty();
            Assert.That(sim.Query.Power.DamagePct(0), Is.EqualTo(64.0 * Mvp.LevelMult(2)).Within(1e-12));
        }

        [Test]
        public void LayerMult_IsPartyDpsOverPartyDpsWithoutTheCompanionTerms()   // 04 section 11
        {
            var sim = Sim();
            Slot(sim, E_DMG1, E_BUFF_ATK, E_DMG2);
            var p = sim.Query.Power;
            double without = 0;
            for (int i = 0; i < 5; i++) if (p.Occupied(i)) without += p.SlotDps(i) / p.OwnFactor(i);
            Assert.That(SummonQuery.LayerMult(Mvp, sim.State, Banner.COMPANION), Is.EqualTo(p.AutoDps / without).Within(1e-12));
        }

        // ============================================================================================ tick loop (03 2.1, 2.4)

        [Test]
        public void MpRegen_Step1_EveryPool_CappedAtMpPoolMax()
        {
            var sim = Sim();
            Slot(sim);   // an empty party: nobody casts, and every pool still regenerates
            double perTick = sim.Query.MpRegenPerTick;
            Assert.That(perTick, Is.EqualTo(0.05).Within(1e-15), "MP_REGEN0 = 0.5 per second (00 decision 110)");
            sim.Advance(10);
            Assert.That(sim.State.Combat.Mp, Is.All.EqualTo(10 * perTick).Within(1e-12));
            sim.Advance(5000);
            Assert.That(sim.State.Combat.Mp, Is.All.EqualTo(sim.Query.MpPoolMax));

            sim.State.Research.Level["res_mp_pool"] = 2;   // +40, every pool
            sim.Query.MarkDirty();
            sim.Advance(1200);
            Assert.That(sim.State.Combat.Mp, Is.All.EqualTo(140.0).Within(1e-9));
        }

        [Test]
        public void AC14_FourDamageCompanions_EachFireAtTheirPoolsRate_AndTwiceAsOftenAfterResMpRegen1()
        {
            var sim = Sim();
            Slot(sim, E_DMG1, E_DMG2, E_DMG3, E_DMG4);
            EndlessFight(sim);
            var rows = CastsPerMinute(sim, 6);
            foreach (var r in rows.Skip(2))
            {
                Assert.That(r.Take(4), Is.All.InRange(2, 4), "steady state: 0.5 MP/s for 10 MP a cast is one every 20 s per pool");
                Assert.That(r.Sum(), Is.InRange(11, 13));
            }

            sim.State.Research.Level["res_mp_regen"] = 1;   // 1 MP/s per pool: full uptime, one per 10 s each
            sim.Query.MarkDirty();
            rows = CastsPerMinute(sim, 4);
            foreach (var r in rows.Skip(1)) Assert.That(r.Sum(), Is.InRange(23, 25), "after res_mp_regen level 1");
        }

        [Test]
        public void AC11_AnEpicMpRegen_FeedsEveryOtherPool()
        {
            var sim = Sim();
            Slot(sim, E_MP, E_DMG1, E_DMG2, E_DMG3, E_DMG4);
            EndlessFight(sim);
            var rows = CastsPerMinute(sim, 12);
            double others = rows.Skip(4).Average(r => r[1] + r[2] + r[3] + r[4]);
            Assert.That(others, Is.EqualTo(24.0).Within(1.0), "each of the four casts every 10 s once the restores arrive");
            Assert.That(rows.Skip(4).Average(r => r[0]), Is.EqualTo(6.0).Within(0.5), "the restorer pays its own cost from its own restore");
        }

        [Test]
        public void Abilities_EverySlotCastsInTheSameTick_FromItsOwnPool_NoCritDraws()
        {
            var sim = Sim();
            Slot(sim, E_DMG1, E_DMG2, E_DMG3, E_DMG4);
            var mp = sim.State.Combat.Mp;
            mp[0] = 25; mp[1] = 15; mp[2] = 5; mp[3] = 10;
            var f = EndlessFight(sim);
            mp[0] = 25; mp[1] = 15; mp[2] = 5; mp[3] = 10;
            sim.Advance(1);
            var used = sim.DrainEvents().OfType<CompanionAbilityUsed>().ToList();
            Assert.That(used.Select(u => u.Slot), Is.EqualTo(new[] { 0, 1, 3 }), "one tick, slot order, each from its own pool; slot 2 waits");
            Assert.That(used[0].Mp, Is.EqualTo(25 + 0.05 - 10).Within(1e-12), "the caster's own pool after the cast");
            Assert.That(sim.State.Combat.CompanionCooldowns[2], Is.EqualTo(0), "a waiting ability keeps a zero cooldown");
            Assert.That(sim.State.Combat.CompanionCooldowns[0], Is.EqualTo(100));
            Assert.That(sim.State.Rng[RngStreams.Crit].Counter, Is.EqualTo(sim.State.Stats.PartyHits), "companion abilities draw no crit");
        }

        [Test]
        public void Damage_IsTheSlotsCompanionUnitTimesMagnitude_ScaledByLevelMult()
        {
            var sim = Sim();
            sim.EmitHitLanded = true;
            Slot(sim, null, null, E_DMG1);
            sim.State.Companions.Items[E_DMG1].Copies = 6;   // level 3
            var f = EndlessFight(sim);
            sim.State.Combat.Mp[2] = 50;
            double hp = f.EnemyHp = 1e12;
            sim.Advance(1);
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<CompanionAbilityUsed>().Single().Slot, Is.EqualTo(2));
            double hits = ev.OfType<HitLanded>().Sum(h => h.Damage);
            Assert.That(hp - f.EnemyHp - hits, Is.EqualTo(sim.Query.CompanionUnit(2) * 640.0 * Mvp.LevelMult(3) / 100).Within(1e-3));
        }

        [Test]
        public void AC20_RatedDps_MatchesMeasured_OnAFullParty_WithSkillsAndDamageCompanionsAtFullMp()   // 03 AC20, issue #10
        {
            var sim = Sim();
            sim.EmitHitLanded = true;
            var s = sim.State;
            foreach (var id in new[] { "skill_e_02", "skill_r_03", "skill_r_08" }) Combat.Own(s.Skills, id, 3);
            foreach (var id in new[] { "weapon_r_01", "weapon_e_01" }) Combat.Own(s.Weapons, id, 2);
            Slot(sim, E_DMG1, E_DMG2, C_DMG, E_DMG3, E_DMG4);
            var x = s.Party.Slots;
            x[0].Skill = "skill_e_02"; x[1].Skill = "skill_r_03"; x[3].Skill = "skill_r_08";   // INSTANT, INSTANT, DOT
            x[1].Weapon = "weapon_r_01"; x[4].Weapon = "weapon_e_01";
            x[2].AwakenRank = 2;
            s.Research.Level["res_mp_regen"] = 1;   // 1 MP/s per pool against 1 MP/s of demand: full uptime
            for (int i = 0; i < 5; i++) s.Combat.Mp[i] = 100;
            s.Fish.Cells[0] = 3;
            s.Training.Level[(int)Stat.ATK_SPD] = 50;
            s.Training.Level[(int)Stat.CRIT_RATE] = 120;
            s.Training.Level[(int)Stat.CRIT_DMG] = 80;
            sim.Query.MarkDirty();
            for (int i = 0; i < 5; i++) Assert.That(sim.Query.MpSufficiency(i), Is.EqualTo(1.0));
            Assert.That(PowerTests.MeasuredDps(sim, 100_000), Is.EqualTo(sim.Query.RatedDps).Within(1).Percent);
        }

        [Test]
        public void EmptySlots_DoNothing()
        {
            var sim = Sim();
            sim.EmitHitLanded = true;
            Slot(sim, null, E_DMG1);
            EndlessFight(sim);
            sim.State.Combat.Mp[0] = 50;
            sim.Advance(200);
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<CompanionAbilityUsed>().Select(u => u.Slot), Is.All.EqualTo(1));
            Assert.That(ev.OfType<HitLanded>().Select(h => h.Slot), Is.All.EqualTo(1));
        }

        [Test]
        public void BuffAtk_IsPartyWide_BuffGold_MultipliesKillGold_MpRegen_RestoresEveryPool()
        {
            var sim = Sim();
            Slot(sim, E_BUFF_ATK);
            var f = EndlessFight(sim);
            sim.State.Combat.Mp[0] = 50;
            sim.Advance(1);
            var ba = Mvp.Companions[E_BUFF_ATK];
            Assert.That((f.AtkBuffPct, f.AtkBuffTicksLeft), Is.EqualTo((ba.Magnitude, ba.DurationTicks.Value - 1)));
            Assert.That(f.SlotAtkBuffPct, Is.All.EqualTo(0.0), "the party buff, not a slot's");
            f.AtkBuffPct = 1000; f.AtkBuffTicksLeft = 5;   // a stronger buff active: the companion's weaker one does not replace it
            sim.State.Combat.CompanionCooldowns[0] = 0;
            sim.Advance(1);
            Assert.That((f.AtkBuffPct, f.AtkBuffTicksLeft), Is.EqualTo((1000.0, 4)));

            Slot(sim, E_BUFF_GOLD);
            sim.State.Combat.Mp[0] = 50;
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

            Slot(sim, E_MP, E_DMG1);
            sim.DrainEvents();   // a stale EnemySpawned would end EndlessFight's wait early
            EndlessFight(sim);
            sim.State.Combat.CompanionCooldowns[0] = 0;
            sim.State.Combat.CompanionCooldowns[1] = 99;
            var mp = sim.State.Combat.Mp;
            mp[0] = 20; mp[1] = 3; mp[2] = 0; mp[3] = 0; mp[4] = sim.Query.MpPoolMax;
            sim.Advance(1);
            Assert.That(mp[0], Is.EqualTo(20 + 0.05 - 10 + 16).Within(1e-12), "its own pool: cost, then the restore");
            Assert.That(mp[1], Is.EqualTo(3 + 0.05 + 16).Within(1e-12), "every other pool gets the restore");
            Assert.That(mp[2], Is.EqualTo(0.05 + 16).Within(1e-12), "an empty slot's pool too");
            Assert.That(mp[4], Is.EqualTo(sim.Query.MpPoolMax), "restores never pass MpPoolMax");
        }

        [Test]
        public void DeepClone_MidFight_ContinuesIdentically()
        {
            var sim = Sim();
            Slot(sim, E_MP, E_DMG1, E_BUFF_ATK, E_BUFF_GOLD, E_DMG2);
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
            Slot(sim, null, E_DMG2);
            var vm = SummonQuery.Collection(Mvp, sim.State, Banner.COMPANION);
            Assert.That((vm.PoolCount, vm.OwnedCount), Is.EqualTo((50, 6)), "the five starters plus one");
            var item = vm.Items.Single(i => i.Id == E_DMG2);
            Assert.That((item.Equipped, item.Slot, item.ValueNow, item.Rarity), Is.EqualTo((true, (int?)1, 64.0, Rarity.EPIC)));
        }
    }
}
