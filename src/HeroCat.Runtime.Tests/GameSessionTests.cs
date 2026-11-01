using System;
using System.Linq;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    public class GameSessionTests
    {
        FakeClock _clock;
        MemorySaveStore _store;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeClock(TestContent.T0);
            _store = new MemorySaveStore();
        }

        GameSession Open() => GameSession.Open(TestContent.Db, GameServices.InMemory(_clock, _store),
                                               new GameSessionOptions { NewProfileRngRoot = TestContent.Root, InstallId = TestContent.Device });

        [Test]
        public void NewProfile_IsCreated_Granted_LoggedIn_AndSaved()
        {
            var s = Open();
            var k = TestContent.Db.Constants;
            Assert.That(s.IsNewProfile, Is.True);
            Assert.That(_store.WriteCount, Is.EqualTo(1));
            Assert.That(s.State.Tutorial.TutorialDiamondsGranted, Is.True);
            Assert.That(s.State.Wallet.Diamond, Is.EqualTo(1500 + k.LoginDiamonds));
            Assert.That(s.State.Time.DayKey, Is.EqualTo(TestContent.T0 / k.DaySeconds));
            var changes = s.TakeEvents().OfType<CurrencyChanged>().ToList();
            Assert.That(changes.Select(c => c.Reason), Is.EqualTo(new[] { CurrencyReason.LOGIN }), "the tutorial grant happens before the sim exists");
        }

        [Test]
        public void Reopen_LoadsTheSave_AndDoesNotReGrant()
        {
            var a = Open();
            a.Update(10);
            a.SaveNow();
            long diamonds = a.State.Wallet.Diamond;
            _clock.Advance(3600);
            var b = Open();
            Assert.That(b.IsNewProfile, Is.False);
            Assert.That(b.LoadResult.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(b.State.Wallet.Diamond, Is.EqualTo(diamonds), "same day: no second login grant");
        }

        [Test]
        public void Update_TicksAtTickHz_WithPerFrameCap()
        {
            var s = Open();
            long t0 = s.Sim.Tick;
            Assert.That(s.Update(1.0), Is.EqualTo(TestContent.Db.Constants.TickHz));
            Assert.That(s.Update(20.0), Is.EqualTo(SaveConfig.MAX_TICKS_PER_FRAME));
            Assert.That(s.Sim.Tick - t0, Is.EqualTo(TestContent.Db.Constants.TickHz + SaveConfig.MAX_TICKS_PER_FRAME));
        }

        [Test]
        public void Resume_NextDay_RollsTheDay_AndPaysOneLogin()
        {
            var s = Open();
            long d0 = s.State.Wallet.Diamond;
            s.Pause();
            _clock.Advance(3 * 86400);
            s.Resume();
            Assert.That(s.State.Wallet.Diamond, Is.EqualTo(d0 + TestContent.Db.Constants.LoginDiamonds), "lazy single reset");
            Assert.That(s.State.Time.LastTickServerTime, Is.EqualTo(_clock.NowServerSeconds), "ComputeOfflineCatchup stamped the anchor");
        }

        [Test]
        public void SaveTooNew_AbortsOpen()
        {
            var body = SaveSerializer.ToJson(TestContent.NewState());
            body["SchemaVersion"] = 99;
            _store.Primary = SaveSerializer.EncodeBody(body.ToString());
            Assert.Throws<SaveTooNewException>(() => Open());
            Assert.That(_store.WriteCount, Is.EqualTo(0));
        }
    }
}
