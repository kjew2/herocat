using System;
using System.Linq;
using HeroCat.Runtime.Profile;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>The daily free gift and the diamond-for-ticket exchange (06 sections 8.3-8.4, 4.2; 06 acceptance 19, 20, 29; issue #174).</summary>
    public class ShopTests
    {
        const long Day = 86_400;
        static readonly long T0 = TestContent.T0;

        static ContentDb Mvp => TestContent.Mvp;

        static GameState NewState(ContentDb db, long now) =>
            ProfileFactory.Create(db, Guid.Empty, now, new Guid("de71ce00-0000-4000-8000-000000000002"), TestContent.Root);

        static Simulation NewSim(ContentDb db = null, long now = -1)
        {
            db ??= Mvp;
            long t = now < 0 ? T0 : now;
            var sim = new Simulation(NewState(db, t), db);
            sim.Apply(new ApplyServerTime(t));
            sim.DrainEvents();
            return sim;
        }

        static void AssertRejectIsPure(Simulation sim, Command c, RejectCode code, string reason)
        {
            sim.DrainEvents();
            string before = sim.StateHash();
            var r = sim.Apply(c);
            Assert.That((r.Ok, r.Code, r.Reason), Is.EqualTo((false, code, reason)), c.ToString());
            Assert.That(sim.StateHash(), Is.EqualTo(before), c.ToString());
            Assert.That(sim.DrainEvents(), Is.Empty, c.ToString());
        }

        static void NextDay(Simulation sim) => sim.Apply(new ApplyServerTime((sim.State.Time.DayKey + 1) * Day + 60));

        // ------------------------------------------------------------------------------------------------ content

        [Test]
        public void Content_ThreeExchangeOffers_50DiamondsForOneDungeonTicket_TwoADay_M0HasNone()
        {
            Assert.That(Mvp.ExchangeOffers.Select(o => (o.Id, o.PriceCurrency, o.PriceAmount, o.GrantCurrency, o.GrantAmount, o.DailyLimit)),
                        Is.EqualTo(new[]
                        {
                            ("ex_ticket_fish", Currency.DIAMOND, 50L, Currency.TICKET_FISH_COIN, 1L, 2),
                            ("ex_ticket_candy", Currency.DIAMOND, 50L, Currency.TICKET_ROCK_CANDY, 1L, 2),
                            ("ex_ticket_exp", Currency.DIAMOND, 50L, Currency.TICKET_EXP, 1L, 2),
                        }), "06 section 8.4");
            Assert.That(Mvp.ExchangeOffer("ex_ticket_exp").NameKey, Is.EqualTo("shop.ex_ticket_exp.name"));
            Assert.That(Mvp.ExchangeOffer("nope"), Is.Null);
            Assert.That(Mvp.ExchangeOffer(null), Is.Null);
            Assert.That(TestContent.Db.ExchangeOffers, Is.Empty, "the shop is MVP (06 section 1.2)");
            Assert.That(Mvp.Constants.DailyGiftWeaponTickets, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------------------------------------ daily gift

        [Test]
        public void AC19_DailyGift_OncePerDayKey_RejectedTheSameDay_ClaimableAfterRollover()
        {
            var sim = NewSim();
            int before = sim.State.Wallet.SummonTicket[(int)Banner.WEAPON];
            Assert.That(sim.Apply(new ClaimDailyGift()).Ok, Is.True);
            var ev = sim.DrainEvents();
            Assert.That(ev.First(), Is.EqualTo(new DailyGiftClaimed(Currency.TICKET_SUMMON_WEAPON, 1)));
            var cc = ev.OfType<CurrencyChanged>().Single();
            Assert.That((cc.Currency, cc.Delta, cc.Reason), Is.EqualTo((Currency.TICKET_SUMMON_WEAPON, 1.0, CurrencyReason.DAILY_GIFT)));
            Assert.That(sim.State.Wallet.SummonTicket[(int)Banner.WEAPON], Is.EqualTo(before + 1));
            Assert.That(sim.State.Shop.DailyGiftClaimed, Is.True);
            Assert.That(sim.State.Stats.ClientTicketsEarned[Banner.WEAPON][CurrencyReason.DAILY_GIFT], Is.EqualTo(1), "credited on upload (issue #49)");

            sim.Apply(new ApplyServerTime(T0 + 3600));   // same day key
            AssertRejectIsPure(sim, new ClaimDailyGift(), RejectCode.AlreadyClaimed, ShopRejectReasons.DAILY_GIFT_ALREADY_CLAIMED);

            NextDay(sim);
            Assert.That(sim.State.Shop.DailyGiftClaimed, Is.False, "the flag resets at rollover (06 section 4.2)");
            Assert.That(sim.Apply(new ClaimDailyGift()).Ok, Is.True);
            Assert.That(sim.State.Wallet.SummonTicket[(int)Banner.WEAPON], Is.EqualTo(before + 2), "summon tickets never expire");
            Assert.That(sim.State.Stats.ClientTicketsEarned[Banner.WEAPON][CurrencyReason.DAILY_GIFT], Is.EqualTo(2));
        }

        [Test]
        public void DailyGift_OnTheTickThatCrossesMidnight_SeesTheNewDay()
        {
            var sim = NewSim(now: T0 + (Day - T0 % Day) - 30);   // 30 s before 00:00 UTC
            Assert.That(sim.Apply(new ClaimDailyGift()).Ok, Is.True);
            int day = sim.State.Time.DayKey;
            sim.Advance(60 * Mvp.Constants.TickHz);                // ticks across midnight, no ApplyServerTime
            Assert.That(sim.State.Time.DayKey, Is.EqualTo(day + 1));
            Assert.That(sim.Apply(new ClaimDailyGift()).Ok, Is.True);
        }

        // ------------------------------------------------------------------------------------------------ exchange

        [Test]
        public void AC20_Exchange_OverTheLimitRejected_TwoDebit100AndGrantTwo_AThirdTheSameDayRejected()
        {
            var sim = NewSim();
            sim.State.Wallet.Diamond = 1000;
            int tickets = sim.State.Wallet.DungeonTicket[(int)Dungeon.FISH_COIN];
            AssertRejectIsPure(sim, new ExchangeItem("ex_ticket_fish", 3), RejectCode.Capped, ShopRejectReasons.EXCHANGE_LIMIT_REACHED);

            Assert.That(sim.Apply(new ExchangeItem("ex_ticket_fish", 2)).Ok, Is.True);
            var ev = sim.DrainEvents();
            Assert.That(ev.First(), Is.EqualTo(new ItemExchanged("ex_ticket_fish", 2, 100)));
            Assert.That(ev.OfType<CurrencyChanged>().Select(e => (e.Currency, e.Delta, e.Reason)), Is.EqualTo(new[]
            {
                (Currency.DIAMOND, -100.0, CurrencyReason.SHOP_EXCHANGE), (Currency.TICKET_FISH_COIN, 2.0, CurrencyReason.SHOP_EXCHANGE),
            }));
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(900));
            Assert.That(sim.State.Wallet.DungeonTicket[(int)Dungeon.FISH_COIN], Is.EqualTo(tickets + 2));
            Assert.That(sim.State.Shop.ExchangeCounts["ex_ticket_fish"], Is.EqualTo(2));
            Assert.That(sim.State.Stats.ClientDiamondsSpent[CurrencyReason.SHOP_EXCHANGE], Is.EqualTo(100), "a client-side spend (issue #49)");

            AssertRejectIsPure(sim, new ExchangeItem("ex_ticket_fish", 1), RejectCode.Capped, ShopRejectReasons.EXCHANGE_LIMIT_REACHED);
            Assert.That(sim.Apply(new ExchangeItem("ex_ticket_candy", 1)).Ok, Is.True, "each offer has its own limit");
            Assert.That(sim.Apply(new ExchangeItem("ex_ticket_candy", 1)).Ok, Is.True, "qty 1..remaining, in steps");
            AssertRejectIsPure(sim, new ExchangeItem("ex_ticket_candy", 1), RejectCode.Capped, ShopRejectReasons.EXCHANGE_LIMIT_REACHED);
        }

        [Test]
        public void Exchange_CountsResetAtRollover_AndBoughtTicketsSurviveIt()
        {
            var sim = NewSim();
            sim.State.Wallet.Diamond = 1000;
            sim.State.Wallet.DungeonTicket[(int)Dungeon.EXP] = 2;   // the daily allotment
            Assert.That(sim.Apply(new ExchangeItem("ex_ticket_exp", 2)).Ok, Is.True);
            Assert.That(sim.State.Wallet.DungeonTicket[(int)Dungeon.EXP], Is.EqualTo(4));

            NextDay(sim);
            Assert.That(sim.State.Shop.ExchangeCounts, Is.Empty, "counts reset at rollover (06 section 4.2)");
            Assert.That(sim.State.Wallet.DungeonTicket[(int)Dungeon.EXP], Is.EqualTo(4), "bought tickets stack above 2 and survive (06 section 6.2)");
            Assert.That(sim.Apply(new ExchangeItem("ex_ticket_exp", 2)).Ok, Is.True, "a new day, a new limit");
        }

        [Test]
        public void Exchange_With99DiamondsAndQty2_IsRejected_WithNoPartialApply()
        {
            var sim = NewSim();
            sim.State.Wallet.Diamond = 99;
            AssertRejectIsPure(sim, new ExchangeItem("ex_ticket_fish", 2), RejectCode.CannotAfford, CollectionRejectReasons.NotEnough(Currency.DIAMOND));
            Assert.That(sim.State.Shop.ExchangeCounts, Is.Empty, "nothing counted");
            Assert.That(sim.Apply(new ExchangeItem("ex_ticket_fish", 1)).Ok, Is.True, "one is affordable");
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(49));
        }

        [Test]
        public void AC29_EveryShopReject_HasItsCodeAndReason_ChangesNothing_EmitsNothing()
        {
            var sim = NewSim();
            sim.State.Wallet.Diamond = 1000;
            AssertRejectIsPure(sim, new ExchangeItem("ex_ticket_gold", 1), RejectCode.InvalidTarget, ShopRejectReasons.UNKNOWN_ID);
            AssertRejectIsPure(sim, new ExchangeItem(null, 1), RejectCode.InvalidTarget, ShopRejectReasons.UNKNOWN_ID);
            AssertRejectIsPure(sim, new ExchangeItem("ex_ticket_fish", 0), RejectCode.BadArgument, ShopRejectReasons.BAD_QUANTITY);
            AssertRejectIsPure(sim, new ExchangeItem("ex_ticket_fish", -1), RejectCode.BadArgument, ShopRejectReasons.BAD_QUANTITY);
            AssertRejectIsPure(sim, new ExchangeItem("ex_ticket_fish", int.MaxValue), RejectCode.Capped, ShopRejectReasons.EXCHANGE_LIMIT_REACHED);
            AssertRejectIsPure(sim, new ExchangeItem("ex_ticket_fish", int.MinValue), RejectCode.BadArgument, ShopRejectReasons.BAD_QUANTITY);

            var m0 = NewSim(TestContent.Db);   // no SHOP row in an M0 build
            m0.State.Wallet.Diamond = 1000;
            AssertRejectIsPure(m0, new ClaimDailyGift(), RejectCode.Locked, ShopRejectReasons.SHOP_LOCKED);
            AssertRejectIsPure(m0, new ExchangeItem("ex_ticket_fish", 1), RejectCode.Locked, ShopRejectReasons.SHOP_LOCKED);
        }

        // ------------------------------------------------------------------------------------------------ replay

        [Test]
        public void ACommandLogReplay_AcrossDayRollovers_ReproducesTheShopStateAndHash()
        {
            var log = new ListCommandLog();
            var live = new Simulation(NewState(Mvp, T0), Mvp) { CommandLog = log };
            var genesis = NewState(Mvp, T0);
            int hz = Mvp.Constants.TickHz;

            live.Apply(new ApplyServerTime(T0 + (Day - T0 % Day) - 2 * 3600));   // 22:00 UTC
            for (int round = 0; round < 12; round++)
            {
                live.Apply(new ClaimDailyGift());
                live.Apply(new ExchangeItem("ex_ticket_fish", 1));
                live.Apply(new ExchangeItem("ex_ticket_candy", 2));
                live.Advance(20 * 60 * hz);   // round 6 crosses 00:00 UTC on ticks alone
                if (round == 8) live.Apply(new ApplyServerTime(live.VirtualNowServerSeconds + 2 * Day));   // a two-day gap
            }
            Assert.That(live.State.Stats.ClientTicketsEarned[Banner.WEAPON][CurrencyReason.DAILY_GIFT], Is.EqualTo(3), "three day keys");

            var replay = new Simulation(genesis, Mvp);
            replay.Replay(log.Records, live.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(live.StateHash()));
            Assert.That(replay.State.Shop.DailyGiftClaimed, Is.EqualTo(live.State.Shop.DailyGiftClaimed));
            Assert.That(replay.State.Shop.ExchangeCounts.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)),
                        Is.EqualTo(live.State.Shop.ExchangeCounts.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value))));
            Assert.That(replay.State.Stats.ClientDiamondsSpent, Is.EqualTo(live.State.Stats.ClientDiamondsSpent));
        }
    }
}
