using System;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>06 section 1 and 00 contract 6: the single Wallet.Apply path (issue #106).</summary>
    public class LedgerTests
    {
        sealed class Sink : IEventEmitter
        {
            public readonly System.Collections.Generic.List<Event> Events = new System.Collections.Generic.List<Event>();
            public void Emit(Event e) => Events.Add(e);
        }

        [Test]
        public void EveryApply_EmitsExactlyOneCurrencyChanged_WithItsReason()   // 06 AC 22
        {
            var s = TestContent.NewState();
            var sink = new Sink();
            Assert.That(Ledger.Apply(s, new CurrencyDelta(Currency.GOLD, 125.5), CurrencyReason.KILL, sink), Is.True);
            Assert.That(Ledger.Apply(s, new CurrencyDelta(Currency.GOLD, -25.5), CurrencyReason.TRAINING, sink), Is.True);
            Assert.That(sink.Events, Has.Count.EqualTo(2));
            var last = (CurrencyChanged)sink.Events[1];
            Assert.That((last.Currency, last.Delta, last.BalanceAfter, last.Reason), Is.EqualTo((Currency.GOLD, -25.5, 100.0, CurrencyReason.TRAINING)));
            Assert.That(s.Wallet.Gold, Is.EqualTo(100.0));
            Assert.That(s.Stats.GoldEarned, Is.EqualTo(125.5));
            Assert.That(s.Stats.GoldSpent, Is.EqualTo(25.5));
        }

        [Test]
        public void Overdraft_NaN_Infinity_AndFractionalCounts_AreRejected_AndChangeNothing()
        {
            var s = TestContent.NewState();
            var sink = new Sink();
            string before = Replay.StateHasher.Hash(s);
            Assert.That(Ledger.Apply(s, new CurrencyDelta(Currency.DIAMOND, -1501), CurrencyReason.SUMMON, sink), Is.False);
            Assert.That(Ledger.Apply(s, new CurrencyDelta(Currency.GOLD, double.NaN), CurrencyReason.KILL, sink), Is.False);
            Assert.That(Ledger.Apply(s, new CurrencyDelta(Currency.GOLD, double.PositiveInfinity), CurrencyReason.KILL, sink), Is.False);
            Assert.That(Ledger.Apply(s, new CurrencyDelta(Currency.DIAMOND, 0.5), CurrencyReason.QUEST, sink), Is.False);
            Assert.That(Ledger.Apply(s, new CurrencyDelta(Currency.TICKET_EXP, -1), CurrencyReason.DUNGEON, sink), Is.False);
            Assert.That(sink.Events, Is.Empty);
            Assert.That(Replay.StateHasher.Hash(s), Is.EqualTo(before));
        }

        [Test]
        public void HeroExp_CreditsHeroExp_NotTheWallet()
        {
            var s = TestContent.NewState();
            var sink = new Sink();
            Assert.That(Ledger.Apply(s, new CurrencyDelta(Currency.HERO_EXP, 12.5), CurrencyReason.KILL, sink), Is.True);
            Assert.That(s.Hero.Exp, Is.EqualTo(12.5));
            Assert.That(s.Wallet.HeroExp, Is.EqualTo(0));
            Assert.That(((CurrencyChanged)sink.Events[0]).BalanceAfter, Is.EqualTo(12.5));
        }

        [Test]
        public void Tickets_UseTheirOrdinalSlots_AndFeedTheUploadCounters()
        {
            var s = TestContent.NewState();
            Ledger.Apply(s, new CurrencyDelta(Currency.TICKET_SUMMON_SKILL, 2), CurrencyReason.QUEST, null);
            Ledger.Apply(s, new CurrencyDelta(Currency.TICKET_ROCK_CANDY, 3), CurrencyReason.DAILY_RESET, null);
            Ledger.Apply(s, new CurrencyDelta(Currency.TICKET_SUMMON_RUNE, 1), CurrencyReason.MAIL, null);
            Assert.That(s.Wallet.SummonTicket, Is.EqualTo(new[] { 0, 2, 0, 1 }));
            Assert.That(s.Wallet.DungeonTicket, Is.EqualTo(new[] { 0, 3, 0 }));
            Assert.That(s.Stats.ClientTicketsEarned[Banner.SKILL][CurrencyReason.QUEST], Is.EqualTo(2));
            Assert.That(s.Stats.ClientTicketsEarned.ContainsKey(Banner.RUNE), Is.False, "MAIL is a server grant (issue #49)");
            Ledger.Apply(s, new CurrencyDelta(Currency.DIAMOND, 300), CurrencyReason.IAP, null);
            Assert.That(s.Stats.DiamondsPurchased, Is.EqualTo(300));
            Assert.That(s.Stats.ClientDiamondsEarned.ContainsKey(CurrencyReason.IAP), Is.False);
        }

        [Test]
        public void Reasons_AreThe00ClosedList_InOrder()
        {
            Assert.That(Enum.GetNames(typeof(CurrencyReason)), Is.EqualTo(new[]
            {
                "KILL", "BOSS", "OFFLINE", "LOGIN", "TUTORIAL", "FIRST_CLEAR", "QUEST", "ACHIEVEMENT", "DAILY_GIFT", "DAILY_RESET",
                "SUMMON", "TRAINING", "RESEARCH", "AWAKENING", "LIMIT_BREAK", "IAP", "MAIL", "DUNGEON", "FISH_DISCOVERY", "FISH_MERGE",
                "SHOP_EXCHANGE", "REFUND", "ADMIN",
            }));
            Assert.That((int)Currency.TICKET_SUMMON_RUNE, Is.EqualTo(12), "appended after GUILD_COIN (issue #26)");
        }
    }
}
