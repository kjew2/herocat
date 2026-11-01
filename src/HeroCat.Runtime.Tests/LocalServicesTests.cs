using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>The composition root and the M0 local seams (01 section 1.2, 08 section 1, 10 section 1.2; issues #66, #119).</summary>
    public class LocalServicesTests : TempDirTest
    {
        [Test]
        public void BuildLocal_ReturnsWorkingImplementations_OfEverySeam()   // issue #66 acceptance 1
        {
            GameState state = TestContent.NewState();
            var svc = GameServices.Build(BackendMode.Local, new ServicesOptions { DataDir = Dir, Content = TestContent.Db, State = () => state });
            Assert.That(svc.Mode, Is.EqualTo(BackendMode.Local));
            Assert.That(svc.Clock, Is.InstanceOf<ServerClock>());
            Assert.That(svc.Clock.IsAuthoritative, Is.False);
            Assert.That(svc.Saves, Is.InstanceOf<FileSaveStore>());
            Assert.That(svc.Inbox, Is.InstanceOf<LocalInboxService>());
            Assert.That(svc.Receipts, Is.InstanceOf<DisabledReceiptValidator>());
            Assert.That(svc.Config, Is.InstanceOf<LocalConfigService>());
            Assert.That(svc.Dungeons, Is.InstanceOf<LocalDungeonSeedService>());
            Assert.That(svc.Analytics, Is.SameAs(NullAnalytics.Instance));
            // TODO(#92): svc.Summon is a LocalSummonService.

            var mem = GameServices.InMemory(new FakeClock(TestContent.T0));
            Assert.That(mem.Saves, Is.InstanceOf<MemorySaveStore>());
            Assert.That(mem.Clock, Is.InstanceOf<FakeClock>());
        }

        [Test]
        public void BuildRemote_WithoutWiring_Throws_AndWithWiring_IsMarkedRemote()   // 01 section 1.2
        {
            Assert.Throws<NotSupportedException>(() => GameServices.Build(BackendMode.Remote, new ServicesOptions()));
            var fake = new FakeClock(TestContent.T0);
            var svc = GameServices.Build(BackendMode.Remote, new ServicesOptions
            {
                Remote = o => GameServices.Build(BackendMode.Local, new ServicesOptions { Clock = fake }),
            });
            Assert.That(svc.Mode, Is.EqualTo(BackendMode.Remote));
            Assert.That(svc.Clock, Is.SameAs(fake));
        }

        [Test]
        public void LocalConfig_HasEverythingOn_AndNoGate()   // 10 section 1.2 "Local seams"
        {
            var c = new LocalConfigService(TestContent.Db.Version).FetchAsync().GetAwaiter().GetResult();
            Assert.That(c.ContentVersion, Is.EqualTo(TestContent.Db.Version));
            Assert.That(c.MinClientVersion, Is.Null);
            Assert.That(c.Maintenance.Active, Is.False);
            Assert.That(c.Flags.ShopEnabled && c.Flags.DungeonsEnabled && c.Flags.CloudSaveEnabled && c.Flags.AdsEnabled, Is.True);
            foreach (Banner b in Enum.GetValues(typeof(Banner))) Assert.That(c.Flags.IsSummonEnabled(b), Is.True, b.ToString());
        }

        [Test]
        public void LocalInbox_IsEmpty_UnknownClaimIsNotFound_AndClaimsAreIdempotent()   // 08 section 5.5, 10 section 1.2
        {
            var inbox = new LocalInboxService();
            Assert.That(inbox.ListAsync().Result, Is.Empty);
            var e = Assert.Throws<ServiceException>(() => inbox.ClaimAsync("r0", "nope").GetAwaiter().GetResult());
            Assert.That(e.Code, Is.EqualTo(LocalInboxService.INBOX_NOT_FOUND));
            Assert.That(e.Kind, Is.EqualTo(ErrorClass.Rejected));

            var grant = new InboxGrantPayload("m1", new[] { new GrantLine("currency", "DIAMOND", 100) }, CurrencyReason.MAIL);
            inbox.Add(new InboxItem("m1", grant, long.MaxValue));
            var first = inbox.ClaimAsync("r1", "m1").Result;
            Assert.That(first.AlreadyClaimed, Is.False);
            Assert.That(first.Grant, Is.SameAs(grant));
            Assert.That(inbox.ClaimAsync("r1", "m1").Result, Is.SameAs(first), "same requestId replays the result");
            var again = inbox.ClaimAsync("r2", "m1").Result;
            Assert.That(again.AlreadyClaimed, Is.True, "INBOX_ALREADY_CLAIMED success-equivalent");
            Assert.That(again.Grant, Is.SameAs(grant));
            Assert.That(inbox.ListAsync().Result, Is.Empty);
        }

        [Test]
        public void Receipts_DisabledThrows_FakeGrantsIntoTheInbox_Idempotently()   // 08 section 5.5
        {
            Assert.Throws<NotSupportedException>(() => new DisabledReceiptValidator().ValidateAsync(new ReceiptRequest("r", "ios", "sku", "t", "p")));

            var svc = GameServices.Build(BackendMode.Local, new ServicesOptions
            {
                Clock = new FakeClock(TestContent.T0),
                DebugReceiptCatalog = sku => sku == "gems_small" ? new[] { new GrantLine("currency", "DIAMOND", 300) } : null,
            });
            Assert.That(svc.Receipts, Is.InstanceOf<FakeReceiptValidator>());
            var r = svc.Receipts.ValidateAsync(new ReceiptRequest("r1", "android", "gems_small", "tx1", "{}")).Result;
            Assert.That(r.Status, Is.EqualTo(ReceiptResult.GRANTED));
            var again = svc.Receipts.ValidateAsync(new ReceiptRequest("r2", "android", "gems_small", "tx1", "{}")).Result;
            Assert.That(again.Status, Is.EqualTo(ReceiptResult.ALREADY_GRANTED));
            Assert.That(again.InboxItemId, Is.EqualTo(r.InboxItemId));
            var items = svc.Inbox.ListAsync().Result;
            Assert.That(items.Select(i => i.Id), Is.EqualTo(new[] { r.InboxItemId }));
            Assert.That(items[0].Grant.Reason, Is.EqualTo(CurrencyReason.IAP));
            var bad = Assert.Throws<ServiceException>(() => svc.Receipts.ValidateAsync(new ReceiptRequest("r3", "ios", "nope", "tx2", "{}")).GetAwaiter().GetResult());
            Assert.That(bad.Code, Is.EqualTo("RECEIPT_INVALID"));
        }

        [Test]
        public void LocalDungeonSeed_PeeksTheNextDungeonDraw_WithoutWritingState()   // 01 section 8.8 step 2
        {
            var state = TestContent.NewState();
            var stream = state.Rng[RngStreams.Dungeon];
            stream.Counter = 3;
            string before = HeroCat.Sim.Replay.StateHasher.Hash(state);
            var seeds = new LocalDungeonSeedService(() => state);
            var g = seeds.RequestSeedAsync(new DungeonSeedRequest("r1", default(Dungeon), 1)).Result;
            Assert.That(RngStreams.TryParseSeed(stream.Seed, out ulong seed), Is.True);
            Assert.That(g.Seed, Is.EqualTo(SplitMix64.Derive(seed, 3UL)));
            Assert.That(g.LocalSeed, Is.True);
            Assert.That(g.GrantId, Is.EqualTo(""));
            Assert.That(seeds.RequestSeedAsync(new DungeonSeedRequest("r2", default(Dungeon), 1)).Result.Seed, Is.EqualTo(g.Seed), "not consumed");
            Assert.That(HeroCat.Sim.Replay.StateHasher.Hash(state), Is.EqualTo(before));
            Assert.Throws<InvalidOperationException>(() => new LocalDungeonSeedService(() => null).RequestSeedAsync(new DungeonSeedRequest("r", default(Dungeon), 1)).GetAwaiter().GetResult());
        }

        [Test]
        public void ALocalSession_MakesNoNetworkCalls_OverTwentyMinutes()   // 08 AC 24 (headless half; issue #119)
        {
            var net = new FailingTransport();
            var s = TestSession.Open(network: net);
            s.Play(20 * 60, 0.5);
            s.Runner.OnPause();
            s.Clock.Advance(3600);
            s.Runner.OnResume();
            s.Play(60);
            s.Runner.OnQuit();
            Assert.That(net.Calls, Is.EqualTo(0));
            Assert.That(s.Runner.TotalTicks, Is.GreaterThan(0));
        }

        sealed class FailingTransport : INetTransport
        {
            public int Calls;

            public Task<NetResponse> SendAsync(NetRequest request, CancellationToken ct = default)
            {
                Interlocked.Increment(ref Calls);
                Assert.Fail("a Local-mode session called the network: " + request.Method + " " + request.Path);
                return null;
            }
        }
    }
}
