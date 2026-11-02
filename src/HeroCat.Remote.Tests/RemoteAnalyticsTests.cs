using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Remote.Services;
using HeroCat.Remote.Transport;
using HeroCat.Runtime;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Remote.Tests
{
    /// <summary>The backend analytics uploader (issue #297) under the real AnalyticsSink and ApiClient, against <see cref="FakeApiHandler"/>.</summary>
    public class RemoteAnalyticsTests
    {
        const long T0 = 1_800_000_000;
        const string Salt = "test-salt";

        sealed class Creds : IApiCredentials
        {
            public string AccessToken { get; set; } = "token";
            public Task<bool> RefreshAsync(CancellationToken ct) => Task.FromResult(false);
        }

        /// <summary>08 section 3.5's answer: every event accepted, or the listed indexes refused.</summary>
        static FakeApiHandler Server(List<JArray> received, Func<JArray, JArray> refuse = null) =>
            new FakeApiHandler().On("POST", RemoteAnalyticsUploader.Route, call =>
            {
                var events = (JArray)JObject.Parse(call.BodyText)["events"];
                lock (received) received.Add(events);
                var rejected = refuse?.Invoke(events) ?? new JArray();
                return FakeReply.Json(200, new JObject
                {
                    ["accepted"] = events.Count - rejected.Count, ["duplicates"] = 0, ["rejected"] = rejected, ["serverTime"] = T0,
                }.ToString());
            });

        static ApiClient Client(FakeApiHandler h, IApiCredentials creds = null) =>
            new ApiClient(new ApiClientOptions { Handler = h, Delay = (d, ct) => Task.CompletedTask, Jitter = () => 0, Credentials = creds });

        static AnalyticsSink Sink(ApiClient client, out RemoteAnalyticsUploader uploader)
        {
            uploader = new RemoteAnalyticsUploader(client);
            var envelope = new AnalyticsEnvelope(new FakeClock(T0), null, Salt, 86400, Guid.NewGuid(), Guid.NewGuid(), "1.2.3", "content");
            return new AnalyticsSink(envelope, AnalyticsSink.MemoryQueue(), uploader);
        }

        static void Economy(AnalyticsSink sink, int n)
        {
            for (int i = 0; i < n; i++) sink.Track(new AnalyticsEvents.CurrencyChanged(Currency.DIAMOND, i, 1000 + i, CurrencyReason.QUEST));
        }

        static void Flush(AnalyticsSink sink) => Assert.That(sink.FlushAsync().Wait(TimeSpan.FromSeconds(30)), Is.True);

        [Test]
        public void AFlush_PostsBatchesOfAtMost100_OneRequestIdEach_AndEmptiesTheQueue()
        {
            var got = new List<JArray>();
            var server = Server(got);
            var sink = Sink(Client(server), out var up);
            Economy(sink, 250);
            var queued = sink.Queue.Peek(1000);
            Flush(sink);

            Assert.That(got.Select(b => b.Count), Is.EqualTo(new[] { 100, 100, 50 }));
            var calls = server.Calls;
            Assert.That(calls.Select(c => c.Path), Is.All.EqualTo("/analytics/events"));
            Assert.That(calls.Select(c => c.Method), Is.All.EqualTo("POST"));
            Assert.That(calls[0].Header(ApiHeaders.RequestId), Is.EqualTo(AnalyticsBatch.RequestIdFor(queued.Take(100).ToList())));
            Assert.That(calls.Select(c => c.Header(ApiHeaders.RequestId)), Is.Unique);
            // The lines go as queued, oldest first: the server reads 02 section 14.1's shape.
            Assert.That(got.SelectMany(b => b).Select(t => t.ToString(Newtonsoft.Json.Formatting.None)),
                        Is.EqualTo(queued.Select(q => JObject.Parse(q.Line).ToString(Newtonsoft.Json.Formatting.None))));
            Assert.That(sink.Queue.Count, Is.Zero);
            Assert.That((up.BatchesSent, up.EventsAccepted), Is.EqualTo((3L, 250L)));
        }

        [Test]
        public void PerEventRefusals_AreFinal_TheBatchLeavesTheQueue()
        {
            var got = new List<JArray>();
            var server = Server(got, events => new JArray(new JObject { ["index"] = 1, ["eventId"] = events[1]["eventId"], ["code"] = "EVENT_REASON_INVALID" }));
            var sink = Sink(Client(server), out var up);
            Economy(sink, 3);
            Flush(sink);
            Assert.That(sink.Queue.Count, Is.Zero);
            Assert.That((up.EventsAccepted, up.EventsRefused, up.LastRefusalCode), Is.EqualTo((2L, 1L, "EVENT_REASON_INVALID")));
            Flush(sink);
            Assert.That(server.Calls, Has.Count.EqualTo(1), "a refused event is never resent");
        }

        [Test]
        public void Offline_And5xx_KeepTheQueue_AndTheNextFlushResendsTheSameRequestId()
        {
            var got = new List<JArray>();
            var server = Server(got);
            int attempts = RuntimeConfig.HTTP_RETRY_MAX + 1;
            server.Then(Enumerable.Repeat(FakeFault.Drop, attempts).ToArray());
            var sink = Sink(Client(server), out var up);
            Economy(sink, 5);
            Flush(sink);
            Assert.That(server.Calls, Has.Count.EqualTo(attempts), "the ApiClient's backoff retries first");
            Assert.That(sink.Queue.Count, Is.EqualTo(5), "offline: the queue is intact");
            Assert.That(sink.UploadFailures, Is.EqualTo(1));

            server.Then(Enumerable.Repeat(FakeFault.Status(503, "HTTP_503"), attempts).ToArray());
            Flush(sink);
            Assert.That(sink.Queue.Count, Is.EqualTo(5), "5xx: the queue is intact");

            Flush(sink);
            var ids = server.Calls.Select(c => c.Header(ApiHeaders.RequestId)).Distinct().ToList();
            Assert.That(ids, Has.Count.EqualTo(1), "every attempt of the batch carries one X-Request-Id");
            Assert.That(got, Has.Count.EqualTo(1));
            Assert.That(sink.Queue.Count, Is.Zero);
        }

        [Test]
        public void ALostReply_IsReplayed_NotStoredTwice()
        {
            var got = new List<JArray>();
            var server = Server(got);
            server.Then(FakeFault.TimeoutAfterCommit);
            var sink = Sink(Client(server), out _);
            Economy(sink, 2);
            Flush(sink);
            Assert.That(got, Has.Count.EqualTo(1), "the server handled it once");
            Assert.That(server.Replays, Is.EqualTo(1));
            Assert.That(sink.Queue.Count, Is.Zero);
        }

        [Test]
        public void A400_DropsTheBatch_SoItCannotBlockTheQueue()
        {
            var server = Server(new List<JArray>());
            server.Then(FakeFault.Status(400, "VALIDATION"));
            var sink = Sink(Client(server), out _);
            Economy(sink, 2);
            Flush(sink);
            Assert.That(sink.Queue.Count, Is.Zero);
            Assert.That(sink.BatchesRejected, Is.EqualTo(1));
            Assert.That(server.Calls, Has.Count.EqualTo(1), "a 4xx is not retried");
        }

        [Test]
        public void SignedOut_SendsNothing_AndKeepsTheQueue_UntilThereIsAToken()
        {
            var got = new List<JArray>();
            var server = Server(got);
            var creds = new Creds { AccessToken = null };
            var sink = Sink(Client(server, creds), out _);
            Economy(sink, 2);
            Flush(sink);
            Assert.That(server.Calls, Is.Empty);
            Assert.That(sink.Queue.Count, Is.EqualTo(2));

            server.Then(FakeFault.Status(401, "UNAUTHENTICATED"));
            creds.AccessToken = "token";
            Flush(sink);
            Assert.That(sink.Queue.Count, Is.EqualTo(2), "a 401 keeps them too");
            Flush(sink);
            Assert.That(got.Single(), Has.Count.EqualTo(2));
            Assert.That(server.Calls.Last().Header(ApiHeaders.Authorization), Is.EqualTo("Bearer token"));
            Assert.That(sink.Queue.Count, Is.Zero);
        }

        [Test]
        public void OnlyEconomyEventsGoToTheBackend_WithoutAdvertisingIds()
        {
            var got = new List<JArray>();
            var server = Server(got);
            var sink = Sink(Client(server), out var up);
            sink.Track(new AnalyticsEvents.SessionStart(0, 0, true, 1200));
            sink.Track(new AnalyticsEvents.SaveFallback());
            Flush(sink);
            Assert.That(server.Calls, Is.Empty, "a batch with no economy event sends nothing");
            Assert.That(sink.Queue.Count, Is.Zero);

            sink.Track(new AnalyticsEvents.SessionStart(0, 0, false, 900));
            sink.Track("currency_changed", new Dictionary<string, object>
            {
                ["currency"] = "DIAMOND", ["delta"] = 50, ["balanceAfter"] = 640, ["reason"] = "IAP", ["advertisingId"] = "38400000-8cf0", ["gaid"] = "x",
            });
            Flush(sink);
            var sent = got.Single().Single();
            Assert.That((string)sent["name"], Is.EqualTo("currency_changed"));
            Assert.That(((JObject)sent["props"]).Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "currency", "delta", "balanceAfter", "reason" }));
            Assert.That(up.EventsSkipped, Is.EqualTo(3));
        }

        [TestCase(200, UploadOutcome.Accepted)]
        [TestCase(0, UploadOutcome.Retry)]
        [TestCase(500, UploadOutcome.Retry)]
        [TestCase(503, UploadOutcome.Retry)]
        [TestCase(429, UploadOutcome.Retry)]
        [TestCase(409, UploadOutcome.Retry)]
        [TestCase(401, UploadOutcome.Retry)]
        [TestCase(403, UploadOutcome.Retry)]
        [TestCase(426, UploadOutcome.Retry)]
        [TestCase(400, UploadOutcome.Rejected)]
        [TestCase(404, UploadOutcome.Rejected)]
        [TestCase(413, UploadOutcome.Rejected)]
        public void TheStatusTable(int status, UploadOutcome expected) =>
            Assert.That(RemoteAnalyticsUploader.OutcomeOf(status), Is.EqualTo(expected));

        [Test]
        public void RemoteWire_BuildsTheSink_OnlyWithASalt()
        {
            var server = Server(new List<JArray>());
            var api = new ApiClientOptions { Handler = server, Delay = (d, ct) => Task.CompletedTask };
            var without = RemoteGameServices.Wire(new ServicesOptions { Slots = new MemorySaveStore(), InstallId = Guid.NewGuid() }, api);
            Assert.That(without.Analytics, Is.InstanceOf<NullAnalytics>());

            var session = Guid.NewGuid();
            var with = RemoteGameServices.Wire(new ServicesOptions
            {
                Slots = new MemorySaveStore(), InstallId = Guid.NewGuid(), AnalyticsSalt = Salt, SessionId = session,
            }, new ApiClientOptions { Handler = server, Delay = (d, ct) => Task.CompletedTask, Credentials = new Creds() });
            var sink = (AnalyticsSink)with.Analytics;
            Assert.That(sink.Envelope.SessionId, Is.EqualTo(session));
            Assert.That(with.Saves, Is.Not.Null);
            sink.Track(new AnalyticsEvents.CurrencyChanged(Currency.GOLD, 5, 5, CurrencyReason.QUEST));
            Flush(sink);
            Assert.That(server.Calls.Select(c => c.Path), Is.EqualTo(new[] { "/analytics/events" }));
        }
    }
}
