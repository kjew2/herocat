using System;
using System.Linq;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Events;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>Sim-side rules agreed on issue #246 (00 decisions 98 and 101): multi-event tutorial steps and the backwards-clock tolerance.</summary>
    public class DecisionsWave3Tests
    {
        static readonly long T0 = TestContent.T0;

        [Test]
        public void TutorialBossStep_CompletesOnBossClearedOrBossFailed()   // 07 section 14 step 12, 09 section 5.29
        {
            var boss = TestContent.Mvp.TutorialSteps.Single(s => s.Id == "tut_boss");
            Assert.That(boss.ExpectEvents, Is.EqualTo(new[] { "BossCleared", "BossFailed" }));
            Assert.That(boss.ExpectCount, Is.EqualTo(1));
            Assert.That(boss.Expects(nameof(BossCleared)), Is.True);
            Assert.That(boss.Expects(nameof(BossFailed)), Is.True);
            Assert.That(boss.Expects(nameof(EnemyKilled)), Is.False);
            var tap = TestContent.Mvp.TutorialSteps.Single(s => s.Id == "tut_hero_tab");
            Assert.That(tap.ExpectEvents, Is.Empty, "an empty expect_event is tap to continue");
            Assert.That(tap.Expects(nameof(BossCleared)), Is.False);
        }

        [Test]
        public void EveryTutorialExpectEvent_IsASimEventType()   // 09 section 5.29 (E021 in the exporter)
        {
            var names = typeof(Event).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Event)) && !t.IsAbstract)
                                     .Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var step in TestContent.Mvp.TutorialSteps)
                foreach (var name in step.ExpectEvents)
                    Assert.That(names, Does.Contain(name), step.Id + ": " + name);
        }

        [Test]
        public void ClockBehindByTheTolerance_IsJitter_PastItIsClockWentBackwards()   // 01 section 3.1, issue #246 item 5
        {
            int tol = TestContent.Db.Constants.ClockBackwardsToleranceS;
            Assert.That(tol, Is.EqualTo(5));
            var sim = TestContent.NewSim(T0 + 3600);
            sim.DrainEvents();
            long now = sim.VirtualNowServerSeconds;

            sim.Apply(new ApplyServerTime(now - tol));
            Assert.That(sim.DrainEvents().OfType<ClockWentBackwards>(), Is.Empty, "a gap of exactly the tolerance is jitter");

            now = sim.VirtualNowServerSeconds;
            sim.Apply(new ApplyServerTime(now - tol - 1));
            Assert.That(sim.DrainEvents().OfType<ClockWentBackwards>().Single().DeltaSeconds, Is.EqualTo(tol + 1));
        }
    }
}
