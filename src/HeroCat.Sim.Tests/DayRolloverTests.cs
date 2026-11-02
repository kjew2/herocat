using System.Linq;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>Day/week keys, EnsureDay rollover and the login grant (06 section 4, 00 contract 7; issue #107).</summary>
    public class DayRolloverTests
    {
        static readonly long T0 = TestContent.T0;
        static int DaySec => TestContent.Db.Constants.DaySeconds;
        static int Login => TestContent.Db.Constants.LoginDiamonds;

        [Test]
        public void DayKey_ChangesAtMidnightUtc()   // 06 AC 2
        {
            Assert.That(DayKeys.DayKey(DaySec - 1, DaySec), Is.Not.EqualTo(DayKeys.DayKey(DaySec, DaySec)));
            Assert.That(DayKeys.DayKey(0, DaySec), Is.EqualTo(0));
            Assert.That(DayKeys.DayKey(DaySec - 1, DaySec), Is.EqualTo(0));
            Assert.That(DayKeys.DayKey(DaySec, DaySec), Is.EqualTo(1));
            Assert.That(DayKeys.DayKey(-1, DaySec), Is.EqualTo(-1), "floors before the epoch");
        }

        [Test]
        public void WeekKey_StartsMonday_Day4IsWeekZero()   // 06 AC 2, 00 decision 21
        {
            Assert.That(DayKeys.WeekKey(3), Is.Not.EqualTo(DayKeys.WeekKey(4)), "Thursday 1970-01-01 + 3 = Sunday; day 4 is Monday");
            for (int d = 4; d <= 10; d++) Assert.That(DayKeys.WeekKey(d), Is.EqualTo(0), "day " + d);
            Assert.That(DayKeys.WeekKey(4), Is.EqualTo(0));
            Assert.That(DayKeys.WeekKey(11), Is.EqualTo(1));
            Assert.That(DayKeys.WeekKey(3), Is.EqualTo(-1));
            Assert.That(DayKeys.WeekKey(0), Is.EqualTo(-1));
            // 06's pseudocode form (day + 3) / 7 is off by one in value; 00's form wins (issue #107).
            Assert.That(DayKeys.WeekKey(4), Is.Not.EqualTo((4 + 3) / 7));
            // 2026-01-05 is a Monday.
            long monday = new System.DateTimeOffset(2026, 1, 5, 0, 0, 0, System.TimeSpan.Zero).ToUnixTimeSeconds();
            Assert.That(DayKeys.WeekKeyAt(monday, DaySec), Is.EqualTo(DayKeys.WeekKeyAt(monday - 1, DaySec) + 1));
            Assert.That(DayKeys.WeekKeyAt(monday + 6 * DaySec + DaySec - 1, DaySec), Is.EqualTo(DayKeys.WeekKeyAt(monday, DaySec)));
        }

        [Test]
        public void NewProfile_FirstEnsureDay_RollsDayAndWeek_AndPaysOneLogin()   // 06 section 4.2 rule 4
        {
            var sim = TestContent.NewSim(sync: false);
            Assert.That((sim.State.Time.DayKey, sim.State.Time.WeekKey, sim.State.Time.LoginDayKey), Is.EqualTo((-1, -1, -1)));
            sim.Apply(new ApplyServerTime(T0));
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<DayRolledOver>().Count(), Is.EqualTo(1));
            Assert.That(ev.OfType<WeekRolledOver>().Count(), Is.EqualTo(1));
            var login = ev.OfType<CurrencyChanged>().Single(e => e.Reason != CurrencyReason.DAILY_RESET);   // M0 also tops up Rock Candy tickets
            Assert.That((login.Currency, login.Delta, login.Reason), Is.EqualTo((Currency.DIAMOND, (double)Login, CurrencyReason.LOGIN)),
                        "the login reason is LOGIN, not QUEST");
            Assert.That(sim.State.Time.DayKey, Is.EqualTo(DayKeys.DayKey(T0, DaySec)));
            Assert.That(sim.State.Time.WeekKey, Is.EqualTo(DayKeys.WeekKeyAt(T0, DaySec)));
            Assert.That(sim.State.Time.LoginDayKey, Is.EqualTo(sim.State.Time.DayKey));
        }

        [Test]
        public void ThreeDayAbsence_OneMoreOfEachDayEvent_AndOneLogin()   // 06 section 4.2 rule 1
        {
            var sim = TestContent.NewSim();
            sim.DrainEvents();
            long before = sim.State.Wallet.Diamond;
            sim.Apply(new ApplyServerTime(T0 + 3 * DaySec + 100));
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<DayRolledOver>().Single().DayKey, Is.EqualTo(DayKeys.DayKey(T0, DaySec) + 3));
            Assert.That(ev.OfType<CurrencyChanged>().Count(c => c.Reason == CurrencyReason.LOGIN), Is.EqualTo(1));
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(before + Login));
        }

        [Test]
        public void EnsureDay_BackwardsOrSameKey_ChangesNothing()   // 06 AC 3
        {
            var sim = TestContent.NewSim(T0 + 5 * 3600);
            sim.DrainEvents();
            string hash = sim.StateHash();

            // Before the stored day's start: the clock went backwards a whole day.
            sim.Apply(new ApplyServerTime(T0 - 10));
            var back = sim.DrainEvents();
            Assert.That(back.OfType<DayRolledOver>(), Is.Empty);
            Assert.That(back.OfType<CurrencyChanged>(), Is.Empty);
            Assert.That(back.OfType<ClockWentBackwards>().Count(), Is.EqualTo(1), "once per occurrence");
            Assert.That(sim.State.Time.DayKey, Is.EqualTo(DayKeys.DayKey(T0, DaySec)));

            // Later on the same key: no grant.
            var later = TestContent.NewSim(T0 + 3600);
            later.DrainEvents();
            long d = later.State.Wallet.Diamond;
            later.Apply(new ApplyServerTime(T0 + DaySec - 1));
            Assert.That(later.DrainEvents().OfType<CurrencyChanged>(), Is.Empty);
            Assert.That(later.State.Wallet.Diamond, Is.EqualTo(d));
            Assert.That(hash, Is.Not.Empty);
        }

        [Test]
        public void ClockBackwards_NeverMovesLastTickServerTimeBack()   // 06 section 4.2 rule 3
        {
            var sim = TestContent.NewSim();
            sim.Apply(new ComputeOfflineCatchup(T0 + 7200));
            sim.Apply(new ApplyServerTime(T0));
            sim.Apply(new ComputeOfflineCatchup(T0));
            Assert.That(sim.State.Time.LastTickServerTime, Is.EqualTo(T0 + 7200));
        }

        [Test]
        public void RolloverByTicks_AndBySync_GiveIdenticalStateHashes()   // issue #107 acceptance
        {
            long midnight = T0 + DaySec;
            int ticks = 20 * TestContent.Db.Constants.TickHz;

            // Same genesis for both (created at midnight - 30 s).
            // A: anchored at midnight - 10 s, so the app is open across midnight and the day rolls on the crossing tick.
            var a = TestContent.NewSim(midnight - 30, sync: false);
            a.Apply(new ApplyServerTime(midnight - 10));
            a.Advance(ticks);
            Assert.That(a.State.Time.DayKey, Is.EqualTo(DayKeys.DayKey(midnight, DaySec)), "rolled by ticks");
            a.Apply(new ApplyServerTime(midnight + 10));

            // B: anchored at midnight - 30 s, so the virtual clock lags and only the sync rolls the day.
            var b = TestContent.NewSim(midnight - 30, sync: false);
            b.Apply(new ApplyServerTime(midnight - 30));
            b.Advance(ticks);
            Assert.That(b.State.Time.DayKey, Is.EqualTo(DayKeys.DayKey(midnight - 1, DaySec)), "not rolled by ticks");
            b.Apply(new ApplyServerTime(midnight + 10));

            Assert.That(b.State.Time.DayKey, Is.EqualTo(a.State.Time.DayKey));
            Assert.That(b.State.Wallet.Diamond, Is.EqualTo(a.State.Wallet.Diamond));
            // The catch-up anchor is the virtual time ticked (Simulation.Offline.cs), which differs by construction: A's clock
            // reached midnight + 10 s, B's lagged at midnight - 10 s. Everything else is identical.
            Assert.That(a.State.Time.LastTickServerTime, Is.EqualTo(midnight + 10));
            Assert.That(b.State.Time.LastTickServerTime, Is.EqualTo(midnight - 10));
            b.State.Time.LastTickServerTime = a.State.Time.LastTickServerTime;
            Assert.That(b.StateHash(), Is.EqualTo(a.StateHash()));
        }

        [Test]
        public void EnsureDayNow_IsANoOpBeforeTheFirstAnchor_AndRollsAfter()
        {
            var sim = TestContent.NewSim(sync: false);
            sim.State.Time.AnchorServerTime = 0;
            sim.EnsureDayNow();
            Assert.That(sim.State.Time.DayKey, Is.EqualTo(-1));
            sim.Apply(new ApplyServerTime(T0));
            sim.DrainEvents();
            sim.EnsureDayNow();
            Assert.That(sim.DrainEvents(), Is.Empty);
        }

        [Test]
        public void WeekRolls_OnlyOnMonday_WhenDaysRollInsideAWeek()
        {
            // 2026-01-01 is a Thursday: Fri, Sat, Sun roll days only; Monday rolls the week too.
            var sim = TestContent.NewSim();
            sim.DrainEvents();
            for (int i = 1; i <= 3; i++)
            {
                sim.Apply(new ApplyServerTime(T0 + i * DaySec));
                Assert.That(sim.DrainEvents().OfType<WeekRolledOver>(), Is.Empty, "day " + i);
            }
            sim.Apply(new ApplyServerTime(T0 + 4 * DaySec));
            var ev = sim.DrainEvents();
            Assert.That(ev.OfType<WeekRolledOver>().Single().WeekKey, Is.EqualTo(DayKeys.WeekKeyAt(T0 + 4 * DaySec, DaySec)));
            Assert.That(ev.Select(e => e.GetType().Name).Take(2), Is.EqualTo(new[] { "WeekRolledOver", "DayRolledOver" }));
        }
    }
}
