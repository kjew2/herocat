using System;
using System.Linq;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Runtime.Summons;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Replay;
using HeroCat.Sim.Rng;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>04 sections 10 and 12 (issues #6, #13, #92): the local roll reads the state and never writes it.</summary>
    public class SummonServiceTests
    {
        static LocalSummonService Service(Sim.State.GameState s) => new LocalSummonService(() => s, TestContent.Db, () => TestContent.T0);

        [Test]
        public void Pull_NeverWritesTheState()
        {
            var s = TestContent.NewState();
            string before = StateHasher.Hash(s);
            var r = Service(s).Pull(SummonRequest.New(Banner.WEAPON, 10, SummonPayment.Diamonds));
            Assert.That(r.IsOk, Is.True, r.ToString());
            Assert.That(StateHasher.Hash(s), Is.EqualTo(before));
            var p = r.Value;
            Assert.That(p.Pulls.Length, Is.EqualTo(10));
            Assert.That(p.StreamCounterAfter, Is.EqualTo(s.Rng[RngStreams.SummonWeapon].Counter + 30));
            Assert.That(p.BalancesAfter, Is.Null, "Local mode");
            Assert.That(p.Debits.Single().Currency, Is.EqualTo(Currency.DIAMOND));
        }

        [Test]
        public void Pull_RefusesBeforeDrawing_WhenUnaffordable()
        {
            var s = TestContent.NewState();
            string before = StateHasher.Hash(s);
            var svc = Service(s);
            Assert.That(svc.Pull(SummonRequest.New(Banner.WEAPON, 1, SummonPayment.Tickets)).Error, Is.EqualTo(SummonError.NotEnoughTickets));
            s.Wallet.Diamond = 0;
            Assert.That(svc.Pull(SummonRequest.New(Banner.SKILL, 1, SummonPayment.Diamonds)).Error, Is.EqualTo(SummonError.NotEnoughDiamonds));
            s.Wallet.Diamond = TestContent.NewState().Wallet.Diamond;
            Assert.That(StateHasher.Hash(s), Is.EqualTo(before));
        }

        [Test]
        public void SameRequest_OnAnUnchangedStream_ReturnsTheSamePayload()   // 01 section 8.5.1 step 5
        {
            var s = TestContent.NewState();
            var svc = Service(s);
            var req = SummonRequest.New(Banner.SKILL, 1, SummonPayment.Diamonds);
            Assert.That(svc.Pull(req).Value, Is.SameAs(svc.Pull(req).Value));
        }

        [Test]
        public void GameSession_SummonAsync_AppliesThroughApplySummonResult_AndSaves()
        {
            var clock = new FakeClock(TestContent.T0);
            var store = new MemorySaveStore();
            var session = GameSession.Open(TestContent.Db, GameServices.InMemory(clock, store),
                                           new GameSessionOptions { NewProfileRngRoot = TestContent.Root, InstallId = TestContent.Device });
            session.TakeEvents();
            long diamonds = session.State.Wallet.Diamond;
            int writes = store.WriteCount;
            var r = session.SummonAsync(Banner.WEAPON, 1, SummonPayment.Diamonds).GetAwaiter().GetResult();
            Assert.That(r.IsOk, Is.True, r.ToString());
            Assert.That(session.State.Wallet.Diamond, Is.EqualTo(diamonds - TestContent.Db.Banners[Banner.WEAPON].PullCostDiamonds));
            Assert.That(session.State.Banners[Banner.WEAPON].TotalPulls, Is.EqualTo(1));
            Assert.That(session.TakeEvents().OfType<ItemAcquired>().Count(), Is.EqualTo(1));
            Assert.That(store.WriteCount, Is.GreaterThan(writes));
        }
    }
}
