using System.Linq;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>ApplyInboxGrant (01 section 2.5, 06 section 9; issue #176).</summary>
    public class InboxTests
    {
        static Simulation NewSim()
        {
            var sim = TestContent.NewSim();
            sim.DrainEvents();
            return sim;
        }

        static GrantLine Cur(Currency c, long n) => new GrantLine("currency", c.ToString(), n);

        static void AssertRejectIsPure(Simulation sim, InboxGrantPayload g, RejectCode code, string reason)
        {
            sim.DrainEvents();
            string hash = sim.StateHash();
            var r = sim.Apply(new ApplyInboxGrant(g));
            Assert.That((r.Ok, r.Code, r.Reason), Is.EqualTo((false, code, reason)), g?.ToString());
            Assert.That(sim.StateHash(), Is.EqualTo(hash), "a reject changes nothing");
            Assert.That(sim.DrainEvents(), Is.Empty, "a reject emits nothing");
        }

        [Test]
        public void Grant_AppliesEveryLineWithItsReason_AnyCurrency_AndRecordsTheId()   // 06 rule 9.4
        {
            var sim = NewSim();
            var w = sim.State.Wallet;
            long dia = w.Diamond;
            double fish = w.FishCoin;
            var g = new InboxGrantPayload("ibx_1", new[] { Cur(Currency.DIAMOND, 300), Cur(Currency.ROCK_CANDY, 50), Cur(Currency.FISH_COIN, 1000),
                                                            Cur(Currency.TICKET_SUMMON_WEAPON, 5) }, CurrencyReason.MAIL);
            Assert.That(sim.Apply(new ApplyInboxGrant(g)).Ok, Is.True);
            var ev = sim.DrainEvents().OfType<CurrencyChanged>().ToList();
            Assert.That(ev.Select(e => (e.Currency, e.Delta, e.Reason)), Is.EqualTo(new[]
            {
                (Currency.DIAMOND, 300.0, CurrencyReason.MAIL), (Currency.ROCK_CANDY, 50.0, CurrencyReason.MAIL),
                (Currency.FISH_COIN, 1000.0, CurrencyReason.MAIL), (Currency.TICKET_SUMMON_WEAPON, 5.0, CurrencyReason.MAIL),
            }));
            Assert.That(w.Diamond, Is.EqualTo(dia + 300));
            Assert.That(w.FishCoin, Is.EqualTo(fish + 1000));
            Assert.That(sim.State.Inbox.ClaimedIds, Is.EquivalentTo(new[] { "ibx_1" }));
            Assert.That(sim.State.Stats.DiamondsPurchased, Is.EqualTo(0), "MAIL is not a purchase");

            var iap = new InboxGrantPayload("ibx_2", new[] { Cur(Currency.DIAMOND, 2400) }, CurrencyReason.IAP);
            Assert.That(sim.Apply(new ApplyInboxGrant(iap)).Ok, Is.True);
            Assert.That(sim.State.Stats.DiamondsPurchased, Is.EqualTo(2400));
        }

        [Test]
        public void Replay_OfAClaimedId_EmitsNoCurrencyChanged_AndChangesNothing()   // #176 acceptance 2
        {
            var sim = NewSim();
            var g = new InboxGrantPayload("ibx_1", new[] { Cur(Currency.DIAMOND, 300) }, CurrencyReason.IAP);
            Assert.That(sim.Apply(new ApplyInboxGrant(g)).Ok, Is.True);
            AssertRejectIsPure(sim, g, RejectCode.AlreadyClaimed, InboxRejectReasons.INBOX_ALREADY_APPLIED);
            AssertRejectIsPure(sim, g with { Lines = new[] { Cur(Currency.DIAMOND, 999) } }, RejectCode.AlreadyClaimed,
                               InboxRejectReasons.INBOX_ALREADY_APPLIED);
        }

        [Test]
        public void MalformedGrants_AreRejectedWholly()
        {
            var sim = NewSim();
            var ok = new[] { Cur(Currency.DIAMOND, 10) };
            AssertRejectIsPure(sim, null, RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
            AssertRejectIsPure(sim, new InboxGrantPayload("", ok, CurrencyReason.MAIL), RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
            AssertRejectIsPure(sim, new InboxGrantPayload("a", null, CurrencyReason.MAIL), RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
            AssertRejectIsPure(sim, new InboxGrantPayload("a", ok, CurrencyReason.KILL), RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
            AssertRejectIsPure(sim, new InboxGrantPayload("a", ok, CurrencyReason.SUMMON), RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
            foreach (var bad in new[]
            {
                new GrantLine("currency", "diamond", 10), new GrantLine("currency", "1", 10), new GrantLine("currency", "NOPE", 10),
                new GrantLine("currency", null, 10), new GrantLine("currency", "DIAMOND", 0), new GrantLine("currency", "DIAMOND", -5),
                new GrantLine("coins", "DIAMOND", 10), null,
            })
                AssertRejectIsPure(sim, new InboxGrantPayload("a", new[] { Cur(Currency.GOLD, 1), bad }, CurrencyReason.MAIL),
                                   RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
            foreach (var bad in new[]
            {
                new GrantLine("item", "wpn_x", 1), new GrantLine("item", null, 1), new GrantLine("item", "weapon_c_01", 0),
                new GrantLine("item", "weapon_c_01", -1), new GrantLine("item", "weapon_c_01", (long)int.MaxValue + 1), new GrantLine("item", "DIAMOND", 1),
            })
                AssertRejectIsPure(sim, new InboxGrantPayload("a", new[] { Cur(Currency.GOLD, 1), bad }, CurrencyReason.ADMIN),
                                   RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
        }

        [Test]
        public void ItemGrant_AddsCopies_EmitsOneItemAcquiredPerLine_AutoEquips_AndCountsNoQuest()   // 00 decision 116, issue #271
        {
            var sim = NewSim();
            var s = sim.State;
            Assert.That(s.Weapons.Items.ContainsKey("weapon_m_01"), Is.False);
            long now = sim.VirtualNowServerSeconds;
            long summonQuest = (s.Quests.Daily.Values.Sum(q => q.Progress) + s.Quests.Weekly.Values.Sum(q => q.Progress));
            var g = new InboxGrantPayload("ibx_i", new[] { new GrantLine("item", "weapon_m_01", 3), Cur(Currency.DIAMOND, 10),
                                                            new GrantLine("item", "comp_c_02", 1) }, CurrencyReason.ADMIN);
            Assert.That(sim.Apply(new ApplyInboxGrant(g)).Ok, Is.True);
            var ev = sim.DrainEvents();
            var acquired = ev.OfType<ItemAcquired>().ToList();
            Assert.That(acquired.Select(e => (e.Banner, e.ItemId, e.Rarity, e.IsNew, e.LevelBefore, e.OverflowRockCandy)), Is.EqualTo(new[]
            {
                (Banner.WEAPON, "weapon_m_01", Rarity.MYTHIC, true, 0, 0L),
                (Banner.COMPANION, "comp_c_02", Rarity.COMMON, false, sim.Db.DupeLevel(1), 0L),
            }));
            var w = s.Weapons.Items["weapon_m_01"];
            Assert.That((w.Copies, w.FirstAcquiredServerTime), Is.EqualTo((3, now)), "a new item is stamped with the claim time");
            Assert.That(acquired[0].LevelAfter, Is.EqualTo(sim.Db.DupeLevel(3)));
            Assert.That(s.Companions.Items["comp_c_02"].Copies, Is.EqualTo(2));
            Assert.That(ev.OfType<ItemEquipped>().Any(e => e.Banner == Banner.WEAPON && e.ItemId == "weapon_m_01" && e.Auto), Is.True,
                        "weapon auto-equip runs as after a summon");
            Assert.That(s.Party.Slots.Any(x => x.Weapon == "weapon_m_01"), Is.True);
            Assert.That((s.Quests.Daily.Values.Sum(q => q.Progress) + s.Quests.Weekly.Values.Sum(q => q.Progress)), Is.EqualTo(summonQuest), "a grant is not a summon");
            Assert.That(s.Stats.Pulls, Is.EqualTo(0));
        }

        [Test]
        public void ItemGrant_RunsTheOwnAchievementLadders()   // 06 section 5.4, MVP content
        {
            var db = TestContent.Mvp;
            var state = HeroCat.Runtime.Profile.ProfileFactory.Create(db, System.Guid.Empty, TestContent.T0, new System.Guid("de71ce00-0000-4000-8000-000000000001"), TestContent.Root);
            state.Progress.HighestCleared = db.FeatureUnlocks["QUESTS"].Threshold.Value;
            var sim = new Simulation(state, db);
            sim.Apply(new ApplyServerTime(TestContent.T0));
            var g = new InboxGrantPayload("ibx_a", new[] { new GrantLine("item", "weapon_m_01", 1), new GrantLine("item", "skill_c_02", 1) },
                                          CurrencyReason.ADMIN);
            Assert.That(sim.Apply(new ApplyInboxGrant(g)).Ok, Is.True);
            Assert.That(sim.QuestProgressOf(db.Quest("ach_weapons_01")), Is.EqualTo(state.Weapons.Items.Count), "OWN_WEAPONS: distinct ids");
            Assert.That(sim.QuestProgressOf(db.Quest("ach_skills_01")), Is.EqualTo(state.Skills.Items.Count), "OWN_SKILLS: distinct ids");
        }

        [Test]
        public void ItemGrant_PastTheCap_ConvertsTheRestToRockCandy_WithTheGrantReason()   // 04 section 5.3
        {
            var sim = NewSim();
            int max = sim.Db.DupeLevels[sim.Db.DupeLevels.Count - 1];
            sim.State.Weapons.Items["weapon_l_01"] = new HeroCat.Sim.State.OwnedItem { Copies = max - 1 };
            double rc = sim.State.Wallet.Get(Currency.ROCK_CANDY);
            long rate = sim.Db.Rarities[Rarity.LEGENDARY].DupeOverflowRockCandy;
            var g = new InboxGrantPayload("ibx_o", new[] { new GrantLine("item", "weapon_l_01", 4) }, CurrencyReason.MAIL);
            Assert.That(sim.Apply(new ApplyInboxGrant(g)).Ok, Is.True);
            var ev = sim.DrainEvents();
            Assert.That(sim.State.Weapons.Items["weapon_l_01"].Copies, Is.EqualTo(max));
            var a = ev.OfType<ItemAcquired>().Single();
            Assert.That((a.IsNew, a.OverflowRockCandy, a.LevelAfter), Is.EqualTo((false, 3 * rate, sim.Db.DupeLevel(max))));
            var cc = ev.OfType<CurrencyChanged>().Single(e => e.Currency == Currency.ROCK_CANDY);
            Assert.That((cc.Delta, cc.Reason), Is.EqualTo((3.0 * rate, CurrencyReason.MAIL)));
            Assert.That(sim.State.Wallet.Get(Currency.ROCK_CANDY), Is.EqualTo(rc + 3 * rate));

            // A replay applies nothing twice.
            AssertRejectIsPure(sim, g, RejectCode.AlreadyClaimed, InboxRejectReasons.INBOX_ALREADY_APPLIED);
        }

        [Test]
        public void EmptyGrant_RefundNotice_OnlyRecordsTheId()   // 08 section 6: refund notices carry grants = []
        {
            var sim = NewSim();
            Assert.That(sim.Apply(new ApplyInboxGrant(new InboxGrantPayload("ibx_r", new GrantLine[0], CurrencyReason.REFUND))).Ok, Is.True);
            Assert.That(sim.DrainEvents().OfType<CurrencyChanged>(), Is.Empty);
            Assert.That(sim.State.Inbox.ClaimedIds.Contains("ibx_r"), Is.True);
        }
    }
}
