using System.Linq;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Power;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>04 sections 4, 6.3, 7.2 and 11: the equip commands and the collection / summon-screen queries.</summary>
    public class CollectionTests
    {
        static ContentDb Db => TestContent.Db;

        static Simulation Sim()
        {
            var sim = TestContent.NewSim();
            sim.DrainEvents();
            return sim;
        }

        static void Own(CollectionState c, string id, int copies = 1) => c.Items[id] = new OwnedItem { Copies = copies };

        static void AssertRejectIsPure(Simulation sim, Command c, RejectCode code, string reason)
        {
            string hash = sim.StateHash();
            var r = sim.Apply(c);
            Assert.That((r.Code, r.Reason), Is.EqualTo((code, reason)), c.ToString());
            Assert.That(sim.StateHash(), Is.EqualTo(hash), c.ToString());
            Assert.That(sim.DrainEvents(), Is.Empty, c.ToString());
        }

        // ------------------------------------------------------------------------------------------------ weapons (6.3)

        [Test]
        public void NewProfile_OwnsTheFiveStarterWeapons_InSlotsZeroToFour()   // #93, 00 decisions 109, 111
        {
            var s = TestContent.NewState();
            Assert.That(Db.TutorialWeaponIds, Is.EqualTo(new[] { "weapon_c_01", "weapon_c_02", "weapon_c_03", "weapon_c_04", "weapon_c_05" }));
            Assert.That(Db.TutorialWeaponId, Is.EqualTo(Db.TutorialWeaponIds[0]));
            Assert.That(s.Party.Slots.Select(x => x.Weapon), Is.EqualTo(Db.TutorialWeaponIds), "no unarmed slot");
            foreach (var id in Db.TutorialWeaponIds) Assert.That(CollectionRules.Owns(s.Weapons, id), Is.True);
        }

        [Test]
        public void EquipWeapon_PerSlot_Rejects_AndPublishesItemEquipped()   // #93, 04 section 6.3
        {
            var sim = Sim();
            string tut = sim.State.Party.Slots[0].Weapon;
            string e = Db.Pool(Banner.WEAPON, Rarity.EPIC)[0];
            AssertRejectIsPure(sim, new EquipWeapon(-1, e), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            AssertRejectIsPure(sim, new EquipWeapon(Db.Constants.PartySlots, e), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            AssertRejectIsPure(sim, new EquipWeapon(1, "no_such_weapon"), RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            AssertRejectIsPure(sim, new EquipWeapon(1, null), RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            AssertRejectIsPure(sim, new EquipWeapon(1, e), RejectCode.NotOwned, CollectionRejectReasons.ITEM_NOT_OWNED);
            AssertRejectIsPure(sim, new EquipWeapon(0, tut), RejectCode.InvalidTarget, CollectionRejectReasons.ALREADY_EQUIPPED);
            AssertRejectIsPure(sim, new EquipWeapon(1, tut), RejectCode.InvalidTarget, CollectionRejectReasons.WEAPON_IN_OTHER_SLOT);

            Own(sim.State.Weapons, e);
            Assert.That(sim.Apply(new EquipWeapon(0, e)).Ok, Is.True);
            Assert.That(sim.State.Party.Slots[0].Weapon, Is.EqualTo(e));
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Single(), Is.EqualTo(new ItemEquipped(Banner.WEAPON, 0, e, tut, false)));

            Assert.That(sim.Apply(new EquipWeapon(3, tut)).Ok, Is.True, "now free, the tutorial weapon fits any companion");
            Assert.That(sim.Apply(new UnequipWeapon(3)).Ok, Is.True);
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Last(), Is.EqualTo(new ItemEquipped(Banner.WEAPON, 3, null, tut, false)));
            Assert.That(sim.Apply(new UnequipWeapon(3)).Ok, Is.True, "an unarmed slot is accepted");
            Assert.That(sim.DrainEvents(), Is.Empty, "with no event");
            AssertRejectIsPure(sim, new UnequipWeapon(7), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
        }

        [Test]
        public void AutoEquipBestWeapons_BestWeaponToTheHeaviestSlot_TiesById_EmptySlotsUnarmed()   // #93, 04 section 6.3
        {
            var sim = Sim();
            var rares = Db.Pool(Banner.WEAPON, Rarity.RARE);
            Assume.That(rares.Count, Is.GreaterThanOrEqualTo(2));
            sim.State.Weapons.Items.Clear();             // three weapons for four occupied slots
            foreach (var x in sim.State.Party.Slots) x.Weapon = null;
            Own(sim.State.Weapons, Db.TutorialWeaponId, copies: 1);
            Own(sim.State.Weapons, rares[1], copies: 1);
            Own(sim.State.Weapons, rares[0], copies: 1);
            sim.State.Party.Slots[4].Companion = null;   // an empty slot gets no weapon
            string sk = Db.Pool(Banner.SKILL, Rarity.EPIC)[0];
            Own(sim.State.Skills, sk);
            sim.State.Party.Slots[3].Skill = sk;         // a heavier slot (its skill factor) takes the best weapon
            sim.Query.MarkDirty();
            Assert.That(sim.Apply(new AutoEquipBestWeapons()).Ok, Is.True);
            var w = sim.State.Party.Slots.Select(x => x.Weapon).ToArray();
            Assert.That(w[3], Is.EqualTo(rares[0]), "rarity tie, level tie: id ordinal; the heaviest slot first");
            Assert.That(w[4], Is.Null);
            Assert.That(w.Count(x => x != null), Is.EqualTo(3), "three owned weapons, four occupied slots: the lightest stays unarmed");
            Assert.That(w.Distinct().Count(x => x != null), Is.EqualTo(3), "no weapon in two slots");
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().All(x => !x.Auto), Is.True, "the command is the player's");

            sim.Apply(new AutoEquipBestWeapons());
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>(), Is.Empty, "already best: no event");
        }

        [Test]
        public void AutoEquip_RanksByRatedValue_NotRarity()   // #93, 04 section 6.3: highest WEAPON_ATK_PCT * LevelMult
        {
            var sim = Sim();
            string epic = Db.Pool(Banner.WEAPON, Rarity.EPIC)[0];
            string rare = Db.Pool(Banner.WEAPON, Rarity.RARE)[0];
            Own(sim.State.Weapons, epic, copies: 1);
            int max = Db.DupeLevels[Db.DupeLevels.Count - 1];
            Own(sim.State.Weapons, rare, copies: max);   // level 10
            double ev = CollectionRules.RatedValue(Db, Banner.WEAPON, sim.State.Weapons, epic);
            double rv = CollectionRules.RatedValue(Db, Banner.WEAPON, sim.State.Weapons, rare);
            Assert.That(ev, Is.EqualTo(Db.Weapons[epic].AtkPct));
            Assert.That(rv, Is.EqualTo(Db.Weapons[rare].AtkPct * Db.LevelMult(Db.DupeLevels.Count)));
            Assume.That(rv, Is.GreaterThan(ev), "content: a level-10 Rare out-values a level-1 Epic");
            Combat.Solo(sim);
            sim.Apply(new AutoEquipBestWeapons());
            Assert.That(sim.State.Party.Slots[0].Weapon, Is.EqualTo(rare));
            Assert.That(Db.LevelMult(Db.DupeLevels.Count), Is.EqualTo(2.8).Within(1e-12), "04 criterion 5: LevelMult 2.8 at level 10");
        }

        [Test]
        public void EquipAndCopyGain_MarkDpsDirty()   // #93: checked through the 03 cache
        {
            var sim = Sim();
            string e = Db.Pool(Banner.WEAPON, Rarity.EPIC)[0];
            double before = sim.Query.RatedDps;
            Own(sim.State.Weapons, e);   // a copy gain outside Apply does not reach the cache ...
            Assert.That(sim.Query.RatedDps, Is.EqualTo(before));
            Assert.That(sim.Apply(new EquipWeapon(1, e)).Ok, Is.True);   // ... the equip marks it dirty
            double after = sim.Query.RatedDps;
            Assert.That(after, Is.GreaterThan(before));
            Assert.That(sim.Query.Power.WeaponFactor(1),
                        Is.EqualTo(1 + (PowerFormula.EquippedWeaponPct(Db, sim.State, 1) + PowerFormula.WeaponPassivePct(Db, sim.State)) / 100));
            var p = sim.Query.Power;
            double without = Enumerable.Range(0, 5).Sum(i => p.SlotDps(i) / p.WeaponFactor(i)) * p.PartyBuff;
            Assert.That(SummonQuery.LayerMult(Db, sim.State, Banner.WEAPON), Is.EqualTo(p.AutoDps / without).Within(1e-12));

            string sk = Db.Pool(Banner.SKILL, Rarity.EPIC)[0];
            Own(sim.State.Skills, sk);
            Assert.That(sim.Apply(new EquipSkill(0, sk)).Ok, Is.True);
            Assert.That(sim.Query.Power.SkillFactor(0), Is.EqualTo(1 + Db.Skills[sk].DpsPct / 100));
            Assert.That(SummonQuery.LayerMult(Db, sim.State, Banner.SKILL), Is.GreaterThan(1.0));
        }

        // ------------------------------------------------------------------------------------------------ skills (7.2)

        [Test]
        public void SlottingASkill_StartsItsFullCooldown_UnequipClearsIt()   // #94 (TODO(#86) decision): no cooldown skip by re-slotting
        {
            var sim = Sim();
            string a = Db.Pool(Banner.SKILL, Rarity.COMMON)[0];
            Own(sim.State.Skills, a);
            sim.Apply(new EquipSkill(1, a));
            Assert.That(sim.State.Combat.SkillCooldowns[1], Is.EqualTo(Db.Skills[a].CooldownTicks));
            sim.Advance(5);
            Assert.That(sim.State.Combat.SkillCooldowns[1], Is.EqualTo(Db.Skills[a].CooldownTicks - 5));
            sim.Apply(new UnequipSkill(1));
            Assert.That(sim.State.Combat.SkillCooldowns[1], Is.Zero);
            sim.Apply(new EquipSkill(1, a));
            Assert.That(sim.State.Combat.SkillCooldowns[1], Is.EqualTo(Db.Skills[a].CooldownTicks), "re-slotting restarts it");
        }

        [Test]
        public void EquipSkill_RejectsUnownedBadSlotAndOtherSlot_AndReplacesTheOccupant()   // #94
        {
            var sim = Sim();
            var skills = Db.Pool(Banner.SKILL, Rarity.COMMON);
            string a = skills[0], b = skills[1];
            int slots = Db.Constants.PartySlots;
            AssertRejectIsPure(sim, new EquipSkill(0, a), RejectCode.NotOwned, CollectionRejectReasons.ITEM_NOT_OWNED);
            Own(sim.State.Skills, a);
            Own(sim.State.Skills, b);
            AssertRejectIsPure(sim, new EquipSkill(-1, a), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            AssertRejectIsPure(sim, new EquipSkill(slots, a), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            AssertRejectIsPure(sim, new EquipSkill(0, "no_such_skill"), RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);

            Assert.That(sim.Apply(new EquipSkill(2, a)).Ok, Is.True);
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Single(), Is.EqualTo(new ItemEquipped(Banner.SKILL, 2, a, null, false)));
            AssertRejectIsPure(sim, new EquipSkill(2, a), RejectCode.InvalidTarget, CollectionRejectReasons.ALREADY_EQUIPPED);
            AssertRejectIsPure(sim, new EquipSkill(3, a), RejectCode.InvalidTarget, CollectionRejectReasons.SKILL_IN_OTHER_SLOT);

            Assert.That(sim.Apply(new EquipSkill(2, b)).Ok, Is.True);
            Assert.That(sim.State.Party.Slots[2].Skill, Is.EqualTo(b));
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Single().PreviousItemId, Is.EqualTo(a));
            Assert.That(sim.State.Party.Slots.Length, Is.EqualTo(slots));
        }

        [Test]
        public void UnequipSkill_EmptiesTheSlot()   // #94
        {
            var sim = Sim();
            string a = Db.Pool(Banner.SKILL, Rarity.COMMON)[0];
            Own(sim.State.Skills, a);
            Assert.That(sim.Apply(new UnequipSkill(0)).Ok, Is.True, "an empty slot is accepted (04 section 7.2)");
            Assert.That(sim.DrainEvents(), Is.Empty);
            AssertRejectIsPure(sim, new UnequipSkill(Db.Constants.PartySlots), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            sim.Apply(new EquipSkill(0, a));
            sim.DrainEvents();
            Assert.That(sim.Apply(new UnequipSkill(0)).Ok, Is.True);
            Assert.That(sim.State.Party.Slots[0].Skill, Is.Null);
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Single(), Is.EqualTo(new ItemEquipped(Banner.SKILL, 0, null, a, false)));
        }

        [Test]
        public void AutoEquipBestSkills_BestSkillToTheHeaviestSlot_DeterministicForTies()   // #94
        {
            var sim = Sim();
            var commons = Db.Pool(Banner.SKILL, Rarity.COMMON).ToList();
            var epics = Db.Pool(Banner.SKILL, Rarity.EPIC).ToList();
            // insert in reverse so dictionary order differs from the expected order
            foreach (var id in commons.AsEnumerable().Reverse()) Own(sim.State.Skills, id);
            Own(sim.State.Skills, epics[0]);
            Assert.That(sim.Apply(new AutoEquipBestSkills()).Ok, Is.True);
            var slots = sim.State.Party.Slots.Select(x => x.Skill).ToArray();
            Assert.That(slots[0], Is.EqualTo(epics[0]), "slot 0 holds the starter weapon: the heaviest slot");
            var expectedCommons = commons.OrderBy(x => x, System.StringComparer.Ordinal).Take(Db.Constants.PartySlots - 1).ToList();
            Assert.That(slots.Skip(1), Is.EqualTo(expectedCommons), "the four equal slots by slot index, the commons by id");

            sim.DrainEvents();
            Assert.That(sim.Apply(new AutoEquipBestSkills()).Ok, Is.True);
            Assert.That(sim.DrainEvents(), Is.Empty, "already best: no event");
            Assert.That(CollectionRules.BetterSkillAvailable(Db, sim.State), Is.False);
        }

        // ------------------------------------------------------------------------------------------------ queries (11, 4)

        [Test]
        public void Collection_OwnedOverPool_UnownedIncluded_AndNewCount()   // criterion 16 (model part), #95
        {
            var sim = Sim();
            var pool = SummonQuery.PoolIds(Db, Banner.WEAPON);
            Own(sim.State.Weapons, pool[pool.Count - 1], copies: 3);
            var seen = new System.Collections.Generic.HashSet<string>();
            bool IsNew(string id) => CollectionRules.Owns(sim.State.Weapons, id) && !seen.Contains(id);

            var vm = SummonQuery.Collection(sim, Banner.WEAPON, IsNew);
            int owned = sim.State.Weapons.Items.Count(kv => kv.Value.Copies > 0 && pool.Contains(kv.Key));
            Assert.That(vm.PoolCount, Is.EqualTo(pool.Count).And.EqualTo(vm.Items.Count));
            Assert.That(vm.OwnedCount, Is.EqualTo(owned).And.EqualTo(6), "five starter weapons plus one (00 decision 111)");
            Assert.That(vm.NewCount, Is.EqualTo(6));
            foreach (var id in pool) seen.Add(id);
            Assert.That(SummonQuery.Collection(sim, Banner.WEAPON, IsNew).NewCount, Is.Zero);

            var tut = vm.Items.Single(i => i.Id == Db.TutorialWeaponId);
            Assert.That((tut.Equipped, tut.Slot), Is.EqualTo((true, (int?)0)));
            var unowned = vm.Items.First(i => !i.Owned);
            Assert.That((unowned.Copies, unowned.Level, unowned.EffectiveLevel, unowned.CopiesToNextLevel), Is.EqualTo((0, 0, 0, Db.DupeLevels[0])));
        }

        [Test]
        public void Collection_HeadlineValues_PassiveAndLayer_FromThePowerLayer()   // #95, 04 section 11
        {
            var sim = Sim();
            string w = Db.Pool(Banner.WEAPON, Rarity.RARE)[0];
            Own(sim.State.Weapons, w, copies: Db.DupeLevels[1]);   // level 2
            var vm = SummonQuery.Collection(sim, Banner.WEAPON);
            var item = vm.Items.Single(i => i.Id == w);
            var def = Db.Weapons[w];
            Assert.That(item.ValueNow, Is.EqualTo(def.AtkPct * Db.LevelMult(2)));
            Assert.That(item.ValueNextLevel, Is.EqualTo(def.AtkPct * Db.LevelMult(3)));
            Assert.That(item.PassiveNow, Is.EqualTo(def.PassivePct * Db.LevelMult(2)));
            Assert.That(vm.PassiveTotal, Is.EqualTo(PowerFormula.WeaponPassivePct(Db, sim.State)));
            Assert.That(vm.LayerMult, Is.EqualTo(SummonQuery.LayerMult(Db, sim.State, Banner.WEAPON)));
            Assert.That(vm.LayerMult, Is.GreaterThan(1.0), "the starter weapon and the passive");
            Assert.That(vm.Items.All(i => !double.IsNaN(i.ValueNow) && !double.IsNaN(i.ValueNextLevel) && !double.IsNaN(i.PassiveNow)));

            var unowned = vm.Items.First(i => !i.Owned);
            Assert.That((unowned.ValueNow, unowned.PassiveNow), Is.EqualTo((0.0, 0.0)));
            Assert.That(unowned.ValueNextLevel, Is.EqualTo(Db.Weapons[unowned.Id].AtkPct), "what the first copy gives");

            string sk = Db.Pool(Banner.SKILL, Rarity.EPIC)[0];
            int max = Db.DupeLevels[Db.DupeLevels.Count - 1];
            Own(sim.State.Skills, sk, copies: max);
            var svm = SummonQuery.Collection(sim, Banner.SKILL);
            var s = svm.Items.Single(i => i.Id == sk);
            Assert.That(s.ValueNow, Is.EqualTo(Db.Skills[sk].DpsPct * 2.8).Within(1e-9));
            Assert.That(s.ValueNextLevel, Is.EqualTo(s.ValueNow), "no next level at level 10");
            Assert.That((s.PassiveNow, svm.PassiveTotal, svm.LayerMult), Is.EqualTo((0.0, 0.0, 1.0)), "nothing equipped, no passive");
        }

        [Test]
        public void Collection_CopiesToNextLevel_ZeroAtLevel10()   // #95
        {
            var sim = Sim();
            string id = Db.Pool(Banner.SKILL, Rarity.RARE)[0];
            int max = Db.DupeLevels[Db.DupeLevels.Count - 1];
            Own(sim.State.Skills, id, copies: max);
            var item = SummonQuery.Collection(sim, Banner.SKILL).Items.Single(i => i.Id == id);
            Assert.That(item.Level, Is.EqualTo(Db.DupeLevels.Count));
            Assert.That(item.CopiesToNextLevel, Is.Zero);
            Own(sim.State.Skills, id, copies: Db.DupeLevels[1]);
            item = SummonQuery.Collection(sim, Banner.SKILL).Items.Single(i => i.Id == id);
            Assert.That((item.Level, item.CopiesToNextLevel), Is.EqualTo((2, Db.DupeLevels[2] - Db.DupeLevels[1])));
        }

        [Test]
        public void RateTable_ForLevelL_IsTheBannerLevelsRow()   // #95
        {
            foreach (Banner b in new[] { Banner.WEAPON, Banner.SKILL })
            {
                var def = Db.Banners[b];
                for (int l = 1; l <= def.Levels.Count; l++)
                {
                    var row = SummonQuery.RateTable(Db, b, l);
                    Assert.That(row.Level, Is.EqualTo(l));
                    Assert.That(row.ExpRequired, Is.EqualTo(def.Levels[l - 1].ExpRequired));
                    Assert.That(row.Rates, Is.EquivalentTo(def.Levels[l - 1].Rates));
                    Assert.That(row.Rates.Count, Is.EqualTo(5), "all five rarities");
                }
            }
        }

        [Test]
        public void BannerView_PullsToNextLevel_PityAndAffordability()   // #95
        {
            var sim = Sim();
            var def = Db.Banners[Banner.WEAPON];
            var v = SummonQuery.BannerView(sim, Banner.WEAPON);
            Assert.That(v.BannerLevel, Is.EqualTo(1));
            Assert.That(v.PullsToNextLevel, Is.EqualTo((def.Levels[1].ExpRequired + def.ExpPerPull - 1) / def.ExpPerPull));
            Assert.That(v.CanAffordDiamonds1, Is.EqualTo(sim.State.Wallet.Diamond >= def.PullCostDiamonds));
            Assert.That(v.CanAffordTickets1, Is.False);

            var bs = sim.State.Banners.TryGetValue(Banner.WEAPON, out var x) && x != null ? x : (sim.State.Banners[Banner.WEAPON] = new BannerState());
            bs.Exp = def.Levels[def.Levels.Count - 1].ExpRequired;
            bs.Pity = 7;
            v = SummonQuery.BannerView(sim, Banner.WEAPON);
            Assert.That(v.BannerLevel, Is.EqualTo(def.MaxLevel));
            Assert.That(v.PullsToNextLevel, Is.Zero, "0 at the top level");
            Assert.That(v.Pity, Is.EqualTo(7));
            Assert.That(v.Rates.Level, Is.EqualTo(def.MaxLevel));
        }
    }
}
