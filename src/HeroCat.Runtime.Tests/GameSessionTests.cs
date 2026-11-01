using System;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using HeroCat.Sim.Time;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>Offline catch-up on load / resume, and the session's autosave cadence, all on a FakeClock.</summary>
    public class GameSessionTests
    {
        FakeClock _clock;
        MemorySaveStore _store;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeClock(TestContent.T0 + 3600);   // 01:00 UTC, well clear of a day boundary
            _store = new MemorySaveStore();
        }

        GameSession Open() => GameSession.Open(TestContent.Db, GameServices.InMemory(_clock, _store), null,
            new GameSessionOptions { NewProfileId = TestContent.ProfileId, NewProfileSeed = 7 });

        /// <summary>Real-time play: the fake clock moves in step with Update dt.</summary>
        void Play(GameSession s, int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                _clock.Advance(1);
                s.Update(1.0);
            }
        }

        GameState OnDisk() => SaveSystem.DecodeFull(_store.Primary, TestContent.Db).state;

        double ExpectedOfflineGold(GameSession s, long creditedSec)
        {
            int stage = s.State.Stage.Stage;
            var k = s.Content.Constants;
            return creditedSec * k.OfflineRate * s.Sim.Query.Gold(stage) / s.Sim.Query.KillTimeSeconds(stage);
        }

        static void AssertNothingButGoldMoved(GameState before, GameState after)
        {
            Assert.That(after.Stage.Stage, Is.EqualTo(before.Stage.Stage));
            Assert.That(after.Stage.KillsInStage, Is.EqualTo(before.Stage.KillsInStage));
            Assert.That(after.Lifetime.Kills, Is.EqualTo(before.Lifetime.Kills));
            Assert.That(after.Tick, Is.EqualTo(before.Tick));
            foreach (var name in StreamNames.All)
                Assert.That(after.Rng.Streams[name].Counter, Is.EqualTo(before.Rng.Streams[name].Counter), name);
            Assert.That(after.Currencies.Gems, Is.EqualTo(before.Currencies.Gems));
        }

        [Test]
        public void NewProfile_IsCreatedGrantedAndSaved()
        {
            var s = Open();
            Assert.That(s.IsNewProfile, Is.True);
            Assert.That(s.LoadResult.Status, Is.EqualTo(LoadStatus.NoSave));
            Assert.That(s.State.ProfileId, Is.EqualTo(TestContent.ProfileId));
            Assert.That(s.State.Progression.TutorialGranted, Is.True);
            Assert.That(s.State.Daily.DayKey, Is.EqualTo(_clock.NowServerSeconds / 86400));
            Assert.That(_store.WriteCount, Is.EqualTo(1));
            Assert.That(s.LastCatchUp, Is.Null);
        }

        [Test]
        public void Load_CreditsOfflineGold_SinceLastSave()
        {
            var a = Open();
            Play(a, 300);
            a.Quit();
            var saved = OnDisk();

            _clock.Advance(2 * 3600);
            var b = Open();

            Assert.That(b.IsNewProfile, Is.False);
            var r = b.LastCatchUp.Value;
            Assert.That(r.ElapsedSec, Is.EqualTo(7200));
            Assert.That(r.CreditedSec, Is.EqualTo(7200));
            Assert.That(r.Capped, Is.False);
            Assert.That(r.Gold, Is.EqualTo(ExpectedOfflineGold(b, 7200)).Within(1e-9).Percent);
            Assert.That(r.Gold, Is.GreaterThan(0));
            Assert.That(b.State.Currencies.Gold, Is.EqualTo(saved.Currencies.Gold + r.Gold).Within(1e-9).Percent);
            AssertNothingButGoldMoved(saved, b.State);
            Assert.That(b.TakeWelcomeBack(), Is.Not.Null);
            Assert.That(b.TakeWelcomeBack(), Is.Null, "taken once");
        }

        [Test]
        public void Load_CapsAtOfflineCap()
        {
            var a = Open();
            Play(a, 60);
            a.Quit();
            _clock.Advance(10 * 3600);
            var b = Open();
            long cap = (long)(TestContent.Db.Constants.OfflineCapH * 3600);
            Assert.That(b.LastCatchUp.Value.CreditedSec, Is.EqualTo(cap));
            Assert.That(b.LastCatchUp.Value.Capped, Is.True);
        }

        [Test]
        public void Load_CreditsTheSameWindowOnlyOnce()
        {
            Open().Quit();
            _clock.Advance(3600);
            var b = Open();
            Assert.That(b.LastCatchUp.Value.CreditedSec, Is.EqualTo(3600));
            double gold = b.State.Currencies.Gold;
            var c = Open();   // reopen at the same instant: b's open wrote a fresh anchor
            Assert.That(c.LastCatchUp.Value.CreditedSec, Is.EqualTo(0));
            Assert.That(c.State.Currencies.Gold, Is.EqualTo(gold));
        }

        [Test]
        public void Load_ClockWentBackwards_CreditsNothing()
        {
            var a = Open();
            Play(a, 60);
            a.Quit();
            double gold = OnDisk().Currencies.Gold;
            _clock.Advance(-3600);
            var b = Open();
            Assert.That(b.LastCatchUp.Value.ElapsedSec, Is.EqualTo(0));
            Assert.That(b.State.Currencies.Gold, Is.EqualTo(gold));
        }

        [Test]
        public void Load_AfterADay_RollsTheDay()
        {
            var a = Open();
            long day = a.State.Daily.DayKey;
            a.Sim.Pull();   // spend nothing daily-specific; just make sure we have a non-trivial state
            a.Quit();
            _clock.Advance(30 * 3600);
            var b = Open();
            Assert.That(b.State.Daily.DayKey, Is.EqualTo(day + 1));
            Assert.That(b.State.Currencies.FreePullsDaily, Is.EqualTo(TestContent.Db.HeroBanner.FreePullsPerDay));
        }

        [Test]
        public void PauseResume_CreditsTheTimeAway()
        {
            var s = Open();
            Play(s, 90);
            s.Pause();
            Assert.That(s.Update(1.0), Is.EqualTo(0), "no ticks while paused");
            var before = s.State.DeepClone();
            _clock.Advance(3600);
            s.Resume();
            var r = s.LastCatchUp.Value;
            Assert.That(r.CreditedSec, Is.EqualTo(3600));
            Assert.That(s.State.Currencies.Gold, Is.EqualTo(before.Currencies.Gold + r.Gold).Within(1e-9).Percent);
            AssertNothingButGoldMoved(before, s.State);
            Assert.That(OnDisk().Currencies.Gold, Is.EqualTo(s.State.Currencies.Gold), "resume saves");
        }

        [Test]
        public void FrozenFrame_IsCreditedAsOffline_NotTicked()
        {
            var s = Open();
            Play(s, 10);
            long tick = s.State.Tick;
            _clock.Advance(120);
            Assert.That(s.Update(120), Is.EqualTo(0));
            Assert.That(s.State.Tick, Is.EqualTo(tick));
            Assert.That(s.LastCatchUp.Value.CreditedSec, Is.EqualTo(120));
        }

        [Test]
        public void Update_TicksAtTickHz_WithPerFrameCap()
        {
            var s = Open();
            int hz = TestContent.Db.Constants.TickHz;
            Assert.That(s.Update(1.0), Is.EqualTo(hz));
            Assert.That(s.Update(25.0), Is.EqualTo(SaveConfig.MAX_TICKS_PER_FRAME));
            int total = SaveConfig.MAX_TICKS_PER_FRAME;
            while (total < 25 * hz) { int n = s.Update(0.001); Assert.That(n, Is.GreaterThan(0)); total += n; }
            Assert.That(total, Is.EqualTo(25 * hz), "the hitch is ticked out over following frames, nothing lost");
        }

        [Test]
        public void Autosave_EveryIntervalWhileDirty_AndRightAfterPulls()
        {
            var s = Open();
            int writes = _store.WriteCount;
            Play(s, 29);
            Assert.That(_store.WriteCount, Is.EqualTo(writes));
            Play(s, 1);
            Assert.That(_store.WriteCount, Is.EqualTo(writes + 1));

            Assert.That(s.Sim.Pull().Ok, Is.True, "tutorial banked pulls");
            s.Update(0.05);
            Assert.That(_store.WriteCount, Is.EqualTo(writes + 2), "a pull saves on the next Update");
            Assert.That(OnDisk().HeroBanner.TotalPulls, Is.EqualTo(1));

            s.Pause();
            Assert.That(_store.WriteCount, Is.EqualTo(writes + 3), "pause saves immediately");
        }

        [Test]
        public void SaveTooNew_AbortsOpen()
        {
            var body = SaveSerializer.Decode(SaveSerializer.Encode(TestContent.NewSim().State, new SaveMeta())).body;
            body["SchemaVersion"] = SaveSchema.Current + 1;
            _store.Primary = SaveSerializer.EncodeBody(body.ToString());
            Assert.Throws<SaveTooNewException>(() => Open());
            Assert.That(_store.WriteCount, Is.EqualTo(0));
        }
    }
}
