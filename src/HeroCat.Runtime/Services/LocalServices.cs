using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Errors;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Services
{
    // The M0 local seams (08 section 1 "M0" column, section 12; 10 section 1.2 "Local seams"; issue #119). They touch no
    // network and never write GameState: anything that changes state comes back as a payload for a system command.

    /// <summary>Bundled content only: every kill switch on, no maintenance, no client gate (08 section 4.6).</summary>
    public sealed class LocalConfigService : IConfigService
    {
        public LocalConfigService(string contentVersion)
        {
            Current = new ClientConfig(contentVersion ?? "", null, MaintenanceInfo.None, KillSwitches.AllOn);
        }

        public ClientConfig Current { get; }

        public Task<ClientConfig> FetchAsync(CancellationToken ct = default) => Task.FromResult(Current);
    }

    /// <summary>
    /// The local inbox (08 section 5.5): empty in M0, so a claim is INBOX_NOT_FOUND. Bots and debug builds can
    /// <see cref="Add"/> rows (issue #215's spender bots). Claims are idempotent: the same requestId replays its result,
    /// and a second claim of the same item with a new requestId is the INBOX_ALREADY_CLAIMED success-equivalent.
    /// </summary>
    public sealed class LocalInboxService : IInboxService
    {
        public const string INBOX_NOT_FOUND = "INBOX_NOT_FOUND";

        readonly List<InboxItem> _items = new List<InboxItem>();
        readonly Dictionary<string, InboxClaimResult> _byRequest = new Dictionary<string, InboxClaimResult>(StringComparer.Ordinal);
        readonly Dictionary<string, InboxGrantPayload> _claimed = new Dictionary<string, InboxGrantPayload>(StringComparer.Ordinal);

        public void Add(InboxItem item) => _items.Add(item ?? throw new ArgumentNullException(nameof(item)));

        public Task<IReadOnlyList<InboxItem>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InboxItem>>(_items.ToArray());

        public Task<InboxClaimResult> ClaimAsync(string requestId, string inboxItemId, CancellationToken ct = default)
        {
            if (requestId != null && _byRequest.TryGetValue(requestId, out var replay)) return Task.FromResult(replay);
            InboxClaimResult result;
            if (inboxItemId != null && _claimed.TryGetValue(inboxItemId, out var grant))
                result = new InboxClaimResult(grant, AlreadyClaimed: true);
            else
            {
                int i = _items.FindIndex(x => x.Id == inboxItemId);
                if (i < 0) throw new ServiceException(ErrorClass.Rejected, INBOX_NOT_FOUND, "inbox item " + inboxItemId + " not found", 404);
                var item = _items[i];
                _items.RemoveAt(i);
                _claimed[item.Id] = item.Grant;
                result = new InboxClaimResult(item.Grant, AlreadyClaimed: false);
            }
            if (requestId != null) _byRequest[requestId] = result;
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Release Local builds have no store (the shop is not available in Local mode, 01 section 6): validating a receipt
    /// is a programmer error.
    /// </summary>
    public sealed class DisabledReceiptValidator : IReceiptValidator
    {
        public Task<ReceiptResult> ValidateAsync(ReceiptRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("receipt validation is unsupported in Local mode (01 section 6)");
    }

    /// <summary>
    /// Debug builds and bots: every receipt for a SKU in <paramref name="catalog"/> is GRANTED into the local inbox
    /// (reason IAP), idempotent on the transaction id like the server (ALREADY_GRANTED with the same inbox id).
    /// An unknown SKU is RECEIPT_INVALID.
    /// </summary>
    public sealed class FakeReceiptValidator : IReceiptValidator
    {
        readonly LocalInboxService _inbox;
        readonly Func<string, GrantLine[]> _catalog;
        readonly IClock _clock;
        readonly Dictionary<string, string> _granted = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <param name="catalog">Grant lines for a SKU, or null when the SKU is unknown.</param>
        public FakeReceiptValidator(LocalInboxService inbox, Func<string, GrantLine[]> catalog, IClock clock)
        {
            _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public Task<ReceiptResult> ValidateAsync(ReceiptRequest request, CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.TransactionId != null && _granted.TryGetValue(request.TransactionId, out var id))
                return Task.FromResult(new ReceiptResult(ReceiptResult.ALREADY_GRANTED, id));
            var lines = _catalog(request.Sku);
            if (lines == null) throw new ServiceException(ErrorClass.Rejected, "RECEIPT_INVALID", "unknown sku " + request.Sku, 400);
            string inboxId = "iap_" + (request.TransactionId ?? request.RequestId);
            _inbox.Add(new InboxItem(inboxId, new InboxGrantPayload(inboxId, lines, CurrencyReason.IAP), long.MaxValue,
                                     _clock.NowServerSeconds));
            if (request.TransactionId != null) _granted[request.TransactionId] = inboxId;
            return Task.FromResult(new ReceiptResult(ReceiptResult.GRANTED, inboxId));
        }
    }

    /// <summary>
    /// M0 dungeon seeds (01 section 8.8 step 2): the next draw of the persisted `dungeon` stream, read without advancing
    /// it; Apply(StartDungeon) consumes that one draw, so the service never writes state (as for summons, issue #6).
    /// </summary>
    public sealed class LocalDungeonSeedService : IDungeonSeedService
    {
        readonly Func<GameState> _state;

        /// <param name="state">The live state (read only). A function because the composition root runs before the save loads.</param>
        public LocalDungeonSeedService(Func<GameState> state) { _state = state ?? throw new ArgumentNullException(nameof(state)); }

        public Task<DungeonSeedGrant> RequestSeedAsync(DungeonSeedRequest request, CancellationToken ct = default)
        {
            var s = _state() ?? throw new InvalidOperationException("no game state yet: dungeon seeds are requested after boot");
            if (!s.Rng.TryGetValue(RngStreams.Dungeon, out var stream) || !RngStreams.TryParseSeed(stream.Seed, out ulong seed))
                throw new InvalidOperationException("the `dungeon` stream is missing or malformed (02 R4)");
            ulong draw = SplitMix64.Derive(seed, (ulong)stream.Counter);
            return Task.FromResult(new DungeonSeedGrant(draw, "", long.MaxValue, LocalSeed: true));
        }
    }
}
