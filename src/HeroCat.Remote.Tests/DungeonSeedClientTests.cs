using System.Linq;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Remote.Services;
using HeroCat.Remote.Transport;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Remote.Tests
{
    /// <summary>The remote dungeon seed client over 08 section 3.3 (issue #146), against <see cref="FakeApiHandler"/>.</summary>
    public class DungeonSeedClientTests
    {
        const string Req = "0123456789abcdef0123456789abcdef";

        static ApiClient Client(FakeApiHandler h) => new ApiClient(new ApiClientOptions { Handler = h, Delay = (d, ct) => Task.CompletedTask, Jitter = () => 0 });

        static FakeApiHandler Server(string seed = "9f86d081884c7d65") => new FakeApiHandler().On("POST", DungeonSeedClient.Route, _ =>
            FakeReply.Json(200, new JObject { ["grantId"] = "a4d1e5c2-0000-4000-8000-000000000001", ["seed"] = seed, ["expiresAt"] = 1793671500,
                                              ["serverTime"] = 1793670900 }.ToString()));

        [Test]
        public async Task It_posts_kind_floor_and_content_version_with_the_requestId_and_reads_the_grant()
        {
            var h = Server();
            var g = await new DungeonSeedClient(Client(h), () => "2026.11.03").RequestSeedAsync(new DungeonSeedRequest(Req, Dungeon.ROCK_CANDY, 7));
            Assert.That((g.Seed, g.GrantId, g.ExpiresAtServerSeconds, g.LocalSeed),
                        Is.EqualTo((0x9f86d081884c7d65UL, "a4d1e5c2-0000-4000-8000-000000000001", 1793671500L, false)));
            var call = h.Calls.Single();
            Assert.That(call.Header(ApiHeaders.RequestId), Is.EqualTo(Req));
            Assert.That(JToken.DeepEquals(JObject.Parse(call.BodyText),
                                          JObject.Parse(@"{ ""kind"": ""ROCK_CANDY"", ""floor"": 7, ""clientContentVersion"": ""2026.11.03"" }")), Is.True, call.BodyText);
        }

        [Test]
        public async Task A_timeout_after_commit_is_retried_with_the_same_requestId_and_gets_the_stored_answer()
        {
            var h = Server().Then(FakeFault.TimeoutAfterCommit);
            var g = await new DungeonSeedClient(Client(h), () => "v").RequestSeedAsync(new DungeonSeedRequest(Req, Dungeon.FISH_COIN, 1));
            Assert.That(g.Seed, Is.EqualTo(0x9f86d081884c7d65UL));
            Assert.That(h.Calls.Select(c => c.Header(ApiHeaders.RequestId)).Distinct(), Is.EqualTo(new[] { Req }));
            Assert.That(h.Handled, Is.EqualTo(1), "the server handled it once; the retry was its replay");
        }

        [Test]
        public void Offline_cap_and_a_bad_body_throw_service_exceptions()
        {
            var off = Server().Then(FakeFault.Drop, FakeFault.Drop, FakeFault.Drop, FakeFault.Drop, FakeFault.Drop);
            var e = Assert.ThrowsAsync<ServiceException>(() => new DungeonSeedClient(Client(off), null).RequestSeedAsync(new DungeonSeedRequest(Req, Dungeon.EXP, 1)));
            Assert.That((e.Code, e.Retryable), Is.EqualTo((ErrorClassifier.NET_OFFLINE, true)));

            var cap = new FakeApiHandler().On("POST", DungeonSeedClient.Route, _ => FakeReply.Error(429, "DUNGEON_SEED_CAP"));
            e = Assert.ThrowsAsync<ServiceException>(() => new DungeonSeedClient(Client(cap), null).RequestSeedAsync(new DungeonSeedRequest(Req, Dungeon.EXP, 1)));
            Assert.That((e.Code, e.Kind, e.Retryable), Is.EqualTo(("DUNGEON_SEED_CAP", ErrorClass.Rejected, false)));

            e = Assert.ThrowsAsync<ServiceException>(() => new DungeonSeedClient(Client(Server("xyz")), null).RequestSeedAsync(new DungeonSeedRequest(Req, Dungeon.EXP, 1)));
            Assert.That(e.Code, Is.EqualTo(ErrorClassifier.SRV_UNEXPECTED));
        }
    }
}
