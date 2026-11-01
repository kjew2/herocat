using System.Linq;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
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
        public void NewProfile_OwnsAndEquipsTheTutorialWeapon()   // #93
        {
            var s = TestContent.NewState();
            Assert.That(Db.TutorialWeaponId, Is.Not.Null);
            Assert.That(s.Loadout.Weapon, Is.EqualTo(Db.TutorialWeaponId));
            Assert.That(CollectionRules.Owns(s.Weapons, Db.TutorialWeaponId), Is.True);
        }

        [Test]
        public void EquipWeapon_RequiresOwnership_AndPublishesItemEquipped()   // #93, criterion 8 (no unequip)
        {
            var sim = Sim();
            string tut = sim.State.Loadout.Weapon;
            string e = Db.Pool(Banner.WEAPON, Rarity.EPIC)[0];
            AssertRejectIsPure(sim, new EquipWeapon(e), RejectCode.NotOwned, CollectionRejectReasons.ITEM_NOT_OWNED);
            AssertRejectIsPure(sim, new EquipWeapon("no_such_weapon"), RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            AssertRejectIsPure(sim, new EquipWeapon(null), RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            AssertRejectIsPure(sim, new EquipWeapon(tut), RejectCode.InvalidTarget, CollectionRejectReasons.ALREADY_EQUIPPED);

            Own(sim.State.Weapons, e);
            Assert.That(sim.Apply(new EquipWeapon(e)).Ok, Is.True);
            Assert.That(sim.State.Loadout.Weapon, Is.EqualTo(e));
            var ev = sim.DrainEvents().OfType<ItemEquipped>().Single();
            Assert.That(ev, Is.EqualTo(new ItemEquipped(Banner.WEAPON, 0, e, tut, false)));

            Assert.That(typeof(Command).Assembly.GetTypes().Any(t => t.Name == "UnequipWeapon"), Is.False, "no weapon unequip (04 6.3)");
        }

        [Test]
        public void AutoEquipBestWeapon_PicksHighestRarityThenLevelThenId_AndOnlyIfStrictlyBetter()   // #93
        {
            var sim = Sim();
            var rares = Db.Pool(Banner.WEAPON, Rarity.RARE);
            Assume.That(rares.Count, Is.GreaterThanOrEqualTo(2));
            Own(sim.State.Weapons, rares[1], copies: 1);
            Own(sim.State.Weapons, rares[0], copies: 1);
            Assert.That(sim.Apply(new AutoEquipBestWeapon()).Ok, Is.True);
            Assert.That(sim.State.Loadout.Weapon, Is.EqualTo(rares[0]), "rarity tie, level tie: id ordinal");

            // same rarity, higher level wins
            sim.State.Weapons.Items[rares[1]].Copies = Db.DupeLevels[2];   // level 3
            sim.DrainEvents();
            sim.Apply(new AutoEquipBestWeapon());
            Assert.That(sim.State.Loadout.Weapon, Is.EqualTo(rares[1]));
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Single().Auto, Is.False, "the command is the player's");

            // an equal-ranked weapon (same rarity and level, smaller id) does not replace the equipped one
            sim.State.Weapons.Items[rares[0]].Copies = Db.DupeLevels[2];
            sim.Apply(new AutoEquipBestWeapon());
            Assert.That(sim.State.Loadout.Weapon, Is.EqualTo(rares[1]), "switches only to a strictly better weapon");
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>(), Is.Empty);
        }

        // ------------------------------------------------------------------------------------------------ skills (7.2)

        [Test]
        public void EquipSkill_RejectsUnownedBadSlotAndOtherSlot_AndReplacesTheOccupant()   // #94
        {
            var sim = Sim();
            var skills = Db.Pool(Banner.SKILL, Rarity.COMMON);
            string a = skills[0], b = skills[1];
            int slots = Db.Constants.SkillSlots;
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
            Assert.That(sim.State.Loadout.Skills[2], Is.EqualTo(b));
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Single().PreviousItemId, Is.EqualTo(a));
            Assert.That(sim.State.Loadout.Skills.Length, Is.EqualTo(slots));
        }

        [Test]
        public void UnequipSkill_EmptiesTheSlot()   // #94
        {
            var sim = Sim();
            string a = Db.Pool(Banner.SKILL, Rarity.COMMON)[0];
            Own(sim.State.Skills, a);
            AssertRejectIsPure(sim, new UnequipSkill(0), RejectCode.InvalidTarget, CollectionRejectReasons.SLOT_EMPTY);
            AssertRejectIsPure(sim, new UnequipSkill(Db.Constants.SkillSlots), RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            sim.Apply(new EquipSkill(0, a));
            sim.DrainEvents();
            Assert.That(sim.Apply(new UnequipSkill(0)).Ok, Is.True);
            Assert.That(sim.State.Loadout.Skills[0], Is.Null);
            Assert.That(sim.DrainEvents().OfType<ItemEquipped>().Single(), Is.EqualTo(new ItemEquipped(Banner.SKILL, 0, null, a, false)));
        }

        [Test]
        public void AutoEquipBestSkills_BestInSlot0_DeterministicForTies()   // #94
        {
            var sim = Sim();
            var commons = Db.Pool(Banner.SKILL, Rarity.COMMON).ToList();
            var epics = Db.Pool(Banner.SKILL, Rarity.EPIC).ToList();
            // insert in reverse so dictionary order differs from the expected order
            foreach (var id in commons.AsEnumerable().Reverse()) Own(sim.State.Skills, id);
            Own(sim.State.Skills, epics[0]);
            Assert.That(sim.Apply(new AutoEquipBestSkills()).Ok, Is.True);
            var slots = sim.State.Loadout.Skills;
            Assert.That(slots[0], Is.EqualTo(epics[0]));
            var expectedCommons = commons.OrderBy(x => x, System.StringComparer.Ordinal).Take(Db.Constants.SkillSlots - 1).ToList();
            for (int i = 1; i < slots.Length; i++)
                Assert.That(slots[i], Is.EqualTo(i - 1 < expectedCommons.Count ? expectedCommons[i - 1] : null));

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
            Assert.That(vm.OwnedCount, Is.EqualTo(owned).And.EqualTo(2));
            Assert.That(vm.NewCount, Is.EqualTo(2));
            foreach (var id in pool) seen.Add(id);
            Assert.That(SummonQuery.Collection(sim, Banner.WEAPON, IsNew).NewCount, Is.Zero);

            var tut = vm.Items.Single(i => i.Id == Db.TutorialWeaponId);
            Assert.That((tut.Equipped, tut.Slot), Is.EqualTo((true, (int?)0)));
            var unowned = vm.Items.First(i => !i.Owned);
            Assert.That((unowned.Copies, unowned.Level, unowned.EffectiveLevel, unowned.CopiesToNextLevel), Is.EqualTo((0, 0, 0, Db.DupeLevels[0])));
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
