using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Remote.Services;
using HeroCat.Remote.Transport;
using HeroCat.Runtime;
using HeroCat.Runtime.Account;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Remote.Tests
{
    /// <summary>
    /// The AuthClient (issue #148) against a fake of 08 section 2's auth service: guest creation with the idempotent
    /// replay of a lost 201, refresh rotation with the REFRESH_REUSE_GRACE_S same-device retry (issue #273), the refused
    /// session, the ApiClient's 401 refresh, logout and account deletion.
    /// </summary>
    public class AuthClientTests
    {
        const string Device = "6f1c2e8a-0000-4000-8000-000000000001";
        static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

        /// <summary>08 section 2.1's refresh families, as AuthStore implements them (rotation, reuse, grace, logout).</summary>
        sealed class FakeAuthServer
        {
            sealed class Token
            {
                public Guid Player, Family;
                public string Device;
                public long? RevokedAt;
                public string ReplacedBy;
            }

            readonly Dictionary<string, Token> _tokens = new Dictionary<string, Token>();
            readonly Dictionary<string, Guid> _devices = new Dictionary<string, Guid>();
            readonly Dictionary<string, Guid> _accessTokens = new Dictionary<string, Guid>();
            int _n;
            public long Now = 1_800_000_000;
            public int Rotations, Reuses;
            public readonly List<string> LoggedOut = new List<string>();

            public FakeAuthServer(FakeApiHandler h)
            {
                h.ServerTime = () => Now;
                h.On("POST", AuthRoutes.Guest, Guest);
                h.On("POST", AuthRoutes.Refresh, Refresh);
                h.On("POST", AuthRoutes.Logout, Logout);
                h.On("POST", AuthRoutes.Devices, Devices);
            }

            /// <summary>The key each device registered, as the server's <c>devices.hmac_key_enc</c> holds it.</summary>
            public readonly Dictionary<string, byte[]> DeviceKeys = new Dictionary<string, byte[]>();

            /// <summary>A final refusal <c>/devices</c> answers instead of 204 (null: accept).</summary>
            public FakeReply DevicesRefusal;

            FakeReply Devices(FakeApiCall c)
            {
                if (!_accessTokens.ContainsKey((c.Header(ApiHeaders.Authorization) ?? "").Replace("Bearer ", "")))
                    return FakeReply.Error(401, "TOKEN_EXPIRED");
                if (DevicesRefusal != null) return DevicesRefusal;
                var b = JObject.Parse(c.BodyText);
                string device = (string)b["deviceId"];
                if (device != c.Header(ApiHeaders.DeviceId)) return FakeReply.Error(403, "FORBIDDEN", details: new { reason = "DEVICE_MISMATCH" });
                var key = Convert.FromBase64String((string)b["deviceKey"]);
                if (key.Length != 32) return FakeReply.Error(400, "VALIDATION");
                DeviceKeys[device] = key;   // the same key again only updates the metadata (08 section 2.2)
                return new FakeReply(204);
            }

            public string NewRefresh(Guid player, string device, Guid family)
            {
                string t = "r1." + (++_n).ToString("D43");
                _tokens[t] = new Token { Player = player, Family = family, Device = device };
                return t;
            }

            public string NewAccess(Guid player)
            {
                string a = "access-" + (++_n);
                _accessTokens[a] = player;
                return a;
            }

            public int LiveTokens(Guid family) => _tokens.Values.Count(t => t.Family == family && t.RevokedAt == null);
            public Guid FamilyOf(string refresh) => _tokens[refresh].Family;
            public bool IsLive(string refresh) => _tokens.TryGetValue(refresh, out var t) && t.RevokedAt == null;

            FakeReply Guest(FakeApiCall c)
            {
                var b = JObject.Parse(c.BodyText);
                string device = (string)b["deviceId"];
                if (device != c.Header(ApiHeaders.DeviceId)) return FakeReply.Error(400, "VALIDATION");
                if (Convert.FromBase64String((string)b["deviceKey"]).Length != 32) return FakeReply.Error(400, "VALIDATION");
                if (_devices.TryGetValue(device, out var bound)) return FakeReply.Error(409, "DEVICE_ALREADY_BOUND", details: new { playerId = bound });
                var player = Guid.NewGuid();
                _devices[device] = player;
                DeviceKeys[device] = Convert.FromBase64String((string)b["deviceKey"]);
                return FakeReply.Json(201, new
                {
                    playerId = player, accessToken = NewAccess(player), refreshToken = NewRefresh(player, device, Guid.NewGuid()),
                    accessExpiresAt = Now + 3600, profile = new { playerId = player }, serverTime = Now,
                });
            }

            FakeReply Refresh(FakeApiCall c)
            {
                var b = JObject.Parse(c.BodyText);
                string presented = (string)b["refreshToken"];
                if (!_tokens.TryGetValue(presented, out var t)) return FakeReply.Error(401, "UNAUTHENTICATED");
                if (t.Device != (string)b["deviceId"]) return FakeReply.Error(403, "DEVICE_MISMATCH");
                if (t.RevokedAt != null && t.ReplacedBy != null)
                {
                    var live = _tokens.FirstOrDefault(kv => kv.Value.Family == t.Family && kv.Value.RevokedAt == null);
                    if (Now - t.RevokedAt.Value <= RuntimeConfig.REFRESH_REUSE_GRACE_S && live.Key != null)
                    {
                        presented = live.Key;   // the lost response's token (or its successor) rotates instead
                        t = live.Value;
                    }
                    else
                    {
                        Reuses++;
                        foreach (var x in _tokens.Values.Where(x => x.Family == t.Family && x.RevokedAt == null)) x.RevokedAt = Now;
                        return FakeReply.Error(401, "TOKEN_REUSED");
                    }
                }
                if (t.RevokedAt != null) return FakeReply.Error(401, "TOKEN_REVOKED");
                Rotations++;
                string next = NewRefresh(t.Player, t.Device, t.Family);
                t.RevokedAt = Now;
                t.ReplacedBy = next;
                return FakeReply.Json(200, new { accessToken = NewAccess(t.Player), refreshToken = next, accessExpiresAt = Now + 3600, serverTime = Now });
            }

            FakeReply Logout(FakeApiCall c)
            {
                if (!_accessTokens.ContainsKey((c.Header(ApiHeaders.Authorization) ?? "").Replace("Bearer ", "")))
                    return FakeReply.Error(401, "TOKEN_EXPIRED");
                string presented = (string)JObject.Parse(c.BodyText)["refreshToken"];
                LoggedOut.Add(presented);
                if (_tokens.TryGetValue(presented, out var t))
                    foreach (var x in _tokens.Values.Where(x => x.Family == t.Family && x.RevokedAt == null)) x.RevokedAt = Now;
                return new FakeReply(204);
            }
        }

        /// <summary>Every attempt takes <see cref="Seconds"/> of monotonic time (a full per-attempt timeout, say).</summary>
        sealed class SlowHandler : DelegatingHandler
        {
            readonly FakeMonoClock _mono;
            public double Seconds;
            public readonly List<(string path, double startSec)> Starts = new List<(string, double)>();

            public SlowHandler(HttpMessageHandler inner, FakeMonoClock mono) : base(inner) { _mono = mono; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                lock (Starts)
                {
                    Starts.Add((request.RequestUri.AbsolutePath, _mono.Ticks / (double)FakeMonoClock.TicksPerSecond));
                    _mono.Advance(Seconds);
                }
                return base.SendAsync(request, ct);
            }
        }

        sealed class Harness
        {
            public readonly SlowHandler Slow;
            public readonly FakeApiHandler Handler = new FakeApiHandler();
            public readonly FakeAuthServer Server;
            public readonly MemorySecureStore Secure = new MemorySecureStore();
            public readonly SecureTokenStore Tokens;
            public readonly FakeMonoClock Mono = new FakeMonoClock();
            public readonly List<TimeSpan> Delays = new List<TimeSpan>();
            public readonly ApiClient Client;
            public readonly ApiClientOptions Api;
            public AuthClient Auth;

            public Harness()
            {
                Server = new FakeAuthServer(Handler);
                Slow = new SlowHandler(Handler, Mono);
                Tokens = new SecureTokenStore(Secure);
                Api = new ApiClientOptions
                {
                    BaseUrl = "https://api.test.whiskerhero.example/v1",
                    Handler = Slow,
                    ClientVersion = "1.4.2+318",
                    DeviceId = Device,
                    Mono = Mono,
                    Jitter = () => 0.5,
                    Delay = (d, ct) => { lock (Delays) Delays.Add(d); return Task.CompletedTask; },
                };
                Client = new ApiClient(Api);
                NewAuth();
            }

            /// <summary>A fresh AuthClient over the same token store: the next process.</summary>
            public AuthClient NewAuth(byte[] key = null)
            {
                Auth = new AuthClient(Client, Tokens, key ?? Key, new AuthClientOptions
                {
                    Model = () => "Pixel 7", Os = () => "Android 14", Region = () => "US", Mono = Mono, Jitter = () => 0.5,
                    Delay = (d, ct) =>
                    {
                        lock (Delays) Delays.Add(d);
                        Mono.Advance(d.TotalSeconds);
                        return Task.CompletedTask;
                    },
                });
                Api.Credentials = Auth;
                return Auth;
            }

            public SignInResult SignIn() => Auth.SignInAsync().GetAwaiter().GetResult();

            public FakeApiCall[] CallsTo(string path) => Handler.Calls.Where(c => c.Path == path).ToArray();
        }

        static Harness SignedIn()
        {
            var h = new Harness();
            Assert.That(h.SignIn().Status, Is.EqualTo(SignInStatus.SignedIn));
            return h;
        }

        // ------------------------------------------------------------------------------------------- guest (8.1 step 3)

        [Test]
        public void FirstLaunch_CreatesAGuest_WithThe08WireBody_AndStoresTheTokens()
        {
            var h = new Harness();
            var r = h.SignIn();

            Assert.That(r.Status, Is.EqualTo(SignInStatus.SignedIn));
            Assert.That(r.NewAccount, Is.True, "the first guest auth adopts any local save (open question 7)");
            Assert.That(r.PlayerId, Is.Not.EqualTo(Guid.Empty));
            var call = h.CallsTo(AuthRoutes.Guest).Single();
            var body = JObject.Parse(call.BodyText);
            Assert.That((string)body["deviceId"], Is.EqualTo(Device));
            Assert.That(call.Header(ApiHeaders.DeviceId), Is.EqualTo(Device), "deviceId must equal X-Device-Id");
            Assert.That(Convert.FromBase64String((string)body["deviceKey"]), Is.EqualTo(Key));
            Assert.That((string)body["clientVersion"], Is.EqualTo("1.4.2+318"));
            Assert.That((string)body["model"], Is.EqualTo("Pixel 7"));
            Assert.That((string)body["os"], Is.EqualTo("Android 14"));
            Assert.That((string)body["region"], Is.EqualTo("US"));
            Assert.That(call.Header(ApiHeaders.Authorization), Is.Null, "auth none");
            Assert.That(call.Header(ApiHeaders.RequestId), Does.Match("^[0-9a-f]{32}$"));

            var stored = h.Tokens.Load();
            Assert.That(stored.PlayerId, Is.EqualTo(r.PlayerId));
            Assert.That(stored.RefreshToken, Does.StartWith("r1."));
            Assert.That(stored.AccessToken, Is.EqualTo(h.Auth.AccessToken));
            Assert.That(stored.AccessExpiresAt, Is.EqualTo(h.Server.Now + 3600));
            Assert.That(stored.PendingGuestRequestId, Is.Null);
        }

        [Test]
        public void OfflineFirstLaunch_IsAPlayableLocalGuest_AndTheNextConnectionCreatesTheAccount()
        {
            var h = new Harness();
            h.Handler.Then(Enumerable.Repeat(FakeFault.Drop, RuntimeConfig.HTTP_RETRY_MAX + 1).ToArray());
            var offline = h.SignIn();
            Assert.That(offline.Status, Is.EqualTo(SignInStatus.Offline));
            Assert.That(offline.PlayerId, Is.EqualTo(Guid.Empty), "a local guest: playerId null until the first connection");
            Assert.That(offline.Code, Is.EqualTo(ErrorClassifier.NET_OFFLINE));
            Assert.That(h.Auth.HasSession, Is.False);

            var online = h.SignIn();
            Assert.That(online.Status, Is.EqualTo(SignInStatus.SignedIn));
            Assert.That(online.NewAccount, Is.True);
        }

        [Test]
        public void ALostGuestResponse_IsReplayed_NextLaunch_NotRefusedAsDeviceAlreadyBound()
        {
            var h = new Harness();
            // The server commits the guest, then every attempt of this launch fails: the 201 never arrives.
            h.Handler.Then(new[] { FakeFault.TimeoutAfterCommit }.Concat(Enumerable.Repeat(FakeFault.Drop, RuntimeConfig.HTTP_RETRY_MAX)).ToArray());
            Assert.That(h.SignIn().Status, Is.EqualTo(SignInStatus.Offline));
            string pending = h.Tokens.Load().PendingGuestRequestId;
            Assert.That(pending, Does.Match("^[0-9a-f]{32}$"), "the request id was stored before the call");

            h.NewAuth();   // the app is killed and relaunched
            var r = h.SignIn();
            Assert.That(r.Status, Is.EqualTo(SignInStatus.SignedIn));
            Assert.That(h.Handler.Replays, Is.EqualTo(1), "the stored 201 is replayed");
            Assert.That(h.CallsTo(AuthRoutes.Guest).Select(c => c.Header(ApiHeaders.RequestId)).Distinct(), Is.EqualTo(new[] { pending }));
            Assert.That(h.Tokens.Load().PendingGuestRequestId, Is.Null);
        }

        [Test]
        public void ABoundDeviceWithNoTokens_NeedsSignIn_AndDropsThePendingId()
        {
            var h = SignedIn();
            h.Tokens.Clear();   // tokens lost (or logged out); the server still binds the device
            h.NewAuth();
            var r = h.SignIn();
            Assert.That(r.Status, Is.EqualTo(SignInStatus.SignInRequired));
            Assert.That(r.Code, Is.EqualTo("DEVICE_ALREADY_BOUND"));
            Assert.That(r.PlayerId, Is.EqualTo(Guid.Empty));
            Assert.That(h.Tokens.Load()?.PendingGuestRequestId, Is.Null, "the server stored that 409; a new attempt needs a new id");
        }

        // ------------------------------------------------------------------------------------------- refresh (rotation)

        [Test]
        public void ALaunchWithARefreshToken_RotatesIt_AndStoresTheNewPairBeforeUse()
        {
            var h = SignedIn();
            var before = h.Tokens.Load();
            h.NewAuth();
            var r = h.SignIn();

            Assert.That(r.Status, Is.EqualTo(SignInStatus.SignedIn));
            Assert.That(r.NewAccount, Is.False);
            Assert.That(r.PlayerId, Is.EqualTo(before.PlayerId));
            var call = h.CallsTo(AuthRoutes.Refresh).Single();
            var body = JObject.Parse(call.BodyText);
            Assert.That((string)body["refreshToken"], Is.EqualTo(before.RefreshToken));
            Assert.That((string)body["deviceId"], Is.EqualTo(Device));
            Assert.That(call.Header(ApiHeaders.Authorization), Is.Null, "auth none: the refresh token is in the body");
            var after = h.Tokens.Load();
            Assert.That(after.RefreshToken, Is.Not.EqualTo(before.RefreshToken));
            Assert.That(h.Server.IsLive(after.RefreshToken), Is.True);
            Assert.That(h.Server.IsLive(before.RefreshToken), Is.False, "rotated out");
            Assert.That(h.Auth.AccessToken, Is.EqualTo(after.AccessToken));
        }

        [Test]
        public void ALostRefreshResponse_IsRetriedWithTheSameToken_InsideTheGrace_AndLeavesOneLiveToken()   // issue #273, client half
        {
            var h = SignedIn();
            var before = h.Tokens.Load();
            h.Handler.Then(FakeFault.TimeoutAfterCommit);   // the server rotated; the response is lost

            var r = h.SignIn();

            Assert.That(r.Status, Is.EqualTo(SignInStatus.SignedIn));
            var sent = h.CallsTo(AuthRoutes.Refresh).Select(c => (string)JObject.Parse(c.BodyText)["refreshToken"]).ToArray();
            Assert.That(sent, Is.EqualTo(new[] { before.RefreshToken, before.RefreshToken }), "the retry presents the old token");
            Assert.That(h.Server.Reuses, Is.Zero);
            var family = h.Server.FamilyOf(before.RefreshToken);
            Assert.That(h.Server.LiveTokens(family), Is.EqualTo(1));
            Assert.That(h.Server.IsLive(h.Tokens.Load().RefreshToken), Is.True, "the client holds the one live token");

            // And it keeps working: the next refresh is an ordinary rotation.
            h.NewAuth();
            Assert.That(h.SignIn().Status, Is.EqualTo(SignInStatus.SignedIn));
        }

        [Test]
        public void RefreshRetries_StartOnlyWhileTheyCanLandInsideTheGrace_ThenOfflineKeepsTheTokens()
        {
            var h = SignedIn();
            var before = h.Tokens.Load();
            h.Handler.Then(Enumerable.Repeat(FakeFault.Drop, 10).ToArray());
            h.Slow.Seconds = RuntimeConfig.HTTP_TIMEOUT_S;   // each failed attempt uses its whole timeout
            h.Slow.Starts.Clear();

            var r = h.SignIn();

            Assert.That(r.Status, Is.EqualTo(SignInStatus.Offline));
            Assert.That(r.PlayerId, Is.EqualTo(before.PlayerId), "offline continues with the cached playerId");
            Assert.That(h.Tokens.Load().RefreshToken, Is.EqualTo(before.RefreshToken), "tokens are kept");
            // A retry starts at most GRACE - TIMEOUT after the first send, so its handling lands inside the server's grace.
            var starts = h.Slow.Starts.Where(s => s.path.EndsWith(AuthRoutes.Refresh)).Select(s => s.startSec).ToArray();
            Assert.That(starts.Length, Is.GreaterThan(1), "at least one retry fits");
            Assert.That(starts.Last() - starts.First(), Is.LessThanOrEqualTo(RuntimeConfig.REFRESH_REUSE_GRACE_S - RuntimeConfig.HTTP_TIMEOUT_S));
            Assert.That(starts.Last() - starts.First() + RuntimeConfig.HTTP_TIMEOUT_S, Is.LessThan(RuntimeConfig.REFRESH_REUSE_GRACE_S));
        }

        [TestCase("TOKEN_REUSED", 401)]
        [TestCase("TOKEN_REVOKED", 401)]
        [TestCase("TOKEN_EXPIRED", 401)]
        [TestCase("UNAUTHENTICATED", 401)]
        [TestCase("DEVICE_MISMATCH", 403)]
        public void ARefusedRefresh_ClearsTheTokens_AndReportsTheLostSession(string code, int status)
        {
            var h = SignedIn();
            SignInResult lost = null;
            h.Auth.SessionLost += x => lost = x;
            h.Handler.Then(FakeFault.Status(status, code));

            var r = h.SignIn();

            Assert.That(r.Status, Is.EqualTo(SignInStatus.SignInRequired));
            Assert.That(r.Code, Is.EqualTo(code));
            Assert.That(lost, Is.EqualTo(r));
            Assert.That(h.Auth.HasSession, Is.False);
            Assert.That(h.Auth.AccessToken, Is.Null);
            Assert.That(h.Tokens.Load(), Is.Null);
        }

        [Test]
        public void ARefreshPastTheGrace_IsReuse_AndRevokesTheFamily()   // 08 AC 19 from the client's side
        {
            var h = SignedIn();
            var before = h.Tokens.Load();
            h.Handler.Then(FakeFault.TimeoutAfterCommit, FakeFault.Drop, FakeFault.Drop, FakeFault.Drop, FakeFault.Drop);
            Assert.That(h.SignIn().Status, Is.EqualTo(SignInStatus.Offline));

            h.Server.Now += RuntimeConfig.REFRESH_REUSE_GRACE_S + 1;   // the next launch, too late
            h.NewAuth();
            var r = h.SignIn();
            Assert.That(r.Status, Is.EqualTo(SignInStatus.SignInRequired));
            Assert.That(r.Code, Is.EqualTo("TOKEN_REUSED"));
            Assert.That(h.Server.LiveTokens(h.Server.FamilyOf(before.RefreshToken)), Is.Zero);
        }

        [Test]
        public void TheApiClients401_RefreshesOnceThroughTheAuthClient_ForConcurrentCalls_AndReplays()
        {
            var h = SignedIn();
            string stale = h.Auth.AccessToken;
            int expired = 0;
            h.Handler.On("GET", "/inbox", c =>
            {
                if (c.Header(ApiHeaders.Authorization) == "Bearer " + stale)
                {
                    Interlocked.Increment(ref expired);
                    return FakeReply.Error(401, "TOKEN_EXPIRED");
                }
                return FakeReply.Json(200, new { items = new object[0] });
            });

            var calls = Enumerable.Range(0, 4).Select(_ => h.Client.SendAsync<JToken>(ApiRequest.Get("/inbox"))).ToArray();
            Task.WaitAll(calls);

            Assert.That(calls.All(c => c.Result.Ok), Is.True);
            Assert.That(h.CallsTo(AuthRoutes.Refresh).Length, Is.EqualTo(1), "one rotation; a second would be TOKEN_REUSED");
            Assert.That(h.Server.Reuses, Is.Zero);
            Assert.That(h.Auth.AccessToken, Is.Not.EqualTo(stale));
        }

        // ------------------------------------------------------------------------------------------- logout, delete (8.13)

        [Test]
        public void Logout_RevokesThisDevicesFamily_ThenClearsTheTokens()
        {
            var h = SignedIn();
            var t = h.Tokens.Load();

            Assert.That(h.Auth.LogoutAsync().GetAwaiter().GetResult(), Is.True);

            var call = h.CallsTo(AuthRoutes.Logout).Single();
            Assert.That(call.Header(ApiHeaders.Authorization), Is.EqualTo("Bearer " + t.AccessToken));
            Assert.That((string)JObject.Parse(call.BodyText)["refreshToken"], Is.EqualTo(t.RefreshToken));
            Assert.That(h.Server.IsLive(t.RefreshToken), Is.False);
            Assert.That(h.Tokens.Load(), Is.Null);
            Assert.That(h.Auth.PlayerId, Is.EqualTo(Guid.Empty));
        }

        [Test]
        public void LogoutOffline_StillClearsTheTokens_InOneAttempt()
        {
            var h = SignedIn();
            h.Handler.Then(FakeFault.Drop, FakeFault.Drop);
            Assert.That(h.Auth.LogoutAsync().GetAwaiter().GetResult(), Is.False);
            Assert.That(h.CallsTo(AuthRoutes.Logout).Length, Is.EqualTo(1), "best effort, no backoff schedule");
            Assert.That(h.Tokens.Load(), Is.Null);
            Assert.That(h.Auth.HasSession, Is.False);
        }

        [Test]
        public void ConfirmedLogout_Offline_KeepsTheTokens_AndARetryThatLandsClearsThem()   // 00 decision 119
        {
            var h = SignedIn();
            var t = h.Tokens.Load();
            Assert.That(h.Auth.IsLinked, Is.False, "a guest has no linked provider");
            h.Handler.Then(FakeFault.Drop);

            Assert.That(h.Auth.TryConfirmedLogoutAsync().GetAwaiter().GetResult(), Is.False);
            Assert.That(h.CallsTo(AuthRoutes.Logout).Length, Is.EqualTo(1), "one attempt per try; the player decides on a retry");
            Assert.That(h.Tokens.Load(), Is.EqualTo(t), "still signed in");
            Assert.That(h.Auth.HasSession, Is.True);
            Assert.That(h.Server.IsLive(t.RefreshToken), Is.True);

            Assert.That(h.Auth.TryConfirmedLogoutAsync().GetAwaiter().GetResult(), Is.True);
            Assert.That(h.CallsTo(AuthRoutes.Logout).Length, Is.EqualTo(2));
            Assert.That(h.Server.IsLive(t.RefreshToken), Is.False);
            Assert.That(h.Tokens.Load(), Is.Null);
            Assert.That(h.Auth.HasSession, Is.False);
        }

        [Test]
        public void ConfirmedLogout_WithoutASession_SendsNothing()
        {
            var h = new Harness();
            Assert.That(h.Auth.TryConfirmedLogoutAsync().GetAwaiter().GetResult(), Is.True);
            Assert.That(h.CallsTo(AuthRoutes.Logout), Is.Empty);
        }

        [Test]
        public void Linked_IsStoredInTokensJson_AndKeptAcrossARefresh()
        {
            var h = SignedIn();
            Assert.That(h.Tokens.Load().Linked, Is.False, "the guest call stores false");
            h.Tokens.Save(h.Tokens.Load() with { Linked = true });   // TODO(#196): set by /auth/link and /auth/link-login
            h.NewAuth();
            Assert.That(h.Auth.IsLinked, Is.True);

            Assert.That(h.SignIn().Status, Is.EqualTo(SignInStatus.SignedIn));
            Assert.That(h.CallsTo(AuthRoutes.Refresh).Length, Is.EqualTo(1));
            Assert.That(h.Tokens.Load().Linked, Is.True);
            Assert.That(h.Auth.IsLinked, Is.True);
        }

        [Test]
        public void DeleteAccount_ReturnsGraceUntil_AndClearsTheTokens_OnlyOnSuccess()
        {
            var h = SignedIn();
            h.Handler.Then(FakeFault.Status(429, "RATE_LIMITED", 3600));
            h.Handler.Then(Enumerable.Repeat(FakeFault.Status(429, "RATE_LIMITED", 3600), RuntimeConfig.HTTP_RETRY_MAX).ToArray());
            var refused = Assert.Throws<ServiceException>(() => h.Auth.DeleteAccountAsync().GetAwaiter().GetResult());
            Assert.That(refused.Code, Is.EqualTo("RATE_LIMITED"));
            Assert.That(h.Tokens.Load(), Is.Not.Null, "a failure changes nothing");

            h.Handler.On("POST", AuthRoutes.AccountDelete, 200, new { graceUntil = 1_794_880_000L });
            Assert.That(h.Auth.DeleteAccountAsync().GetAwaiter().GetResult(), Is.EqualTo(1_794_880_000L));
            Assert.That(h.CallsTo(AuthRoutes.AccountDelete).Last().Header(ApiHeaders.Authorization), Does.StartWith("Bearer "));
            Assert.That(h.Tokens.Load(), Is.Null);
        }

        // ------------------------------------------------------------------------------------------- device registration (02 section 10.1)

        static DeviceRegistration Ensure(Harness h) => h.Auth.EnsureDeviceRegisteredAsync().GetAwaiter().GetResult();

        /// <summary>A session from tokens.json without the registration record (a build before it), in a new process.</summary>
        static Harness SignedInWithoutRecord(byte[] key = null)
        {
            var h = SignedIn();
            h.Tokens.Save(h.Tokens.Load() with { RegisteredKeyId = null });
            h.NewAuth(key);
            return h;
        }

        [Test]
        public void TheGuestCall_RegistersTheKey_SoNoDevicesCallFollows()
        {
            var h = SignedIn();
            Assert.That(h.Server.DeviceKeys[Device], Is.EqualTo(Key));
            Assert.That(h.Tokens.Load().RegisteredKeyId, Is.EqualTo(DeviceKey.KeyId(Key)));
            Assert.That(Ensure(h), Is.EqualTo(DeviceRegistration.Registered));
            Assert.That(h.CallsTo(AuthRoutes.Devices), Is.Empty);
        }

        [Test]
        public void WithoutASession_ThereIsNothingToRegister()
        {
            var h = new Harness();
            Assert.That(Ensure(h), Is.EqualTo(DeviceRegistration.NoSession));
            Assert.That(h.Handler.Calls, Is.Empty);
        }

        [Test]
        public void AKeyTheServerDoesNotHold_IsRegistered_WithThe08WireBody_Once()
        {
            var newKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();   // replaced after the guest call
            var h = SignedInWithoutRecord(newKey);
            Assert.That(Ensure(h), Is.EqualTo(DeviceRegistration.Registered));

            var call = h.CallsTo(AuthRoutes.Devices).Single();
            var body = JObject.Parse(call.BodyText);
            Assert.That((string)body["deviceId"], Is.EqualTo(Device));
            Assert.That(Convert.FromBase64String((string)body["deviceKey"]), Is.EqualTo(newKey));
            Assert.That((string)body["clientVersion"], Is.EqualTo("1.4.2+318"));
            Assert.That((string)body["model"], Is.EqualTo("Pixel 7"));
            Assert.That((string)body["os"], Is.EqualTo("Android 14"));
            Assert.That(body["keyId"], Is.Null, "the key id is client-local; the server compares the key bytes");
            Assert.That(call.Header(ApiHeaders.Authorization), Does.StartWith("Bearer "));
            Assert.That(call.Header(ApiHeaders.RequestId), Does.Match("^[0-9a-f]{32}$"));
            Assert.That(h.Server.DeviceKeys[Device], Is.EqualTo(newKey));
            Assert.That(h.Tokens.Load().RegisteredKeyId, Is.EqualTo(DeviceKey.KeyId(newKey)), "stored before anyone relies on it");

            Assert.That(Ensure(h), Is.EqualTo(DeviceRegistration.Registered));
            h.NewAuth(newKey);
            Assert.That(Ensure(h), Is.EqualTo(DeviceRegistration.Registered), "the next process too");
            Assert.That(h.CallsTo(AuthRoutes.Devices), Has.Length.EqualTo(1), "idempotent: registered once");
        }

        [Test]
        public void ConcurrentCallers_ShareOneRegistration()
        {
            var h = SignedInWithoutRecord();
            var all = Enumerable.Range(0, 8).Select(_ => h.Auth.EnsureDeviceRegisteredAsync()).ToArray();
            Task.WaitAll(all);
            Assert.That(all.Select(t => t.Result), Is.All.EqualTo(DeviceRegistration.Registered));
            Assert.That(h.CallsTo(AuthRoutes.Devices), Has.Length.EqualTo(1));
        }

        [Test]
        public void AnOfflineRegistration_Throws_AndIsRetriedWithTheSameRequestId_UntilItLands()
        {
            var h = SignedInWithoutRecord();
            h.Handler.Then(Enumerable.Repeat(FakeFault.Drop, RuntimeConfig.HTTP_RETRY_MAX + 1).ToArray());
            var e = Assert.Throws<ServiceException>(() => Ensure(h));
            Assert.That(e.Kind, Is.EqualTo(ErrorClass.Offline));
            Assert.That(h.Tokens.Load().RegisteredKeyId, Is.Null);

            Assert.That(Ensure(h), Is.EqualTo(DeviceRegistration.Registered));
            var ids = h.CallsTo(AuthRoutes.Devices).Select(c => c.Header(ApiHeaders.RequestId)).Distinct().ToArray();
            Assert.That(ids, Has.Length.EqualTo(1), "one idempotent request, replayed until it lands");
        }

        [Test]
        public void AFinalRefusal_IsNotRetriedThisProcess_AndUploadsGoAhead()
        {
            var h = SignedInWithoutRecord(Enumerable.Repeat((byte)9, 32).ToArray());
            h.Server.DevicesRefusal = FakeReply.Error(403, "FORBIDDEN", details: new { reason = "KEY_ROTATION_NOT_ALLOWED" });
            Assert.That(Ensure(h), Is.EqualTo(DeviceRegistration.Refused));
            Assert.That(Ensure(h), Is.EqualTo(DeviceRegistration.Refused));
            Assert.That(h.CallsTo(AuthRoutes.Devices), Has.Length.EqualTo(1));
            Assert.That(h.Tokens.Load().RegisteredKeyId, Is.Null, "not recorded: the next launch asks again");
        }

        [Test]
        public void ARefreshKeepsTheRegistrationRecord()
        {
            var h = SignedIn();
            h.NewAuth();
            Assert.That(h.SignIn().Status, Is.EqualTo(SignInStatus.SignedIn));
            Assert.That(h.Server.Rotations, Is.EqualTo(1));
            Assert.That(h.Tokens.Load().RegisteredKeyId, Is.EqualTo(DeviceKey.KeyId(Key)));
        }

        [Test]
        public void Uploads_WaitForTheRegistration()
        {
            var h = SignedInWithoutRecord();
            h.Handler.On("PUT", RemoteRoutes.Save, 200, new { etag = "e1" });
            var store = new CloudSaveStore(new HeroCat.Runtime.Save.MemorySaveStore(), h.Client, ct => h.Auth.EnsureDeviceRegisteredAsync(ct));
            var file = HeroCat.Runtime.Save.SaveSerializer.EncodeBody("{\"SchemaVersion\":1}", Key);
            store.Write(file);

            h.Handler.Then(Enumerable.Repeat(FakeFault.Drop, RuntimeConfig.HTTP_RETRY_MAX + 1).ToArray());
            Assert.That(store.TryUploadAsync().GetAwaiter().GetResult(), Is.False);
            Assert.That(h.CallsTo(RemoteRoutes.Save), Is.Empty, "no upload before the server holds the key");
            Assert.That(store.PendingUpload, Is.EqualTo(file));

            Assert.That(store.TryUploadAsync().GetAwaiter().GetResult(), Is.True);
            var paths = h.Handler.Calls.Select(c => c.Path).Where(p => p == AuthRoutes.Devices || p == RemoteRoutes.Save).ToArray();
            Assert.That(paths.Last(), Is.EqualTo(RemoteRoutes.Save));
            Assert.That(h.CallsTo(RemoteRoutes.Save), Has.Length.EqualTo(1));
            Assert.That(HeroCat.Runtime.Save.SaveSerializer.Decode(h.CallsTo(RemoteRoutes.Save)[0].Body, h.Server.DeviceKeys[Device]).HmacValid,
                        Is.True, "the upload verifies against the key the server holds");
        }

        [Test]
        public void TheKey_TravelsOnlyInTheRegistrationBodies_AndIsNotStoredWithTheTokens()
        {
            var h = SignedInWithoutRecord();
            Ensure(h);
            h.Handler.On("PUT", RemoteRoutes.Save, 200, new { etag = "e1" });
            var store = new CloudSaveStore(new HeroCat.Runtime.Save.MemorySaveStore(), h.Client, ct => h.Auth.EnsureDeviceRegisteredAsync(ct));
            store.Write(HeroCat.Runtime.Save.SaveSerializer.EncodeBody("{\"SchemaVersion\":1}", Key));
            Assert.That(store.TryUploadAsync().GetAwaiter().GetResult(), Is.True);

            string b64 = Convert.ToBase64String(Key);
            string hex = string.Concat(Key.Select(b => b.ToString("x2")));
            foreach (var c in h.Handler.Calls)
            {
                foreach (var kv in c.Headers)
                    Assert.That(kv.Value, Does.Not.Contain(b64).And.Not.Contain(hex), $"{c} header {kv.Key}");
                if (c.Path == AuthRoutes.Guest || c.Path == AuthRoutes.Devices) continue;
                string text = c.Body == null ? "" : System.Text.Encoding.UTF8.GetString(c.Body);
                Assert.That(text, Does.Not.Contain(b64).And.Not.Contain(hex), c.ToString());
            }
            string tokens = System.Text.Encoding.UTF8.GetString(h.Secure.Get(SecureTokenStore.Key));
            Assert.That(tokens, Does.Not.Contain(b64).And.Not.Contain(hex));
            Assert.That(DeviceKey.KeyId(Key), Has.Length.EqualTo(16).And.Not.Contain(hex.Substring(0, 16)), "the id is a hash, not a prefix");
        }

        // ------------------------------------------------------------------------------------------- wiring

        [Test]
        public void RemoteWiring_UsesTheAuthClient_AsTheAccountAndTheApiCredentials_WithAStoredDeviceKey()
        {
            var secure = new MemorySecureStore();
            var api = new ApiClientOptions { BaseUrl = "https://api.test.whiskerhero.example/v1", Handler = new OfflineHandler() };
            var svc = RemoteGameServices.Wire(new ServicesOptions
            {
                SecureStore = secure, Clock = new FakeClock(1_800_000_000), Slots = new HeroCat.Runtime.Save.MemorySaveStore(),
            }, api);

            Assert.That(svc.Account, Is.TypeOf<AuthClient>());
            Assert.That(api.Credentials, Is.SameAs(svc.Account));
            var key = secure.Get(DeviceKey.Key);
            Assert.That(key, Has.Length.EqualTo(DeviceKey.KeyBytes));
            Assert.That(DeviceKey.LoadOrCreate(secure), Is.EqualTo(key), "created once, then reused");
        }
    }
}
