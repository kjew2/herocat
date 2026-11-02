using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;

namespace HeroCat.Runtime.Services
{
    // The server-sensitive seams of 00 contract 11 and 01 section 1.2 (issues #66, #119). Each has a local implementation
    // for M0 and bots (LocalServices.cs) and a remote one from MVP (08). The sim never sees any of them: services return
    // payloads that Runtime turns into system commands. IClock is Clock.cs; ISaveStore is HeroCat.Runtime.Save's
    // (02 section 8); ISummonService is 04's (TODO(#92)).
    // A seam reports failure by throwing HeroCat.Runtime.Errors.ServiceException; local implementations never fail on
    // the network because they have none.

    // ------------------------------------------------------------------------------------- config (08 section 4.6, 01 8.12)

    /// <summary>Maintenance window from config (01 section 8.12 step 4).</summary>
    public sealed record MaintenanceInfo(bool Active, string MessageKey, long UntilServerSeconds)
    {
        public static readonly MaintenanceInfo None = new MaintenanceInfo(false, null, 0);
    }

    /// <summary>
    /// Kill switches (01 section 8.12 step 1). They apply immediately, never touch GameState, and a view-model reads them
    /// to disable a button with FEATURE_DISABLED.
    /// </summary>
    public sealed record KillSwitches(bool ShopEnabled, bool DungeonsEnabled, bool CloudSaveEnabled, bool AdsEnabled,
                                      IReadOnlyDictionary<Banner, bool> SummonEnabled)
    {
        /// <summary>Everything on (Local mode, and the remote default when no config was ever fetched; 8.1 step 2).</summary>
        public static readonly KillSwitches AllOn = new KillSwitches(true, true, true, true, new Dictionary<Banner, bool>());

        /// <summary>A banner missing from the map is enabled.</summary>
        public bool IsSummonEnabled(Banner b) => SummonEnabled == null || !SummonEnabled.TryGetValue(b, out bool on) || on;
    }

    /// <summary>The parts of GET /config the client acts on (08 section 4.6; 01 section 8.1 step 2 and 8.12).</summary>
    public sealed record ClientConfig(string ContentVersion, string MinClientVersion, MaintenanceInfo Maintenance, KillSwitches Flags);

    public interface IConfigService
    {
        /// <summary>The config in force: the last fetched, else the cached, else the defaults (everything on, no gate).</summary>
        ClientConfig Current { get; }

        /// <summary>
        /// Boot step 2 and every CONFIG_REFRESH_S. Never throws for a network failure: on timeout or offline it keeps the
        /// cached config (under CONFIG_CACHE_MAX_AGE_S) or the defaults and returns <see cref="Current"/>.
        /// </summary>
        Task<ClientConfig> FetchAsync(CancellationToken ct = default);
    }

    // ------------------------------------------------------------------------------------- inbox (06 section 9, 08 3.2)

    /// <summary>
    /// One inbox row (06 section 9 model, 08 section 3.2 wire). <see cref="Type"/> is the item type in 06's spelling
    /// (IAP, PACKAGE_DAILY, MAIL, COMPENSATION, COUPON, REFUND; 08's wire <c>kind</c> upper-cased). AvailableAtServerSeconds
    /// above now: a scheduled package portion, not claimable yet (issue #51). ExpiresAtServerSeconds is
    /// <see cref="InboxRules.NeverExpires"/> for IAP items (06 rule 9.1). TitleKey / BodyKey / Args are presentation (07).
    /// </summary>
    public sealed record InboxItem(string Id, InboxGrantPayload Grant, long ExpiresAtServerSeconds, long AvailableAtServerSeconds = 0,
                                   string Type = null, string TitleKey = null, string BodyKey = null,
                                   IReadOnlyDictionary<string, string> Args = null);

    /// <summary>A claim's grants. AlreadyClaimed: the 409 INBOX_ALREADY_CLAIMED success-equivalent (01 5.2, 8.9 step 6).</summary>
    public sealed record InboxClaimResult(InboxGrantPayload Grant, bool AlreadyClaimed);

    /// <summary>One row of a claim-all response (06 rule 9.3): Grant on success, else ErrorCode (e.g. INBOX_EXPIRED).</summary>
    public sealed record InboxClaimItemResult(string InboxItemId, InboxGrantPayload Grant, string ErrorCode)
    {
        public bool Ok => ErrorCode == null && Grant != null;
    }

    /// <summary>A claim-all response: per-item results, oldest first (08 section 3.2).</summary>
    public sealed record InboxClaimAllResult(IReadOnlyList<InboxClaimItemResult> Items);

    public interface IInboxService
    {
        /// <summary>Unclaimed, unexpired rows (08 section 3.2). Remote may include the next future package portion.</summary>
        Task<IReadOnlyList<InboxItem>> ListAsync(CancellationToken ct = default);

        /// <summary>
        /// Claims one item. Replay-safe by <paramref name="requestId"/>: a retry returns the same result. Throws
        /// ServiceException(Rejected) with INBOX_NOT_FOUND, INBOX_NOT_AVAILABLE or INBOX_EXPIRED. The caller applies the
        /// grant with ApplyInboxGrant only if the id is not already in Inbox.ClaimedIds.
        /// </summary>
        Task<InboxClaimResult> ClaimAsync(string requestId, string inboxItemId, CancellationToken ct = default);

        /// <summary>
        /// Claims every available, unexpired item in one request (06 rule 9.3; up to <see cref="InboxRules.ClaimAllMax"/>,
        /// oldest first). Replay-safe by <paramref name="requestId"/>. A row that fails is reported, not thrown.
        /// </summary>
        Task<InboxClaimAllResult> ClaimAllAsync(string requestId, CancellationToken ct = default);
    }

    /// <summary>The client's inbox rules (06 section 9, 08 section 3.2; issue #176), shared by the services and the flow.</summary>
    public static class InboxRules
    {
        public const string INBOX_NOT_FOUND = "INBOX_NOT_FOUND";
        public const string INBOX_NOT_AVAILABLE = "INBOX_NOT_AVAILABLE";
        public const string INBOX_ALREADY_CLAIMED = "INBOX_ALREADY_CLAIMED";
        public const string INBOX_EXPIRED = "INBOX_EXPIRED";

        /// <summary>expiresAt null on the wire: IAP items never expire (06 rule 9.1).</summary>
        public const long NeverExpires = long.MaxValue;

        /// <summary>08 section 3.2: claim-all takes up to 50 items, oldest first.</summary>
        public const int ClaimAllMax = 50;

        /// <summary>Claimable now: available (availableAt reached) and not expired.</summary>
        public static bool IsAvailable(InboxItem item, long nowServerSec) =>
            item != null && nowServerSec >= item.AvailableAtServerSeconds && nowServerSec < item.ExpiresAtServerSeconds;

        /// <summary>A scheduled portion not deliverable yet (07 may show the next one greyed; issue #51).</summary>
        public static bool IsUpcoming(InboxItem item, long nowServerSec) =>
            item != null && nowServerSec < item.AvailableAtServerSeconds && nowServerSec < item.ExpiresAtServerSeconds;

        /// <summary>The rows SCR_INBOX lists as claimable: available and not in <paramref name="claimedIds"/>, in input order.</summary>
        public static List<InboxItem> Available(IEnumerable<InboxItem> items, long nowServerSec, ICollection<string> claimedIds)
        {
            var list = new List<InboxItem>();
            if (items == null) return list;
            foreach (var i in items)
                if (IsAvailable(i, nowServerSec) && (claimedIds == null || !claimedIds.Contains(i.Id))) list.Add(i);
            return list;
        }

        /// <summary>06 rule 9.5: the HUD badge, the count of available unclaimed items.</summary>
        public static int BadgeCount(IEnumerable<InboxItem> items, long nowServerSec, ICollection<string> claimedIds) =>
            Available(items, nowServerSec, claimedIds).Count;

        /// <summary>
        /// The Wallet reason a grant of this item type carries when the server does not name one (01 section 2.5):
        /// IAP and PACKAGE_DAILY portions are IAP, REFUND is REFUND, ADMIN is ADMIN, everything else (MAIL,
        /// COMPENSATION, COUPON) is MAIL. The server's own <c>reason</c> wins when present.
        /// </summary>
        public static CurrencyReason ReasonFor(string type)
        {
            switch (type)
            {
                case "IAP": case "PACKAGE_DAILY": return CurrencyReason.IAP;
                case "REFUND": return CurrencyReason.REFUND;
                case "ADMIN": return CurrencyReason.ADMIN;
                default: return CurrencyReason.MAIL;
            }
        }
    }

    // ------------------------------------------------------------------------------------- receipts (08 section 5, 01 8.9)

    public sealed record ReceiptRequest(string RequestId, string Platform, string Sku, string TransactionId, string Payload);

    /// <summary>Status GRANTED or ALREADY_GRANTED (treated identically, 01 section 8.9 step 3).</summary>
    public sealed record ReceiptResult(string Status, string InboxItemId)
    {
        public const string GRANTED = "GRANTED";
        public const string ALREADY_GRANTED = "ALREADY_GRANTED";
    }

    public interface IReceiptValidator
    {
        Task<ReceiptResult> ValidateAsync(ReceiptRequest request, CancellationToken ct = default);
    }

    // ------------------------------------------------------------------------------------- dungeon seeds (01 8.8, 08 3.3)

    public sealed record DungeonSeedRequest(string RequestId, Dungeon Kind, int Floor);

    /// <summary>
    /// A run seed for StartDungeon(kind, floor, seed, grantId, localSeed). Local: the next `dungeon` stream draw, not
    /// consumed (StartDungeon consumes it), GrantId "" and LocalSeed true. Remote: the server seed and its grant id.
    /// </summary>
    public sealed record DungeonSeedGrant(ulong Seed, string GrantId, long ExpiresAtServerSeconds, bool LocalSeed);

    /// <summary>The seventh seam (00 contract 11, decision 57; issue #25).</summary>
    public interface IDungeonSeedService
    {
        Task<DungeonSeedGrant> RequestSeedAsync(DungeonSeedRequest request, CancellationToken ct = default);
    }

    // ------------------------------------------------------------------------------------- network

    /// <summary>One HTTP exchange at the transport level: the seam Local-mode tests use to prove no network call is made.</summary>
    public sealed record NetRequest(string Method, string Path, string Body, string RequestId);

    public sealed record NetResponse(int Status, string Body, long ServerTimeSec);

    /// <summary>
    /// The only door to the network (01 section 5). Local builds are given one and must never call it (08 AC 24: a
    /// Local-mode session makes zero network calls; Runtime tests pass a transport that fails the test on any call).
    /// </summary>
    public interface INetTransport
    {
        Task<NetResponse> SendAsync(NetRequest request, CancellationToken ct = default);
    }
}
