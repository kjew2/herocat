using System;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;

namespace HeroCat.Runtime.Summons
{
    /// <summary>
    /// The M0 local roll (04 section 12, 01 section 8.5.1). Reads the GameState but never writes it (issues #6, #13):
    /// checks the wallet before drawing, clones the banner's BannerState and collection, runs the shared
    /// <see cref="PullAlgorithm"/> with a reader over <c>summon_&lt;banner&gt;</c> starting at its saved counter, and
    /// returns a payload carrying the Debits and StreamCounterAfter. ApplySummonResult then debits, sets the counter
    /// and writes the results, so Counter == 3 * TotalPulls. Tasks complete synchronously.
    /// </summary>
    public sealed class LocalSummonService : ISummonService
    {
        readonly Func<GameState> _state;
        readonly ContentDb _db;
        readonly Func<long> _now;
        string _lastRequestId;
        long _lastCounterBefore;
        SummonResultPayload _lastPayload;

        /// <param name="state">The live state, read only.</param>
        /// <param name="now">Server time stamped into the payload (FirstAcquiredServerTime of new items).</param>
        public LocalSummonService(Func<GameState> state, ContentDb db, Func<long> now)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _now = now ?? throw new ArgumentNullException(nameof(now));
        }

        public Task<Result<SummonResultPayload, SummonError>> PullAsync(SummonRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Pull(request));
        }

        /// <summary>The synchronous body of <see cref="PullAsync"/> (bots call it directly).</summary>
        public Result<SummonResultPayload, SummonError> Pull(SummonRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var s = _state() ?? throw new InvalidOperationException("no state");
            if (!_db.Banners.TryGetValue(request.Banner, out var def) || !def.Enabled || def.Levels.Count == 0)
                return Result<SummonResultPayload, SummonError>.Fail(SummonError.BannerClosed);
            if (request.Count != 1 && request.Count != def.GuaranteeBatchSize)
                throw new ArgumentOutOfRangeException(nameof(request), request.Count, "a summon is 1 or a guaranteed batch");

            string streamName = PullAlgorithm.StreamName(request.Banner);
            if (!s.Rng.TryGetValue(streamName, out var stream) || stream == null)
                throw new InvalidOperationException($"the save has no {streamName} stream");

            // One-deep replay cache (01 section 8.5.1 step 5): the same request against an unchanged stream returns the same payload.
            if (_lastPayload != null && request.RequestId == _lastRequestId && stream.Counter == _lastCounterBefore)
                return Result<SummonResultPayload, SummonError>.Ok(_lastPayload);

            // Wallet first, no draw on refusal (04 section 10).
            CurrencyDelta debit;
            if (request.Payment == SummonPayment.Diamonds)
            {
                long cost = (long)def.PullCostDiamonds * request.Count;
                if (s.Wallet.Diamond < cost) return Result<SummonResultPayload, SummonError>.Fail(SummonError.NotEnoughDiamonds);
                debit = new CurrencyDelta(Currency.DIAMOND, -cost);
            }
            else
            {
                if (s.Wallet.Get(def.TicketCurrency) < request.Count) return Result<SummonResultPayload, SummonError>.Fail(SummonError.NotEnoughTickets);
                debit = new CurrencyDelta(def.TicketCurrency, -request.Count);
            }

            // Roll on clones (issue #13).
            s.Banners.TryGetValue(request.Banner, out var live);
            var st = new BannerState { Pity = live?.Pity ?? 0, Exp = live?.Exp ?? 0, TotalPulls = live?.TotalPulls ?? 0 };
            var col = CloneItems(CollectionRules.For(s, request.Banner));
            var rng = StreamUniformSource.From(stream);
            var results = PullAlgorithm.PullBatch(_db, def, st, col, rng, request.Count);

            var pulls = new SummonPull[results.Length];
            for (int i = 0; i < results.Length; i++)
            {
                var r = results[i];
                pulls[i] = new SummonPull(r.ItemId, r.Rarity, r.IsNew, r.LevelBefore, r.LevelAfter, r.CopiesAfter, r.OverflowRockCandy,
                                          r.GuaranteeApplied);
            }
            var payload = new SummonResultPayload(request.RequestId, request.Banner, request.Count, pulls, st.Pity,
                                                  def.LevelFor(st.Exp), checked((int)st.Exp), new[] { debit }, null, rng.Counter, _now());
            _lastRequestId = request.RequestId;
            _lastCounterBefore = stream.Counter;
            _lastPayload = payload;
            return Result<SummonResultPayload, SummonError>.Ok(payload);
        }

        public Task<RateTableDto> GetRateTableAsync(Banner banner, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var def = _db.Banners[banner];
            return Task.FromResult(new RateTableDto(banner, _db.Version, SummonQuery.RateTables(_db, banner), def.PitySoftStart,
                                                    def.PitySoftStep, def.PityHard, def.MShareOfLplus));
        }

        static CollectionState CloneItems(CollectionState c)
        {
            var r = new CollectionState();
            foreach (var kv in c.Items)
                if (kv.Value != null)
                    r.Items[kv.Key] = new OwnedItem { Copies = kv.Value.Copies, LimitBreaks = kv.Value.LimitBreaks, FirstAcquiredServerTime = kv.Value.FirstAcquiredServerTime };
            return r;
        }
    }
}
