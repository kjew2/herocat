using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Inbox;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>The inbox seam and claim flow (06 section 9, 01 section 8.9 step 6; issue #176).</summary>
    public class InboxFlowTests
    {
        static InboxGrantPayload Diamonds(string id, long n, CurrencyReason reason = CurrencyReason.MAIL) =>
            new InboxGrantPayload(id, new[] { new GrantLine("currency", "DIAMOND", n) }, reason);

        static InboxItem Item(string id, long n, long expires = InboxRules.NeverExpires, long available = 0, string type = "MAIL") =>
            new InboxItem(id, Diamonds(id, n, InboxRules.ReasonFor(type)), expires, available, type);

        static LocalInboxService LocalInbox(TestSession s) => (LocalInboxService)s.Runner.Services.Inbox;

        [Test]
        public void LocalInbox_ClaimIsNotFound_InM0()   // 08 section 12
        {
            var s = TestSession.Open();
            var e = Assert.Throws<ServiceException>(() => s.Runner.Services.Inbox.ClaimAsync("r", "ibx_x").GetAwaiter().GetResult());
            Assert.That(e.Code, Is.EqualTo(InboxRules.INBOX_NOT_FOUND));
            var outcome = s.Runner.Inbox.ClaimAsync("ibx_x").GetAwaiter().GetResult();
            Assert.That((outcome.Ok, outcome.ErrorCode, outcome.Retryable), Is.EqualTo((false, InboxRules.INBOX_NOT_FOUND, false)));
        }

        [Test]
        public void Claim_AppliesOnce_BadgeCountsAvailableUnclaimed_AndSaves()   // 06 rules 9.2 and 9.5
        {
            var s = TestSession.Open();
            var inbox = LocalInbox(s);
            long now = s.Clock.NowServerSeconds;
            inbox.Add(Item("ibx_iap", 2400, type: "IAP"));
            inbox.Add(Item("ibx_mail", 300, expires: now + 86400));
            var flow = s.Runner.Inbox;
            flow.RefreshAsync().GetAwaiter().GetResult();
            Assert.That(flow.BadgeCount, Is.EqualTo(2));
            long dia = s.Sim.State.Wallet.Diamond;
            long saves = s.Runner.SavesWritten;
            var events = new List<CurrencyChanged>();
            s.Bus.Subscribe<CurrencyChanged>(events.Add);

            var o = flow.ClaimAsync("ibx_iap").GetAwaiter().GetResult();
            Assert.That((o.Ok, o.Applied), Is.EqualTo((true, true)));
            Assert.That(s.Sim.State.Wallet.Diamond, Is.EqualTo(dia + 2400));
            Assert.That(events.Single().Reason, Is.EqualTo(CurrencyReason.IAP), "the item type's reason");
            Assert.That(s.Sim.State.Inbox.ClaimedIds.Contains("ibx_iap"), Is.True);
            Assert.That(s.Runner.SavesWritten, Is.GreaterThan(saves), "saved after the grant");
            Assert.That(flow.BadgeCount, Is.EqualTo(1));
            Assert.That(flow.Available.Select(i => i.Id), Is.EqualTo(new[] { "ibx_mail" }));

            // A second claim of the same id (new requestId) is INBOX_ALREADY_CLAIMED: success, nothing applied twice.
            events.Clear();
            var again = flow.ClaimAsync("ibx_iap").GetAwaiter().GetResult();
            Assert.That((again.Ok, again.Applied), Is.EqualTo((true, false)));
            Assert.That(events, Is.Empty);
            Assert.That(s.Sim.State.Wallet.Diamond, Is.EqualTo(dia + 2400));
            Assert.That(s.Log.Records.OfType<ApplyInboxGrant>().Count(), Is.EqualTo(1), "a known id is not even issued");
        }

        [Test]
        public void AlreadyClaimed_NotYetInClaimedIds_IsAppliedOnce()   // 01 section 8.9 step 6 (claim confirmed, client lost the reply)
        {
            var s = TestSession.Open();
            var inbox = LocalInbox(s);
            inbox.Add(Item("ibx_1", 500));
            inbox.ClaimAsync("lost-reply", "ibx_1").GetAwaiter().GetResult();   // the server claimed it; the client never applied
            long dia = s.Sim.State.Wallet.Diamond;
            var o = s.Runner.Inbox.ClaimAsync("ibx_1").GetAwaiter().GetResult();
            Assert.That((o.Ok, o.Applied), Is.EqualTo((true, true)));
            Assert.That(s.Sim.State.Wallet.Diamond, Is.EqualTo(dia + 500));
            Assert.That(s.Runner.Inbox.ClaimAsync("ibx_1").GetAwaiter().GetResult().Applied, Is.False);
            Assert.That(s.Sim.State.Wallet.Diamond, Is.EqualTo(dia + 500));
        }

        [Test]
        public void FutureItems_AreNotListedOrCounted_ExpiredItems_AreNotClaimable()   // #176 acceptance 3, 06 rules 9.1 and 9.6
        {
            var s = TestSession.Open();
            var inbox = LocalInbox(s);
            long now = s.Clock.NowServerSeconds;
            inbox.Add(Item("ibx_next", 100, available: now + 86400, type: "PACKAGE_DAILY"));
            inbox.Add(Item("ibx_old", 100, expires: now));
            inbox.Add(Item("ibx_ok", 100, expires: now + 1));
            var flow = s.Runner.Inbox;
            Assert.That(flow.RefreshAsync().GetAwaiter().GetResult().Select(i => i.Id), Is.EqualTo(new[] { "ibx_ok" }));
            Assert.That(flow.BadgeCount, Is.EqualTo(1));

            var early = flow.ClaimAsync("ibx_next").GetAwaiter().GetResult();
            Assert.That((early.Ok, early.ErrorCode), Is.EqualTo((false, InboxRules.INBOX_NOT_AVAILABLE)));
            var late = flow.ClaimAsync("ibx_old").GetAwaiter().GetResult();
            Assert.That((late.Ok, late.ErrorCode), Is.EqualTo((false, InboxRules.INBOX_EXPIRED)));
            Assert.That(s.Sim.State.Inbox.ClaimedIds, Is.Empty);

            // The rules on rows a remote list may return: the next package portion is upcoming, not available.
            var rows = new[] { Item("a", 1, available: now + 10), Item("b", 1, expires: now), Item("c", 1) };
            Assert.That(InboxRules.Available(rows, now, new HashSet<string>()).Select(i => i.Id), Is.EqualTo(new[] { "c" }));
            Assert.That(InboxRules.BadgeCount(rows, now, new HashSet<string> { "c" }), Is.EqualTo(0));
            Assert.That(rows.Where(i => InboxRules.IsUpcoming(i, now)).Select(i => i.Id), Is.EqualTo(new[] { "a" }));
            s.Clock.Advance(86400);
            Assert.That(flow.ClaimAsync("ibx_next").GetAwaiter().GetResult().Applied, Is.True, "claimable once availableAt passes");
        }

        [Test]
        public void ClaimAll_ReportsPerItem_AndAppliesEachOnce()   // 06 rule 9.3
        {
            var s = TestSession.Open();
            var inbox = LocalInbox(s);
            inbox.Add(Item("ibx_a", 100));
            inbox.Add(Item("ibx_b", 200, type: "IAP"));
            inbox.Add(new InboxItem("ibx_c", new InboxGrantPayload("ibx_c", new[] { new GrantLine("item", "wpn_x", 1) }, CurrencyReason.ADMIN),
                                    InboxRules.NeverExpires));
            long dia = s.Sim.State.Wallet.Diamond;
            var outcomes = s.Runner.Inbox.ClaimAllAsync().GetAwaiter().GetResult();
            Assert.That(outcomes.Select(o => (o.InboxItemId, o.Ok, o.ErrorCode)), Is.EqualTo(new[]
            {
                ("ibx_a", true, (string)null), ("ibx_b", true, null), ("ibx_c", false, InboxRejectReasons.INBOX_ITEM_GRANT_UNSUPPORTED),
            }));
            Assert.That(s.Sim.State.Wallet.Diamond, Is.EqualTo(dia + 300));
            Assert.That(s.Runner.Inbox.ClaimAllAsync().GetAwaiter().GetResult(), Is.Empty, "nothing left to claim");
        }

        [Test]
        public void ACreditedClaim_TracksOneInboxClaimed_WithEveryField_BulkOnlyForClaimAll()   // 06 section 13, issue #177
        {
            var s = TestSession.Open();
            var inbox = LocalInbox(s);
            long now = s.Clock.NowServerSeconds;
            inbox.Add(Item("ibx_iap", 2400, type: "IAP") with { CreatedAtServerSeconds = now - 600 });
            inbox.Add(Item("ibx_a", 100) with { CreatedAtServerSeconds = now - 7200 });
            inbox.Add(Item("ibx_b", 200));   // the list did not say when it was created
            var flow = s.Runner.Inbox;
            flow.RefreshAsync().GetAwaiter().GetResult();
            s.Clock.Advance(5);

            Assert.That(flow.ClaimAsync("ibx_iap").GetAwaiter().GetResult().Applied, Is.True);
            var one = s.Analytics.Events.Single(e => e.Name == "inbox_claimed");
            Assert.That(one.Props.Keys, Is.EquivalentTo(new[] { "itemId", "type", "grants", "bulk", "ageSec" }));
            Assert.That((one.Props["itemId"], one.Props["type"], one.Props["bulk"], one.Props["ageSec"]), Is.EqualTo(("ibx_iap", "IAP", false, (object)605L)));
            var grants = (List<Dictionary<string, object>>)one.Props["grants"];
            Assert.That(grants.Single(), Is.EquivalentTo(new Dictionary<string, object> { ["kind"] = "currency", ["id"] = "DIAMOND", ["amount"] = 2400L }));

            Assert.That(flow.ClaimAsync("ibx_iap").GetAwaiter().GetResult().Applied, Is.False);
            Assert.That(s.Analytics.Count("inbox_claimed"), Is.EqualTo(1), "a reconciled double claim credits nothing and tracks nothing");

            flow.ClaimAllAsync().GetAwaiter().GetResult();
            var bulk = s.Analytics.Events.Where(e => e.Name == "inbox_claimed").Skip(1).ToList();
            Assert.That(bulk.Select(e => (e.Props["itemId"], e.Props["type"], e.Props["bulk"], e.Props["ageSec"])), Is.EqualTo(new[]
            {
                ((object)"ibx_a", (object)"MAIL", (object)true, (object)7205L), ("ibx_b", "MAIL", true, null),
            }));
        }

        [Test]
        public void AClaimOfAnUnlistedItem_HasNoTypeOrAge()
        {
            var s = TestSession.Open();
            LocalInbox(s).Add(Item("ibx_x", 50) with { CreatedAtServerSeconds = s.Clock.NowServerSeconds });
            Assert.That(s.Runner.Inbox.ClaimAsync("ibx_x").GetAwaiter().GetResult().Applied, Is.True);   // no RefreshAsync first
            var e = s.Analytics.Events.Single(x => x.Name == "inbox_claimed");
            Assert.That((e.Props["type"], e.Props["ageSec"]), Is.EqualTo(((object)null, (object)null)));
        }

        /// <summary>A server that fails as offline <see cref="Failures"/> times, then claims; it records every requestId.</summary>
        sealed class FlakyInbox : IInboxService
        {
            readonly LocalInboxService _inner = new LocalInboxService();
            public int Failures;
            public readonly List<string> RequestIds = new List<string>();
            public FlakyInbox(params InboxItem[] items) { foreach (var i in items) _inner.Add(i); }
            public Task<IReadOnlyList<InboxItem>> ListAsync(CancellationToken ct = default) => _inner.ListAsync(ct);
            public Task<InboxClaimResult> ClaimAsync(string requestId, string inboxItemId, CancellationToken ct = default)
            {
                RequestIds.Add(requestId);
                if (Failures-- > 0) throw new ServiceException(ErrorClass.Offline, "Offline");
                return _inner.ClaimAsync(requestId, inboxItemId, ct);
            }
            public Task<InboxClaimAllResult> ClaimAllAsync(string requestId, CancellationToken ct = default) => _inner.ClaimAllAsync(requestId, ct);
        }

        static InboxFlow Flow(TestSession s, IInboxService svc, List<InboxClaimStatus> seen, FakeMonoClock mono, InboxFlow[] self) =>
            self[0] = new InboxFlow(svc, () => s.Sim.State, s.Runner.Issue, () => s.Clock.NowServerSeconds, mono,
                                    delay: (t, ct) => { seen.Add(self[0].Status); mono.Advance(t.TotalSeconds); return Task.CompletedTask; },
                                    jitter: new Random(1));

        [Test]
        public void NetworkFailures_RetryWithTheSameRequestId_ShowingRetrying()   // 06 rule 12.8, 00 contract 13
        {
            var s = TestSession.Open();
            var svc = new FlakyInbox(Item("ibx_1", 700)) { Failures = 2 };
            var seen = new List<InboxClaimStatus>();
            var mono = new FakeMonoClock();
            var flow = Flow(s, svc, seen, mono, new InboxFlow[1]);
            long dia = s.Sim.State.Wallet.Diamond;
            var o = flow.ClaimAsync("ibx_1").GetAwaiter().GetResult();
            Assert.That((o.Ok, o.Applied), Is.EqualTo((true, true)));
            Assert.That(svc.RequestIds.Count, Is.EqualTo(3));
            Assert.That(svc.RequestIds.Distinct().Count(), Is.EqualTo(1), "every retry reuses the requestId");
            Assert.That(seen, Is.All.EqualTo(InboxClaimStatus.Retrying));
            Assert.That(flow.Status, Is.EqualTo(InboxClaimStatus.Idle));
            Assert.That(s.Sim.State.Wallet.Diamond, Is.EqualTo(dia + 700));
        }

        [Test]
        public void After10Seconds_TheClaimFailsRetryable_AndTheNextTryReplaysTheSameRequestId()   // 06 rule 12.8
        {
            var s = TestSession.Open();
            var svc = new FlakyInbox(Item("ibx_1", 700)) { Failures = 1000 };
            var seen = new List<InboxClaimStatus>();
            var mono = new FakeMonoClock();
            var flow = Flow(s, svc, seen, mono, new InboxFlow[1]);
            long start = mono.Ticks;
            var o = flow.ClaimAsync("ibx_1").GetAwaiter().GetResult();
            Assert.That((o.Ok, o.Retryable, o.ErrorCode), Is.EqualTo((false, true, "Offline")));
            double waited = (mono.Ticks - start) / (double)FakeMonoClock.TicksPerSecond;
            Assert.That(waited, Is.EqualTo(InboxFlow.RetryWindowSec).Within(1e-6), "Retrying... for 10 s, then the error");
            Assert.That(s.Sim.State.Inbox.ClaimedIds, Is.Empty);
            string first = svc.RequestIds[0];

            svc.Failures = 0;
            var retry = flow.ClaimAsync("ibx_1").GetAwaiter().GetResult();
            Assert.That(retry.Applied, Is.True);
            Assert.That(svc.RequestIds.Last(), Is.EqualTo(first), "the player's Retry replays the same request");
        }
    }
}
