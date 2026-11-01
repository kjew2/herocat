using System;
using HeroCat.Sim.Format;
using HeroCat.Sim.Time;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    public class ProgressionSmokeTests
    {
        [Test]
        public void TwoActiveHours_PushesStages_AndGoldFlows()
        {
            var sim = TestContent.NewSim();
            var clock = new FakeClock(TestContent.T0);
            for (int m = 0; m < 120; m++)
            {
                sim.Advance(600);
                clock.Advance(60);
                sim.SyncWallClock(clock);
                GreedyBot.Spend(sim);
            }
            sim.Validate();
            Assert.That(sim.State.Stage.Stage, Is.GreaterThan(30));
            Assert.That(sim.State.Lifetime.GoldEarned, Is.GreaterThan(0));
            Assert.That(sim.State.Progression.StatLevel, Is.GreaterThan(10));
            Assert.That(sim.State.Lifetime.Kills, Is.GreaterThan(1000));
            TestContext.WriteLine($"2h: stage {sim.State.Stage.Stage}, stat {sim.State.Progression.StatLevel}, dps {BigNumber.Short(sim.Query.Dps)}");
        }

        /// <summary>Not an acceptance test (that is the bot harness, specs/09); prints the curve for eyeballing vs specs/00 2.6.</summary>
        [Test]
        public void SevenDays_ReportCurve()
        {
            var sim = TestContent.NewSim();
            var clock = new FakeClock(TestContent.T0);
            int[] targets = { 0, 90, 0, 240, 0, 0, 0, 380 };
            for (int day = 1; day <= 7; day++)
            {
                GreedyBot.PlayDay(sim, clock);
                var q = sim.Query;
                TestContext.WriteLine(
                    $"day {day}: stage {sim.State.Stage.Stage,4} (target {targets[day]})  dps {BigNumber.Short(q.Dps),7}  " +
                    $"stat {sim.State.Progression.StatLevel} x{q.StatMult:0.0}  gear x{BigNumber.Short(q.GearMult)}  " +
                    $"heroes x{q.HeroMult:0.00}  class x{q.ClassMult:0.00}  pulls {sim.State.HeroBanner.TotalPulls}  gems {sim.State.Currencies.Gems}");
            }
            sim.Validate();
            Assert.That(sim.State.Stage.Stage, Is.GreaterThan(100));
        }
    }
}
