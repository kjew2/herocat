using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Remote.Services;
using HeroCat.Remote.Transport;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Inbox;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Remote.Tests
{
    /// <summary>The remote inbox over 08 section 3.2 and the ApiClient (issue #176), against <see cref="FakeApiHandler"/>.</summary>
    public class RemoteInboxTests
    {
        const string ListBody = @"{ ""items"": [
            { ""inboxItemId"": ""ibx_iap"", ""kind"": ""iap"", ""titleKey"": ""inbox.iap.title"", ""bodyKey"": ""inbox.iap.body"", ""params"": { ""sku"": ""dia_3"" },
              ""grants"": [ { ""kind"": ""currency"", ""id"": ""DIAMOND"", ""amount"": 2400 } ], ""createdAt"": 1793670000, ""expiresAt"": null },
            { ""inboxItemId"": ""ibx_comp"", ""kind"": ""compensation"", ""titleKey"": ""inbox.comp.title"", ""bodyKey"": ""inbox.comp.2026_11_03"", ""params"": {},
              ""grants"": [ { ""kind"": ""currency"", ""id"": ""DIAMOND"", ""amount"": 300 }, { ""kind"": ""currency"", ""id"": ""TICKET_SUMMON_WEAPON"", ""amount"": 5 } ],
              ""createdAt"": 1793600000, ""expiresAt"": 1796200000 },
            { ""inboxItemId"": ""ibx_pkg2"", ""kind"": ""package_daily"", ""grants"": [ { ""kind"": ""currency"", ""id"": ""DIAMOND"", ""amount"": 100 } ],
              ""createdAt"": 1793600000, ""availableAt"": 1793760000, ""expiresAt"": 1796352000 } ],
          ""serverTime"": 1793670900 }";

        const string Req1 = "0123456789abcdef0123456789abcdef";

        /// <summary>
        /// A fake 08 inbox under the fake API: a claimed id answers 409 INBOX_ALREADY_CLAIMED with the original grants in
        /// <c>details</c>; the handler itself replays a handled X-Request-Id (08's idempotency store).
        /// </summary>
        static FakeApiHandler Server(List<string> ledger)
        {
            var claimed = new HashSet<string>();
            var grants = new JArray(new JObject { ["kind"] = "currency", ["id"] = "DIAMOND", ["amount"] = 2400 });
            return new FakeApiHandler()
                .On("GET", RemoteInboxService.ListRoute, _ => FakeReply.Json(200, ListBody))
                .On("POST", RemoteInboxService.ClaimRoute, call =>
                {
                    string id = (string)JObject.Parse(call.BodyText)["inboxItemId"];
                    if (!claimed.Add(id))
                        return FakeReply.Error(409, InboxRules.INBOX_ALREADY_CLAIMED,
                                               details: new JObject { ["inboxItemId"] = id, ["grants"] = grants, ["reason"] = "IAP" });
                    ledger.Add(id);
                    return FakeReply.Json(200, new JObject { ["inboxItemId"] = id, ["grants"] = grants, ["balances"] = new JObject { ["DIAMOND"] = 3640 },
                                                             ["reason"] = "IAP", ["serverTime"] = 1793670901 }.ToString());
                });
        }

        static ApiClient Client(FakeApiHandler h) => new ApiClient(new ApiClientOptions { Handler = h, Delay = (d, ct) => Task.CompletedTask, Jitter = () => 0 });

        static readonly System.Lazy<HeroCat.Sim.Content.ContentDb> Db = new System.Lazy<HeroCat.Sim.Content.ContentDb>(() =>
            HeroCat.Runtime.Content.ContentLoader.LoadFromDirectory(
                HeroCat.Runtime.Content.FileContentSource.FindContentDirectory(TestContext.CurrentContext.TestDirectory), HeroCat.Sim.Content.Tier.M0));

        static Simulation NewSim()
        {
            const long t0 = 1793670900;
            var state = HeroCat.Runtime.Profile.ProfileFactory.Create(Db.Value, System.Guid.Empty, t0, new System.Guid("de71ce00-0000-4000-8000-000000000001"),
                                                                     new System.Guid("7e57a7e0-0000-4000-8000-000000000007"));
            var sim = new Simulation(state, Db.Value);
            sim.Apply(new ApplyServerTime(t0));
            sim.DrainEvents();
            return sim;
        }

        [Test]
        public void List_ParsesTheWireFormat_KindExpiryParamsAndScheduledPortions()   // 08 section 3.2, 06 section 9
        {
            var svc = new RemoteInboxService(Client(Server(new List<string>())));
            var items = svc.ListAsync().GetAwaiter().GetResult();
            Assert.That(items.Select(i => (i.Id, i.Type)), Is.EqualTo(new[] { ("ibx_iap", "IAP"), ("ibx_comp", "COMPENSATION"), ("ibx_pkg2", "PACKAGE_DAILY") }));
            var iap = items[0];
            Assert.That(iap.ExpiresAtServerSeconds, Is.EqualTo(InboxRules.NeverExpires), "IAP never expires (expiresAt null)");
            Assert.That(items.Select(i => i.CreatedAtServerSeconds), Is.EqualTo(new[] { 1793670000L, 1793600000L, 1793600000L }), "inbox_claimed.ageSec");
            Assert.That(iap.Args["sku"], Is.EqualTo("dia_3"));
            Assert.That((iap.TitleKey, iap.BodyKey), Is.EqualTo(("inbox.iap.title", "inbox.iap.body")));
            Assert.That(iap.Grant.Reason, Is.EqualTo(CurrencyReason.IAP));
            Assert.That(items[1].Grant.Lines.Select(l => (l.Kind, l.Id, l.Amount)),
                        Is.EqualTo(new[] { ("currency", "DIAMOND", 300L), ("currency", "TICKET_SUMMON_WEAPON", 5L) }));
            Assert.That(items[2].Grant.Reason, Is.EqualTo(CurrencyReason.IAP), "package daily portions are IAP");
            long now = 1793670900;
            Assert.That(InboxRules.Available(items, now, new HashSet<string>()).Select(i => i.Id), Is.EqualTo(new[] { "ibx_iap", "ibx_comp" }));
            Assert.That(InboxRules.BadgeCount(items, now, new HashSet<string>()), Is.EqualTo(2), "the future portion is not counted");
        }

        [Test]
        public void AC23_ReplayedRequestId_ReturnsTheOriginalResult_AndGrantsNothingTwice()   // 06 AC 23, 08 AC 11
        {
            var ledger = new List<string>();
            var server = Server(ledger);
            var svc = new RemoteInboxService(Client(server));
            svc.ListAsync().GetAwaiter().GetResult();
            var first = svc.ClaimAsync(Req1, "ibx_iap").GetAwaiter().GetResult();
            var replay = svc.ClaimAsync(Req1, "ibx_iap").GetAwaiter().GetResult();
            Assert.That(first.AlreadyClaimed, Is.False);
            Assert.That(replay.AlreadyClaimed, Is.False, "the replay is the original 200");
            Assert.That(server.Replays, Is.EqualTo(1));
            Assert.That(replay.Grant.Lines.Single().Amount, Is.EqualTo(2400));
            Assert.That(replay.Grant.Reason, Is.EqualTo(CurrencyReason.IAP));
            Assert.That(ledger, Is.EqualTo(new[] { "ibx_iap" }), "one ledger write");
            Assert.That(server.Calls.Where(c => c.Method == "POST").Select(c => c.Header(ApiHeaders.RequestId)), Is.All.EqualTo(Req1));

            // Through the sim: applying the original and the replay credits once.
            var sim = NewSim();
            long dia = sim.State.Wallet.Diamond;
            Assert.That(sim.Apply(new ApplyInboxGrant(first.Grant)).Ok, Is.True);
            Assert.That(sim.Apply(new ApplyInboxGrant(replay.Grant)).Reason, Is.EqualTo(InboxRejectReasons.INBOX_ALREADY_APPLIED));
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(dia + 2400));
            Assert.That(sim.DrainEvents().OfType<CurrencyChanged>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void Flow_OverTheRemoteInbox_AppliesOnce_AlreadyClaimedIsSuccess_TimeoutAfterCommitReplays()   // 01 section 8.9 step 6
        {
            var ledger = new List<string>();
            var server = Server(ledger);
            var svc = new RemoteInboxService(Client(server));
            var sim = NewSim();
            var flow = new InboxFlow(svc, () => sim.State, sim.Apply, () => 1793670900, new FakeMonoClock());
            flow.RefreshAsync().GetAwaiter().GetResult();
            Assert.That(flow.BadgeCount, Is.EqualTo(2));
            long dia = sim.State.Wallet.Diamond;

            server.Then(FakeFault.TimeoutAfterCommit);   // the server claims, the reply is lost; the retry replays it
            Assert.That(flow.ClaimAsync("ibx_iap").GetAwaiter().GetResult().Applied, Is.True);
            Assert.That(ledger, Is.EqualTo(new[] { "ibx_iap" }));
            var again = flow.ClaimAsync("ibx_iap").GetAwaiter().GetResult();   // a new requestId: 409 INBOX_ALREADY_CLAIMED
            Assert.That((again.Ok, again.Applied), Is.EqualTo((true, false)));
            Assert.That(sim.State.Wallet.Diamond, Is.EqualTo(dia + 2400));
            Assert.That(flow.BadgeCount, Is.EqualTo(1));
        }

        [Test]
        public void ClaimErrors_KeepTheirCodes_ClaimAll_ParsesPerItemRows()   // 08 section 3.2
        {
            var h = new FakeApiHandler()
                .On("POST", RemoteInboxService.ClaimRoute, _ => FakeReply.Error(410, InboxRules.INBOX_EXPIRED))
                .On("POST", RemoteInboxService.ClaimAllRoute, _ => FakeReply.Json(200,
                    "{\"claimed\":[{\"inboxItemId\":\"a\",\"grants\":[{\"kind\":\"currency\",\"id\":\"FISH_COIN\",\"amount\":500}],\"reason\":\"MAIL\"}," +
                    "{\"inboxItemId\":\"b\",\"code\":\"INBOX_EXPIRED\"}],\"balances\":{}}"));
            var svc = new RemoteInboxService(Client(h));
            var e = Assert.Throws<ServiceException>(() => svc.ClaimAsync(Req1, "x").GetAwaiter().GetResult());
            Assert.That((e.Code, e.Kind, e.Status), Is.EqualTo(("INBOX_EXPIRED", ErrorClass.Rejected, (int?)410)));

            const string req2 = "fedcba9876543210fedcba9876543210";
            var all = svc.ClaimAllAsync(req2).GetAwaiter().GetResult();
            Assert.That(all.Items.Select(i => (i.InboxItemId, i.Ok, i.ErrorCode)), Is.EqualTo(new[] { ("a", true, (string)null), ("b", false, "INBOX_EXPIRED") }));
            Assert.That(all.Items[0].Grant.Reason, Is.EqualTo(CurrencyReason.MAIL));
            Assert.That(h.Calls.Last().Header(ApiHeaders.RequestId), Is.EqualTo(req2));

            var offline = new RemoteInboxService(Client(new FakeApiHandler().Then(Enumerable.Repeat(FakeFault.Drop, 10).ToArray())));
            Assert.That(Assert.Throws<ServiceException>(() => offline.ClaimAsync(Req1, "x").GetAwaiter().GetResult()).Retryable, Is.True);
        }
    }
}
