using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Remote.Transport;
using HeroCat.Runtime;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Remote.Tests
{
    /// <summary>
    /// 01 section 5 ApiClient (issue #143) against the fault-injecting <see cref="FakeApiHandler"/>: the header set,
    /// X-Server-Time forwarding, the 5.2 classification and retry policy, idempotent replay (01 AC 7, client half), the
    /// 401 refresh, and the threading rule of section 4. Delays are recorded, never slept.
    /// </summary>
    public class ApiClientTests
    {
        const long ServerNow = 1_800_000_000;
        const string Rid = "0123456789abcdef0123456789abcdef";

        sealed class Observer : IServerTimeObserver
        {
            public readonly ConcurrentQueue<(long time, long rtt)> Seen = new ConcurrentQueue<(long, long)>();
            public void Observe(long serverTimeSec, long rttMs) => Seen.Enqueue((serverTimeSec, rttMs));
        }

        sealed class Credentials : IApiCredentials
        {
            public string AccessToken { get; set; } = "access-1";
            public int Refreshes;
            public bool RefreshResult = true;
            public TaskCompletionSource<bool> Hold;

            public async Task<bool> RefreshAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref Refreshes);
                if (Hold != null) await Hold.Task.ConfigureAwait(false);
                if (RefreshResult) AccessToken = "access-" + (Refreshes + 1);
                return RefreshResult;
            }
        }

        sealed class Harness
        {
            public readonly FakeApiHandler Server = new FakeApiHandler { ServerTime = () => ServerNow };
            public readonly Observer Clock = new Observer();
            public readonly Credentials Auth = new Credentials();
            public readonly List<TimeSpan> Delays = new List<TimeSpan>();
            public readonly FakeMonoClock Mono = new FakeMonoClock();
            public readonly ApiClient Client;
            public double JitterValue = 0.5;

            public Harness(Action<ApiClientOptions> configure = null)
            {
                var o = new ApiClientOptions
                {
                    BaseUrl = "https://api.test.whiskerhero.example/v1",
                    Handler = Server,
                    ClientVersion = "1.4.2+318",
                    ContentVersion = () => "2026.11.03",
                    DeviceId = "6f1c2e8a-0000-4000-8000-000000000001",
                    Locale = () => "de-DE",
                    Credentials = Auth,
                    ServerTime = Clock,
                    Mono = Mono,
                    Jitter = () => JitterValue,
                    Delay = (d, ct) => { lock (Delays) Delays.Add(d); return Task.CompletedTask; },
                };
                configure?.Invoke(o);
                Client = new ApiClient(o);
            }

            public ApiResponse<JToken> Send(ApiRequest req) => Client.SendAsync<JToken>(req).GetAwaiter().GetResult();
        }

        static ApiRequest Pull(string rid = Rid) => ApiRequest.Mutation(HttpMethod.Post, "/summon/pull", new { bannerId = "weapon", count = 10 }, rid);

        // ------------------------------------------------------------------------------------------- headers (5.1)

        [Test]
        public void EveryRequest_CarriesTheHeaderSet_AMutationItsOwnRequestId_AReadAFreshOne()
        {
            var h = new Harness();
            h.Server.On("POST", "/summon/pull", 200, new { ok = true }).On("GET", "/inbox", 200, new { items = new object[0] });

            Assert.That(h.Send(Pull()).Ok, Is.True);
            h.Send(ApiRequest.Get("/inbox"));
            h.Send(ApiRequest.Get("/inbox"));

            var calls = h.Server.Calls;
            var post = calls[0];
            Assert.That(post.Path, Is.EqualTo("/summon/pull"), "relative to the /v1 base URL");
            Assert.That(post.Header("Authorization"), Is.EqualTo("Bearer access-1"));
            Assert.That(post.Header("X-Request-Id"), Is.EqualTo(Rid));
            Assert.That(post.Header("X-Client-Version"), Is.EqualTo("1.4.2+318"));
            Assert.That(post.Header("X-Content-Version"), Is.EqualTo("2026.11.03"));
            Assert.That(post.Header("X-Device-Id"), Is.EqualTo("6f1c2e8a-0000-4000-8000-000000000001"));
            Assert.That(post.Header("Accept-Language"), Is.EqualTo("de-DE"));
            Assert.That(post.ContentType, Does.StartWith("application/json"));
            Assert.That(JObject.Parse(post.BodyText)["count"].Value<int>(), Is.EqualTo(10));

            string r1 = calls[1].Header("X-Request-Id"), r2 = calls[2].Header("X-Request-Id");
            Assert.That(r1, Does.Match("^[0-9a-f]{32}$"));
            Assert.That(r2, Is.Not.EqualTo(r1), "a fresh id per read");
        }

        [Test]
        public void ARequestWithoutAuth_SendsNoAuthorization_AndAByteBodyGoesAsOctetStream()
        {
            var h = new Harness();
            h.Server.On("GET", "/time", 200, new { serverTime = ServerNow }).On("PUT", "/save", 200, new { etag = "e1" });
            h.Send(ApiRequest.Get("/time", requiresAuth: false));
            h.Send(ApiRequest.Mutation(HttpMethod.Put, "/save", new byte[] { 1, 2, 3 }, Rid) with
            {
                Headers = new Dictionary<string, string> { ["If-Match"] = "*" },
            });
            var calls = h.Server.Calls;
            Assert.That(calls[0].Header("Authorization"), Is.Null);
            Assert.That(calls[1].ContentType, Is.EqualTo("application/octet-stream"));
            Assert.That(calls[1].Body, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(calls[1].Header("If-Match"), Is.EqualTo("*"));
        }

        [Test]
        public void AMutation_WithoutA32HexRequestId_IsAProgrammingError()
        {
            var h = new Harness();
            Assert.ThrowsAsync<ArgumentException>(() => h.Client.SendAsync<JToken>(new ApiRequest(HttpMethod.Post, "/summon/pull", new { })));
            Assert.ThrowsAsync<ArgumentException>(() => h.Client.SendAsync<JToken>(Pull("not-hex")));
            Assert.That(h.Server.Calls, Is.Empty);
        }

        // ------------------------------------------------------------------------------------------- X-Server-Time

        [Test]
        public void XServerTime_OnAnyResponse_SuccessOrError_ReachesServerClockObserve_WithTheRoundTrip()
        {
            var h = new Harness();
            h.Mono.AutoStepTicks = FakeMonoClock.TicksPerSecond / 10;   // each mono read advances 100 ms
            h.Server.On("GET", "/ok", 200, new { }).On("GET", "/bad", 402, null);
            h.Server.On("GET", "/bad", _ => FakeReply.Error(402, "INSUFFICIENT_FUNDS"));

            var ok = h.Send(ApiRequest.Get("/ok"));
            var bad = h.Send(ApiRequest.Get("/bad"));
            Assert.That(ok.ServerTimeSec, Is.EqualTo(ServerNow));
            Assert.That(bad.Ok, Is.False);
            Assert.That(bad.ServerTimeSec, Is.EqualTo(ServerNow));
            var seen = h.Clock.Seen.ToArray();
            Assert.That(seen.Select(s => s.time), Is.EqualTo(new[] { ServerNow, ServerNow }), "the error's X-Server-Time too");
            Assert.That(seen.All(s => s.rtt == 100), Is.True, "rtt from the injected monotonic clock");

            h.Server.ServerTime = null;   // a response without the header observes nothing
            h.Send(ApiRequest.Get("/ok"));
            Assert.That(h.Clock.Seen.Count, Is.EqualTo(2));
        }

        [Test]
        public void ServerClock_IsResyncedByOrdinaryTraffic()
        {
            var mono = new FakeMonoClock();
            var clock = new ServerClock(device: new FakeClock(1000), mono: mono);
            var h = new Harness(o => { o.ServerTime = clock; o.Mono = mono; });
            h.Server.On("GET", "/inbox", 200, new { });
            Assert.That(clock.IsAuthoritative, Is.False);
            h.Send(ApiRequest.Get("/inbox"));
            Assert.That(clock.IsAuthoritative, Is.True);
            Assert.That(clock.NowServerSeconds, Is.EqualTo(ServerNow));
        }

        // ------------------------------------------------------------------------------------------- 01 AC 7 (client half)

        [Test]
        public void Ac7_ATimeoutAfterTheServerCommitted_IsRetriedWithTheSameRequestId_AndTheFlowAppliesExactlyOneResult()
        {
            var h = new Harness();
            int debits = 0;
            h.Server.On("POST", "/summon/pull", call =>
            {
                debits++;
                return FakeReply.Json(200, new { requestId = call.Header("X-Request-Id"), items = new[] { "w1", "w2" }, diamonds = 640 });
            });
            h.Server.Then(FakeFault.TimeoutAfterCommit);

            int applied = 0;
            var r = h.Send(Pull());
            if (r.Ok) applied++;   // the flow applies what SendAsync returns, once

            Assert.That(r.Ok, Is.True, "a replayed 200 is treated like a first 200");
            Assert.That(r.Attempts, Is.EqualTo(2));
            Assert.That(h.Server.Calls.Select(c => c.Header("X-Request-Id")), Is.EqualTo(new[] { Rid, Rid }));
            Assert.That(debits, Is.EqualTo(1), "one server-side debit");
            Assert.That(h.Server.Replays, Is.EqualTo(1));
            Assert.That(applied, Is.EqualTo(1));
            Assert.That((string)r.Body["requestId"], Is.EqualTo(Rid));
            Assert.That(r.Body["items"].Values<string>(), Is.EqualTo(new[] { "w1", "w2" }), "the identical payload");
        }

        [Test]
        public void ADroppedConnection_AndAnInFlight409_RetryTheMutationWithTheSameRequestId()
        {
            var h = new Harness();
            int handled = 0;
            h.Server.On("POST", "/summon/pull", _ => { handled++; return FakeReply.Json(200, new { n = handled }); });
            h.Server.Then(FakeFault.Drop, FakeFault.InFlight, FakeFault.TimeoutBeforeCommit);
            var r = h.Send(Pull());
            Assert.That(r.Ok, Is.True);
            Assert.That(r.Attempts, Is.EqualTo(4));
            Assert.That(h.Server.Calls.All(c => c.Header("X-Request-Id") == Rid), Is.True);
            Assert.That(handled, Is.EqualTo(1));
            Assert.That(h.Delays[1], Is.EqualTo(TimeSpan.FromSeconds(1)), "IDEMPOTENCY_IN_FLIGHT waits its retryAfterSec");
        }

        [Test]
        public void ANonIdempotentRequest_IsNotRetriedOnOfflineOrTimeout()
        {
            var h = new Harness();
            h.Server.On("POST", "/x", 200, new { });
            h.Server.Then(FakeFault.TimeoutAfterCommit);
            var r = h.Send(new ApiRequest(HttpMethod.Post, "/x", new { }, Idempotent: false, RequestId: Rid));
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error.Code, Is.EqualTo(ErrorClassifier.NET_TIMEOUT));
            Assert.That(r.Error.Class, Is.EqualTo(ErrorClass.Timeout));
            Assert.That(r.Attempts, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------------------------------- backoff (5.2)

        [Test]
        public void Backoff_WithASeededJitterSource_StaysWithinFullJitterBounds_AtMostFourRetries_WorstCaseFifteenSeconds()
        {
            var rng = new Random(1234);
            var h = new Harness(o => o.Jitter = () => rng.NextDouble());
            h.Server.Then(Enumerable.Range(0, 10).Select(_ => FakeFault.Drop).ToArray());
            var r = h.Send(ApiRequest.Get("/inbox"));

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error.Code, Is.EqualTo(ErrorClassifier.NET_OFFLINE));
            Assert.That(r.Attempts, Is.EqualTo(1 + RuntimeConfig.HTTP_RETRY_MAX));
            Assert.That(h.Delays, Has.Count.EqualTo(RuntimeConfig.HTTP_RETRY_MAX));
            for (int i = 0; i < h.Delays.Count; i++)
            {
                double ceiling = Math.Min(RuntimeConfig.HTTP_RETRY_CAP_MS, RuntimeConfig.HTTP_RETRY_BASE_MS * Math.Pow(2, i));
                Assert.That(h.Delays[i].TotalMilliseconds, Is.InRange(0, ceiling), "retry " + i);
            }

            Assert.That(ApiClient.BackoffDelay(10, 0.999999).TotalMilliseconds, Is.LessThanOrEqualTo(RuntimeConfig.HTTP_RETRY_CAP_MS));
            double worst = Enumerable.Range(0, RuntimeConfig.HTTP_RETRY_MAX).Sum(i => ApiClient.BackoffDelay(i, 0.999999).TotalMilliseconds);
            Assert.That(worst, Is.EqualTo(15000).Within(5), "about 15 s, the summon pending threshold");
            Assert.That(ApiClient.BackoffDelay(2, 0).TotalMilliseconds, Is.EqualTo(0));
        }

        [Test]
        public void RetryAfter_FromTheBodyOrTheHeader_IsHonoured_ThenRetriesAreExhausted()
        {
            var h = new Harness();
            h.Server.On("GET", "/inbox", 200, new { });
            var headerOnly = FakeReply.Json(503, "<html>busy</html>").With("Retry-After", "7");
            h.Server.Then(FakeFault.Status(429, "RATE_LIMITED", retryAfterSec: 3), FakeFault.With(headerOnly), FakeFault.Status(502, "BAD_GATEWAY"));
            var r = h.Send(ApiRequest.Get("/inbox"));
            Assert.That(r.Ok, Is.True);
            Assert.That(h.Delays.Take(2), Is.EqualTo(new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(7) }));
            Assert.That(h.Delays[2], Is.EqualTo(ApiClient.BackoffDelay(2, h.JitterValue)), "no Retry-After: the backoff schedule");

            var h2 = new Harness();
            h2.Server.Then(Enumerable.Range(0, 10).Select(_ => FakeFault.Status(429, "RATE_LIMITED", 1)).ToArray());
            var exhausted = h2.Send(ApiRequest.Get("/inbox"));
            Assert.That(exhausted.Ok, Is.False);
            Assert.That(exhausted.Status, Is.EqualTo(429));
            Assert.That(exhausted.Error.Code, Is.EqualTo("RATE_LIMITED"));
            Assert.That(exhausted.Error.RetryAfterSec, Is.EqualTo(1));
            Assert.That(exhausted.Attempts, Is.EqualTo(1 + RuntimeConfig.HTTP_RETRY_MAX));
        }

        [Test]
        public void A500_IsRetriedOnce()
        {
            var h = new Harness();
            h.Server.Then(FakeFault.Status(500, "INTERNAL"), FakeFault.Status(500, "INTERNAL"), FakeFault.Status(500, "INTERNAL"));
            var r = h.Send(ApiRequest.Get("/inbox"));
            Assert.That(r.Attempts, Is.EqualTo(2));
            Assert.That(r.Error.Class, Is.EqualTo(ErrorClass.ServerError));
            Assert.That(r.Error.Rule.Code, Is.EqualTo(ErrorClassifier.SRV_5XX), "INTERNAL is not a table code: the 500 status rule");
            Assert.That(r.Error.Code, Is.EqualTo("INTERNAL"), "the wire code is kept");
        }

        [Test]
        public void NoRetry_SendsOnce()
        {
            var h = new Harness();
            h.Server.Then(FakeFault.Drop, FakeFault.Drop);
            var r = h.Send(ApiRequest.Get("/time", requiresAuth: false) with { NoRetry = true });
            Assert.That(r.Attempts, Is.EqualTo(1));
            Assert.That(h.Delays, Is.Empty);
        }

        // ------------------------------------------------------------------------------------------- classification (5.2)

        [TestCase(402, "INSUFFICIENT_FUNDS", ErrorClass.Rejected)]
        [TestCase(403, "ACCOUNT_BANNED", ErrorClass.Fatal)]
        [TestCase(409, "SAVE_CONFLICT", ErrorClass.Conflict)]
        [TestCase(409, "INBOX_ALREADY_CLAIMED", ErrorClass.Ok)]
        [TestCase(409, "CONTENT_TOO_OLD", ErrorClass.Rejected)]
        [TestCase(426, "CLIENT_TOO_OLD", ErrorClass.Rejected)]
        [TestCase(503, "MAINTENANCE", ErrorClass.Maintenance)]
        [TestCase(503, "SUMMON_DISABLED", ErrorClass.Rejected)]
        [TestCase(400, "VALIDATION", ErrorClass.Rejected)]
        [TestCase(404, "NOT_FOUND", ErrorClass.Rejected)]
        public void NonRetryableCodes_AreClassifiedCodeFirst_AndSentOnce(int status, string code, ErrorClass cls)
        {
            var h = new Harness();
            h.Server.Then(FakeFault.With(FakeReply.Error(status, code, details: new { appealUrl = "https://x" })));
            var r = h.Send(Pull());
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Status, Is.EqualTo(status));
            Assert.That(r.Error.Code, Is.EqualTo(code));
            Assert.That(r.Error.Class, Is.EqualTo(cls));
            Assert.That((string)r.Error.Details["appealUrl"], Is.EqualTo("https://x"));
            Assert.That(r.Attempts, Is.EqualTo(1));
            var e = r.ToServiceException();
            Assert.That(e.Kind, Is.EqualTo(cls));
            Assert.That(e.Code, Is.EqualTo(code));
            Assert.That(e.Status, Is.EqualTo(status));
            Assert.That(ErrorClassifier.Classify(e).Class, Is.EqualTo(cls), "round-trips through the classifier");
        }

        [Test]
        public void AMaintenanceBlock_CarriesItsDetails_AndIsNotRetried()
        {
            var h = new Harness();
            h.Server.Then(FakeFault.With(FakeReply.Error(503, "MAINTENANCE", retryAfterSec: 600,
                                                          details: new { messageKey = "maint.db_upgrade", until = ServerNow + 600 })));
            var r = h.Send(ApiRequest.Get("/inbox"));
            Assert.That(r.Error.Class, Is.EqualTo(ErrorClass.Maintenance));
            Assert.That(r.Error.Rule.Retry, Is.EqualTo(RetryPolicy.PollConfig));
            Assert.That(r.Error.RetryAfterSec, Is.EqualTo(600));
            Assert.That((string)r.Error.Details["messageKey"], Is.EqualTo("maint.db_upgrade"));
            Assert.That(r.Attempts, Is.EqualTo(1));
            Assert.That(h.Delays, Is.Empty);
        }

        [Test]
        public void AnUnreadableErrorBody_FallsBackToTheStatusRule_AndAnUnreadableSuccessBodyIsServerError()
        {
            var h = new Harness();
            h.Server.Then(FakeFault.With(new FakeReply(418, "<html>teapot</html>")));
            var r = h.Send(ApiRequest.Get("/inbox"));
            Assert.That(r.Error.Class, Is.EqualTo(ErrorClass.Rejected));
            Assert.That(r.Error.Code, Is.EqualTo(ErrorClassifier.REQUEST_REJECTED));

            h.Server.On("GET", "/garbled", _ => new FakeReply(200, "{not json"));
            var g = h.Send(ApiRequest.Get("/garbled"));
            Assert.That(g.Ok, Is.False);
            Assert.That(g.Status, Is.EqualTo(200));
            Assert.That(g.Error.Code, Is.EqualTo(ErrorClassifier.SRV_UNEXPECTED));
        }

        [Test]
        public void TypedBodies_Deserialize()
        {
            var h = new Harness();
            h.Server.On("GET", "/time", 200, new { serverTime = 42 });
            h.Server.On("GET", "/save", _ => new FakeReply(200, bytes: new byte[] { 9, 8 }));
            h.Server.On("POST", "/inbox/claim-all", 204);
            var typed = h.Client.SendAsync<TimeBody>(ApiRequest.Get("/time")).GetAwaiter().GetResult();
            Assert.That(typed.Body.ServerTime, Is.EqualTo(42));
            var text = h.Client.SendAsync<string>(ApiRequest.Get("/time")).GetAwaiter().GetResult();
            Assert.That(text.Body, Does.Contain("serverTime"));
            var bytes = h.Client.SendAsync<byte[]>(ApiRequest.Get("/save")).GetAwaiter().GetResult();
            Assert.That(bytes.Body, Is.EqualTo(new byte[] { 9, 8 }));
            var empty = h.Send(ApiRequest.Mutation(HttpMethod.Post, "/inbox/claim-all", null, Rid));
            Assert.That(empty.Ok, Is.True);
            Assert.That(empty.Body, Is.Null);
            Assert.That(empty.EnsureOk(), Is.Null);
        }

        public sealed class TimeBody
        {
            public long ServerTime { get; set; }
        }

        // ------------------------------------------------------------------------------------------- timeouts and cancel

        [Test]
        public void ARealPerAttemptTimeout_IsNetTimeout_AndTheCallersCancellationThrows()
        {
            var h = new Harness();
            h.Server.Then(FakeFault.Hang);
            var req = ApiRequest.Get("/inbox") with { Timeout = TimeSpan.FromMilliseconds(50), NoRetry = true };
            var r = h.Send(req);
            Assert.That(r.Error.Code, Is.EqualTo(ErrorClassifier.NET_TIMEOUT));

            h.Server.Then(FakeFault.Hang);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            Assert.That(() => h.Client.SendAsync<JToken>(ApiRequest.Get("/inbox"), cts.Token).GetAwaiter().GetResult(),
                        Throws.InstanceOf<OperationCanceledException>());
        }

        // ------------------------------------------------------------------------------------------- 401 refresh (5.2)

        [Test]
        public void A401TokenExpired_RefreshesOnce_AndReplaysWithTheNewToken()
        {
            var h = new Harness();
            h.Server.On("POST", "/summon/pull", 200, new { ok = true });
            h.Server.Then(FakeFault.Status(401, "TOKEN_EXPIRED"));
            var r = h.Send(Pull());
            Assert.That(r.Ok, Is.True);
            Assert.That(h.Auth.Refreshes, Is.EqualTo(1));
            var calls = h.Server.Calls;
            Assert.That(calls[0].Header("Authorization"), Is.EqualTo("Bearer access-1"));
            Assert.That(calls[1].Header("Authorization"), Is.EqualTo("Bearer access-2"));
            Assert.That(calls[1].Header("X-Request-Id"), Is.EqualTo(Rid), "the replay keeps the request id");
            Assert.That(h.Delays, Is.Empty, "the replay is immediate");
        }

        [Test]
        public void A401_ThenRefresh_ThenReplay_ThenASecond401_GivesAuthExpired()
        {
            var h = new Harness();
            h.Server.Then(FakeFault.Status(401, "TOKEN_EXPIRED"), FakeFault.Status(401, "TOKEN_EXPIRED"), FakeFault.Status(401, "TOKEN_EXPIRED"));
            var r = h.Send(Pull());
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error.Class, Is.EqualTo(ErrorClass.AuthExpired));
            Assert.That(r.Attempts, Is.EqualTo(2));
            Assert.That(h.Auth.Refreshes, Is.EqualTo(1));
            Assert.That(r.ToServiceException().Kind, Is.EqualTo(ErrorClass.AuthExpired));
        }

        [Test]
        public void A401_WhoseRefreshFails_OrThatIsNotTokenExpired_IsFinal()
        {
            var h = new Harness();
            h.Auth.RefreshResult = false;
            h.Server.Then(FakeFault.Status(401, "TOKEN_EXPIRED"));
            var r = h.Send(Pull());
            Assert.That(r.Error.Class, Is.EqualTo(ErrorClass.AuthExpired));
            Assert.That(r.Attempts, Is.EqualTo(1));

            var h2 = new Harness();
            h2.Server.Then(FakeFault.Status(401, "TOKEN_REUSED"));
            var reused = h2.Send(Pull());
            Assert.That(reused.Error.Code, Is.EqualTo("TOKEN_REUSED"));
            Assert.That(reused.Error.Class, Is.EqualTo(ErrorClass.AuthExpired));
            Assert.That(h2.Auth.Refreshes, Is.EqualTo(0), "only TOKEN_EXPIRED refreshes");
        }

        [Test]
        public void ConcurrentExpiredRequests_ShareOneRefresh()
        {
            var h = new Harness();
            h.Auth.Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Server.On("GET", "/a", call => call.Header("Authorization") == "Bearer access-1"
                                                ? FakeReply.Error(401, "TOKEN_EXPIRED")
                                                : FakeReply.Json(200, new { }));
            var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() => h.Client.SendAsync<JToken>(ApiRequest.Get("/a")))).ToArray();
            SpinWait.SpinUntil(() => h.Server.Calls.Count >= 4, 5000);
            h.Auth.Hold.SetResult(true);
            Task.WaitAll(tasks, 5000);
            Assert.That(tasks.All(t => t.Result.Ok), Is.True);
            Assert.That(h.Auth.Refreshes, Is.EqualTo(1), "a second refresh of a rotated token would be TOKEN_REUSED");
        }

        // ------------------------------------------------------------------------------------------- threading (section 4)

        /// <summary>A non-UI SynchronizationContext with one pumping thread, like Unity's main thread.</summary>
        sealed class MainThread : SynchronizationContext, IDisposable
        {
            readonly BlockingCollection<(SendOrPostCallback, object)> _queue = new BlockingCollection<(SendOrPostCallback, object)>();
            readonly Thread _thread;

            public MainThread()
            {
                _thread = new Thread(() =>
                {
                    SetSynchronizationContext(this);
                    foreach (var (cb, state) in _queue.GetConsumingEnumerable()) cb(state);
                }) { IsBackground = true, Name = "fake-main" };
                _thread.Start();
            }

            public int ThreadId => _thread.ManagedThreadId;

            public override void Post(SendOrPostCallback d, object state) => _queue.Add((d, state));

            public Task<T> Run<T>(Func<Task<T>> body)
            {
                var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                Post(async _ =>
                {
                    try { tcs.SetResult(await body()); }
                    catch (Exception e) { tcs.SetException(e); }
                }, null);
                return tcs.Task;
            }

            public void Dispose() => _queue.CompleteAdding();
        }

        [Test]
        public void TheNetworkWorkRunsOffTheMainThread_AndTheCallersContinuationResumesOnIt()
        {
            using var main = new MainThread();
            var observedOn = new ConcurrentBag<int>();
            var h = new Harness(o => o.ServerTime = new ThreadRecorder(observedOn));
            h.Server.On("POST", "/summon/pull", 200, new { ok = true });
            h.Server.Then(FakeFault.Status(503, "BUSY"));   // one retry, so the backoff continuation is exercised too

            int continuationThread = main.Run(async () =>
            {
                var r = await h.Client.SendAsync<JToken>(Pull());
                Assert.That(r.Ok, Is.True);
                Assert.That(SynchronizationContext.Current, Is.SameAs(main));
                return Environment.CurrentManagedThreadId;   // where a flow would turn the result into a command
            }).GetAwaiter().GetResult();

            Assert.That(continuationThread, Is.EqualTo(main.ThreadId), "the caller's continuation is on the main thread");
            Assert.That(h.Server.Calls.All(c => c.ThreadId != main.ThreadId), Is.True, "the exchange runs on the thread pool");
            Assert.That(observedOn, Is.Not.Empty);
            Assert.That(observedOn.All(t => t != main.ThreadId), Is.True, "ServerClock.Observe is called off the main thread");
        }

        sealed class ThreadRecorder : IServerTimeObserver
        {
            readonly ConcurrentBag<int> _bag;
            public ThreadRecorder(ConcurrentBag<int> bag) { _bag = bag; }
            public void Observe(long serverTimeSec, long rttMs) => _bag.Add(Environment.CurrentManagedThreadId);
        }
    }
}
