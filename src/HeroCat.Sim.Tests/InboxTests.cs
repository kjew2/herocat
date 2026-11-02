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
            AssertRejectIsPure(sim, new InboxGrantPayload("a", new[] { Cur(Currency.GOLD, 1), new GrantLine("item", "wpn_x", 1) }, CurrencyReason.ADMIN),
                               RejectCode.NotAvailable, InboxRejectReasons.INBOX_ITEM_GRANT_UNSUPPORTED);
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
