using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HeroCat.Runtime.Boot;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.State;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>02 section 14 (envelope, offline queue, index) and 06 section 13's currency_changed; issue #155.</summary>
    public class AnalyticsTests : TempDirTest
    {
        const string Salt = "test-env-salt";
        static readonly Guid Session = new Guid("5e55e000-0000-4000-8000-000000000155");
        static readonly Guid Player = new Guid("91a7e200-0000-4000-8000-0000000000aa");

        static AnalyticsEnvelope Envelope(IClock clock, Func<GameState> state, string salt = Salt) =>
            new AnalyticsEnvelope(clock, state, salt, TestContent.Db.Constants.DaySeconds, Session, TestContent.Device, "1.2.3",
                                  TestContent.Db.Version);

        static GameState PlayerState(long now) =>
            HeroCat.Runtime.Profile.ProfileFactory.Create(TestContent.Db, Player, now, TestContent.Device, TestContent.Root);

        static JObject Parse(QueuedEvent e) => JObject.Parse(e.Line);

        // ------------------------------------------------------------------------------------------- envelope (14.1)

        [Test]
        public void EveryEvent_CarriesTheFullEnvelope_WithLiveValues()
        {
            var clock = new FakeClock(TestContent.T0 + 3 * TestContent.Db.Constants.DaySeconds + 5);
            var state = PlayerState(TestContent.T0);
            state.Combat.Stage = 42;
            state.DebugTainted = true;
            var sink = new AnalyticsSink(Envelope(clock, () => state), AnalyticsSink.MemoryQueue(), null);
            sink.Envelope.AgeGate = 1;
            sink.Track(new AnalyticsEvents.SaveFallback());
            sink.Track(new AnalyticsEvents.CurrencyChanged(Currency.GOLD, 10, 20, CurrencyReason.QUEST, "q_1"));

            var events = sink.Queue.Peek(10).Select(Parse).ToList();
            Assert.That(events, Has.Count.EqualTo(2));
            foreach (var o in events)
            {
                foreach (var f in AnalyticsEnvelope.Fields) Assert.That(o.ContainsKey(f), Is.True, f);
                Assert.That((long)o["ts"], Is.EqualTo(clock.NowServerSeconds));
                Assert.That((string)o["sessionId"], Is.EqualTo(Session.ToString("N")));
                Assert.That((string)o["deviceId"], Is.EqualTo(TestContent.Device.ToString("N")));
                Assert.That((string)o["clientVersion"], Is.EqualTo("1.2.3"));
                Assert.That((string)o["contentVersion"], Is.EqualTo(TestContent.Db.Version));
                Assert.That((int)o["schemaVersion"], Is.EqualTo(HeroCat.Runtime.Save.SaveSchema.Current));
                Assert.That((bool)o["debugTainted"], Is.True);
                Assert.That((bool)o["clockAuthoritative"], Is.True, "FakeClock is authoritative");
                Assert.That((long)o["dayIndex"], Is.EqualTo(3), "(ts - CreatedServerTime) / DAY_SECONDS");
                Assert.That((int)o["stage"], Is.EqualTo(42));
                Assert.That((int)o["ageGate"], Is.EqualTo(1));
                Assert.That((string)o["playerIdHash"], Is.EqualTo(AnalyticsEnvelope.HashPlayerId(Player, Salt)));
                Assert.That((string)o["eventId"], Does.Match("^[0-9a-f]{32}$"));
            }
            Assert.That((string)events[0]["name"], Is.EqualTo("save_fallback"));
            var p = (JObject)events[1]["props"];
            Assert.That(p.Properties().Select(x => x.Name), Is.EqualTo(new[] { "currency", "delta", "balanceAfter", "reason", "refId" }));
            Assert.That((string)p["reason"], Is.EqualTo("QUEST"));
            Assert.That((string)events[0]["eventId"], Is.Not.EqualTo((string)events[1]["eventId"]));
        }

        [Test]
        public void BeforeTheSaveLoads_DayIndexAndStageAreNull_ButPresent()
        {
            var sink = new AnalyticsSink(Envelope(new FakeClock(TestContent.T0), () => null), AnalyticsSink.MemoryQueue(), null);
            sink.Track(new AnalyticsEvents.SaveReject("slot0.sav", "crc"));
            var o = Parse(sink.Queue.Peek(1)[0]);
            Assert.That(o["dayIndex"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(o["stage"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(o["playerIdHash"].Type, Is.EqualTo(JTokenType.Null), "no state, no playerId");
            Assert.That((bool)o["debugTainted"], Is.False);
        }

        [Test]
        public void PlayerIdHash_IsSaltedSha256_DiffersPerEnvironment_AndNullForAGuest()
        {
            string dev = AnalyticsEnvelope.HashPlayerId(Player, "dev-salt");
            string prod = AnalyticsEnvelope.HashPlayerId(Player, "prod-salt");
            Assert.That(dev, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(prod, Is.Not.EqualTo(dev));
            Assert.That(AnalyticsEnvelope.HashPlayerId(Player, "dev-salt"), Is.EqualTo(dev), "stable");
            Assert.That(AnalyticsEnvelope.HashPlayerId(Guid.Empty, "dev-salt"), Is.Null, "an unlinked guest has no playerId");
            Assert.Throws<ArgumentException>(() => Envelope(new FakeClock(0), () => null, salt: ""));
        }

        [Test]
        public void TheQueueFile_NeverHoldsTheRawPlayerId()
        {
            var state = PlayerState(TestContent.T0);
            var queue = AnalyticsSink.FileQueue(Dir);
            var sink = new AnalyticsSink(Envelope(new FakeClock(TestContent.T0), () => state), queue, null);
            sink.Track(new AnalyticsEvents.SessionStart(0, 0, true, 12.5));
            sink.Track("summon", new Dictionary<string, object> { ["playerId"] = Player.ToString(), ["banner"] = "WEAPON" });
            var text = File.ReadAllText(Path.Combine(Dir, "analytics", "queue.ndjson"));
            foreach (var form in new[] { "D", "N", "B" })
                Assert.That(text, Does.Not.Contain(Player.ToString(form)).IgnoreCase);
            Assert.That(sink.DroppedProps, Is.EqualTo(1), "a raw playerId prop is dropped");
            Assert.That(text, Does.Contain(AnalyticsEnvelope.HashPlayerId(Player, Salt)));
        }

        [Test]
        public void BadNamesAndProps_AreDropped_NeverThrown()
        {
            var sink = new AnalyticsSink(Envelope(new FakeClock(TestContent.T0), () => null), AnalyticsSink.MemoryQueue(), null);
            sink.Track("SessionStart");
            sink.Track("session__start");
            sink.Track(null);
            Assert.That(sink.DroppedInvalid, Is.EqualTo(3));
            sink.Track("summon", new Dictionary<string, object> { ["ok"] = 1, ["Bad_Key"] = 2, ["ts"] = 3, ["stage"] = 4 });
            var p = (JObject)Parse(sink.Queue.Peek(1)[0])["props"];
            Assert.That(p.Properties().Select(x => x.Name), Is.EqualTo(new[] { "ok" }), "not camelCase, or an envelope field");
            Assert.That(sink.DroppedProps, Is.EqualTo(3));
        }

        // ------------------------------------------------------------------------------------------- queue (14.2)

        [Test]
        public void Overflow_DropsTheOldest()
        {
            var q = new AnalyticsQueue(new MemoryAnalyticsQueueStore(), AnalyticsSink.EventIdOf, capacity: 5);
            for (int i = 0; i < 8; i++) q.Enqueue(Line(i));
            Assert.That(q.Count, Is.EqualTo(5));
            Assert.That(q.Dropped, Is.EqualTo(3));
            Assert.That(q.Peek(10).Select(e => e.Id), Is.EqualTo(Enumerable.Range(3, 5).Select(Id)));
        }

        [Test]
        public void DefaultCapacity_IsANALYTICS_QUEUE_MAX()
        {
            Assert.That(AnalyticsSink.MemoryQueue().Capacity, Is.EqualTo(10000));
            Assert.That(RuntimeConfig.ANALYTICS_BATCH_MAX, Is.EqualTo(100));
            Assert.That(RuntimeConfig.CURRENCY_AGG_SEC, Is.EqualTo(60));
        }

        [Test]
        public void ARestart_ResumesTheQueueFromDisk_SkippingATornLastLine()
        {
            var a = AnalyticsSink.FileQueue(Dir);
            for (int i = 0; i < 4; i++) a.Enqueue(Line(i));
            var file = Path.Combine(Dir, "analytics", "queue.ndjson");
            File.AppendAllText(file, "{\"name\":\"summon\",\"eventId\":\"ab");   // killed mid-append
            var b = AnalyticsSink.FileQueue(Dir);
            Assert.That(b.Peek(10).Select(e => e.Id), Is.EqualTo(Enumerable.Range(0, 4).Select(Id)));
            Assert.That(b.SkippedOnLoad, Is.EqualTo(1));
            Assert.That(File.ReadAllLines(file), Has.Length.EqualTo(4), "the torn line is compacted away");
        }

        [Test]
        public void TheFile_IsCompacted_AndALoadKeepsTheNewestCapacityLines()
        {
            var store = new MemoryAnalyticsQueueStore();
            var q = new AnalyticsQueue(store, AnalyticsSink.EventIdOf, capacity: 3);
            for (int i = 0; i < 20; i++) q.Enqueue(Line(i));
            Assert.That(store.Lines.Count, Is.LessThanOrEqualTo(2 * 3 + 1), "the file stays bounded");
            var reloaded = new AnalyticsQueue(store, AnalyticsSink.EventIdOf, capacity: 3);
            Assert.That(reloaded.Peek(10).Select(e => e.Id), Is.EqualTo(new[] { Id(17), Id(18), Id(19) }));
            Assert.That(store.Lines, Has.Count.EqualTo(3));
        }

        [Test]
        public void Flush_UploadsInBatchesOfAtMost100_AndEmptiesTheQueue()
        {
            var up = new MemoryAnalyticsUploader();
            var sink = new AnalyticsSink(Envelope(new FakeClock(TestContent.T0), () => null), AnalyticsSink.MemoryQueue(), up);
            for (int i = 0; i < 250; i++) sink.Track(new AnalyticsEvents.SaveRepaired("P" + i));
            Assert.That(sink.FlushAsync().Wait(TimeSpan.FromSeconds(30)), Is.True);
            Assert.That(up.Batches.Select(b => b.Events.Count), Is.EqualTo(new[] { 100, 100, 50 }));
            Assert.That(up.Batches.SelectMany(b => b.Events).Select(e => (string)Parse(e)["props"]["rule"]),
                        Is.EqualTo(Enumerable.Range(0, 250).Select(i => "P" + i)), "oldest first, each once");
            Assert.That(up.Batches.Select(b => b.RequestId), Is.Unique.And.All.Match("^[0-9a-f]{32}$"));
            Assert.That(sink.Queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void ARetryableFailure_KeepsTheBatch_AndTheRetryReusesItsRequestId()
        {
            var up = new MemoryAnalyticsUploader();
            up.Next.Enqueue(UploadOutcome.Retry);
            var sink = new AnalyticsSink(Envelope(new FakeClock(TestContent.T0), () => null), AnalyticsSink.MemoryQueue(), up);
            for (int i = 0; i < 120; i++) sink.Track(new AnalyticsEvents.SaveRepaired("P" + i));
            sink.FlushAsync().Wait();
            Assert.That(up.Batches, Has.Count.EqualTo(1), "a retryable failure stops the flush");
            Assert.That(sink.Queue.Count, Is.EqualTo(120));
            Assert.That(sink.UploadFailures, Is.EqualTo(1));
            sink.FlushAsync().Wait();
            Assert.That(up.Batches.Select(b => b.Events.Count), Is.EqualTo(new[] { 100, 100, 20 }));
            Assert.That(up.Batches[1].RequestId, Is.EqualTo(up.Batches[0].RequestId), "same events, same X-Request-Id");
            Assert.That(sink.Queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void AThrowingUploader_IsARetry_AndARejectedBatchIsDropped()
        {
            var up = new MemoryAnalyticsUploader { Throw = new IOException("offline") };
            var sink = new AnalyticsSink(Envelope(new FakeClock(TestContent.T0), () => null), AnalyticsSink.MemoryQueue(), up);
            for (int i = 0; i < 3; i++) sink.Track(new AnalyticsEvents.SaveFallback());
            Assert.DoesNotThrow(() => sink.FlushAsync().Wait());
            Assert.That(sink.Queue.Count, Is.EqualTo(3));
            up.Throw = null;
            up.Next.Enqueue(UploadOutcome.Rejected);
            sink.FlushAsync().Wait();
            Assert.That(sink.Queue.Count, Is.EqualTo(0));
            Assert.That(sink.BatchesRejected, Is.EqualTo(1));
        }

        [Test]
        public void EventsTrackedWhileOffline_SurviveAKill_AndUploadAfterTheRestart()
        {
            var clock = new FakeClock(TestContent.T0);
            var offline = new MemoryAnalyticsUploader { Throw = new IOException("offline") };
            var first = new AnalyticsSink(Envelope(clock, () => null), AnalyticsSink.FileQueue(Dir), offline);
            first.Track(new AnalyticsEvents.ClockAnomaly(AnalyticsEvents.ClockAnomaly.BACKWARDS, -60));
            first.Track(new AnalyticsEvents.SaveWriteFailed(1));
            first.FlushAsync().Wait();
            // the process dies here; the next one resumes the same file
            var up = new MemoryAnalyticsUploader();
            var second = new AnalyticsSink(Envelope(clock, () => null), AnalyticsSink.FileQueue(Dir), up);
            Assert.That(second.Queue.Count, Is.EqualTo(2));
            second.FlushAsync().Wait();
            Assert.That(up.Batches.Single().Events.Select(e => (string)Parse(e)["name"]), Is.EqualTo(new[] { "clock_anomaly", "save_write_failed" }));
            Assert.That(File.ReadAllText(Path.Combine(Dir, "analytics", "queue.ndjson")), Is.Empty);
        }

        [Test]
        public void Flush_RaisesFlushing_BeforeTheUpload()
        {
            var up = new MemoryAnalyticsUploader();
            var sink = new AnalyticsSink(Envelope(new FakeClock(TestContent.T0), () => null), AnalyticsSink.MemoryQueue(), up);
            sink.Flushing += () => sink.Track(new AnalyticsEvents.SaveFallback());
            sink.FlushAsync().Wait();
            Assert.That(up.Batches.Single().Events, Has.Count.EqualTo(1), "the hook's event is in this flush");
        }

        // ------------------------------------------------------------------------------------------- catalogue (14.4)

        [Test]
        public void TheCatalogue_MatchesThe02Section14_4Index()
        {
            var spec = File.ReadAllText(Path.Combine(TestContent.RepoRoot, "specs", "02-domain-model-and-persistence.md"));
            int from = spec.IndexOf("### 14.4 Event index", StringComparison.Ordinal);
            int to = spec.IndexOf("07 defines no events", from, StringComparison.Ordinal);
            Assert.That(from, Is.GreaterThan(0));
            var index = Regex.Matches(spec.Substring(from, to - from), @"`([a-z0-9_*]+)`").Select(m => m.Groups[1].Value).ToList();
            foreach (var name in index)
            {
                if (name.EndsWith("*"))
                    Assert.That(AnalyticsNames.All.Any(n => n.StartsWith(name.TrimEnd('*'), StringComparison.Ordinal)), Is.True, name);
                else
                    Assert.That(AnalyticsNames.IsKnown(name), Is.True, name + " is indexed but not in AnalyticsNames");
            }
            foreach (var name in AnalyticsNames.All)
                Assert.That(index.Any(i => i == name || (i.EndsWith("*") && name.StartsWith(i.TrimEnd('*'), StringComparison.Ordinal))), Is.True,
                            name + " is in AnalyticsNames but not in the 02 section 14.4 index");
            Assert.That(AnalyticsNames.All, Is.Unique.And.All.Matches<string>(AnalyticsNames.IsSnakeCase));
            Assert.That(AnalyticsNames.Economy, Is.SubsetOf(AnalyticsNames.All));
            Assert.That(AnalyticsNames.IsEconomy("currency_changed") && !AnalyticsNames.IsEconomy("session_start"), Is.True);
        }

        [Test]
        public void TypedEvents_UseKnownNames_AndCamelCaseProps_WithoutEnvelopeFields()
        {
            var all = new AnalyticsEvent[]
            {
                new AnalyticsEvents.CurrencyChanged(Currency.GOLD, 1, 2, CurrencyReason.KILL, "x"),
                new AnalyticsEvents.SessionStart(1, 2, true, 3),
                new AnalyticsEvents.SessionEnd(1, 2, 3, 4, 5, "pause"),
                new AnalyticsEvents.CloudSaveUploaded(1, 2, "pause", 3, new[] { "f" }),
                new AnalyticsEvents.CloudConflict("L", "R", 1, 2, 3),
                new AnalyticsEvents.CloudAutoresolved("L"),
                new AnalyticsEvents.SaveFallback(),
                new AnalyticsEvents.SaveCorrupt("p"),
                new AnalyticsEvents.SaveCorruptAll("s"),
                new AnalyticsEvents.SaveRepaired("P1"),
                new AnalyticsEvents.SaveReject("f", "r"),
                new AnalyticsEvents.SaveWriteFailed(1),
                new AnalyticsEvents.MigrationApplied(1, 2),
                new AnalyticsEvents.ClockAnomaly(AnalyticsEvents.ClockAnomaly.RESYNC, 400),
            };
            foreach (var e in all)
            {
                Assert.That(AnalyticsNames.IsKnown(e.Name), Is.True, e.Name);
                foreach (var k in e.Props().Keys)
                {
                    Assert.That(AnalyticsNames.IsCamelCase(k), Is.True, e.Name + "." + k);
                    Assert.That(AnalyticsEnvelope.Fields, Does.Not.Contain(k), e.Name + "." + k);
                }
            }
            Assert.That(new AnalyticsEvents.SessionStart(1, 2, true, 3).Props().Keys,
                        Is.EqualTo(new[] { "offlineElapsedSec", "offlineCredited", "coldBoot", "bootMs" }), "02 section 14.3");
        }

        // ------------------------------------------------------------------------------------------- currency_changed (06 section 13)

        [Test]
        public void OneSimulatedHourOfFarming_KillRowsAtMost60PerCurrency_AndRowsSumToTheWalletDelta()   // 06 AC 25
        {
            var s = TestSession.Open();
            int before = s.Analytics.Events.Count;
            double gold0 = s.Sim.State.Wallet.Gold;
            s.Play(3600);
            s.Runner.OnPause();   // flushes the open window
            AssertHourOfRows(s, before, gold0);
        }

        [Test]
        public void CoalescedKills_StillSumExactly()   // 01 section 2.3: a debug "Run 1 h" folds kills into EnemyKilledBatch
        {
            var s = TestSession.Open();
            int before = s.Analytics.Events.Count;
            double gold0 = s.Sim.State.Wallet.Gold;
            double folded = 0;
            s.Bus.Subscribe<HeroCat.Sim.Events.EnemyKilledBatch>(b => folded += b.KillGold);
            s.Runner.DebugAdvance(3600L * TestContent.Db.Constants.TickHz);
            s.Runner.OnPause();
            Assert.That(folded, Is.GreaterThan(0), "the hour coalesced, so KillGold carried the folded KILL gold");
            AssertHourOfRows(s, before, gold0);
        }

        static void AssertHourOfRows(TestSession s, int before, double gold0)
        {
            var rows = s.Analytics.Events.Skip(before).Where(e => e.Name == "currency_changed").Select(e => e.Props).ToList();
            var kill = rows.Where(p => (string)p["reason"] == "KILL").ToList();
            Assert.That(kill, Is.Not.Empty, "the hero farmed");
            foreach (var g in kill.GroupBy(p => (string)p["currency"]))
                Assert.That(g.Count(), Is.LessThanOrEqualTo(3600 / RuntimeConfig.CURRENCY_AGG_SEC), g.Key);
            double sum = rows.Where(p => (string)p["currency"] == "GOLD").Sum(p => (double)p["delta"]);
            double delta = s.Sim.State.Wallet.Gold - gold0;
            Assert.That(delta, Is.GreaterThan(0));
            Assert.That(sum, Is.EqualTo(delta).Within(1e-9 * Math.Max(1, Math.Abs(delta))), "double gold, no rounding to integers");
            var g0 = kill.First(p => (string)p["currency"] == "GOLD");
            Assert.That(g0.Keys, Is.EquivalentTo(new[] { "currency", "delta", "balanceAfter", "reason" }));
        }

        [Test]
        public void NonKillReasons_AreOneRowPerApply_WithTheApplysBalance()
        {
            var s = TestSession.Open();   // boot: the welcome TUTORIAL grant and the first LOGIN
            var login = s.Analytics.Events.Where(e => e.Name == "currency_changed" && (string)e.Props["reason"] == "LOGIN").ToList();
            Assert.That(login, Has.Count.EqualTo(1));
            Assert.That(login[0].Props["currency"], Is.EqualTo("DIAMOND"));
            Assert.That(login[0].Props["delta"], Is.EqualTo((double)TestContent.Db.Constants.LoginDiamonds));
            Assert.That(login[0].Props["balanceAfter"], Is.EqualTo((double)s.Sim.State.Wallet.Diamond));
        }

        [Test]
        public void TheKillWindow_ClosesOnTheBoundary_AndOnFlush()
        {
            var s = TestSession.Open();
            int before = s.Analytics.Events.Count;
            s.Play(30);
            Assert.That(KillRows(s, before), Is.Zero, "the window is still open after 30 s");
            s.Play(45);
            Assert.That(KillRows(s, before), Is.GreaterThan(0), "a kill past 60 s closed the first window");
            int closed = KillRows(s, before);
            s.Analytics.Flush();
            Assert.That(KillRows(s, before), Is.GreaterThan(closed), "a flush emits the open window");
        }

        static int KillRows(TestSession s, int before) =>
            s.Analytics.Events.Skip(before).Count(e => e.Name == "currency_changed" && (string)e.Props["reason"] == "KILL");

        [Test]
        public void SessionStart_HasThe14_3Fields()
        {
            var s = TestSession.Open();
            var e = s.Analytics.Events.Single(x => x.Name == "session_start");
            Assert.That(e.Props.Keys, Is.EquivalentTo(new[] { "offlineElapsedSec", "offlineCredited", "coldBoot", "bootMs" }));
            Assert.That(e.Props["coldBoot"], Is.True);
        }

        [Test]
        public void ALocalBuild_WithoutASink_AttachesNothing_AndWritesNoAnalyticsFiles()   // bots and Local mode
        {
            var r = BootFlow.Run(new BootOptions
            {
                Content = new Func<ContentDb>[] { () => TestContent.Db },
                Services = (db, state) => GameServices.Local(Dir, state, db),
                NewProfileRngRoot = TestContent.Root,
                Mono = new FakeMonoClock(),
                Runner = new SessionRunnerOptions { Mono = new FakeMonoClock(), TickBudgetMs = 0 },
            });
            Assert.That(r.Services.Analytics, Is.SameAs(NullAnalytics.Instance));
            Assert.That(r.CurrencyAnalytics, Is.Null);
            r.Runner.OnQuit();
            Assert.That(Directory.Exists(Path.Combine(Dir, "analytics")), Is.False);
        }

        static string Id(int i) => i.ToString("x32");

        static QueuedEvent Line(int i) =>
            new QueuedEvent(Id(i), new JObject { ["name"] = "summon", ["eventId"] = Id(i) }.ToString(Newtonsoft.Json.Formatting.None));
    }
}
