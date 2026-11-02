using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Summons
{
    /// <summary>The five points of 01 AC 8 where a process kill can land (test hook, <see cref="SummonFlow.Checkpoint"/>).</summary>
    public enum SummonFlowPoint { BeforeIntentSave, AfterIntentSave, AfterServiceReturned, AfterApply, AfterFinalSave }

    /// <summary>
    /// The Runtime summon flow of 01 section 8.5.1 (issue #68), kill-safe:
    /// <list type="number">
    /// <item>refuse while <c>Pending.Summon</c> is set (one in flight);</item>
    /// <item>persist the intent <c>Pending.Summon = {requestId, banner, count, payment}</c> and save synchronously before any
    /// roll (a failed save refuses the summon: the intent must be on disk);</item>
    /// <item>ask the summon service (M0: <see cref="LocalSummonService"/>, pure, completes synchronously);</item>
    /// <item>apply the payload through the logged ApplySummonResult, clear the intent, publish, save.</item>
    /// </list>
    /// A kill before step 4's save leaves either no intent and no charge, or the saved intent and no charge; boot or resume
    /// then re-runs it with the same requestId (<see cref="ResolveAsync"/>, 01 section 8.2 step 3, after the offline modal
    /// closes). The pure local service re-rolls from the unchanged state and stream counter, and the sim refuses a
    /// requestId it already applied, so a request is charged at most once.
    ///
    /// Decision (issue #68): the intent is Runtime bookkeeping written straight into <c>GameState.Pending</c>, as 01's
    /// sequence diagram has it, not a logged command. It is set and cleared inside one synchronous flow, so a command log
    /// replay sees the same states between commands; only a killed flow leaves it in a save.
    /// Main thread only. TODO(#145): the Remote pre-roll, retry and "Pending..." path of 01 section 8.5.2.
    /// </summary>
    public sealed class SummonFlow
    {
        readonly SessionRunner _runner;
        bool _resolveAttempted;

        public SummonFlow(SessionRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        /// <summary>Test hook: called at each of 01 AC 8's kill points. A test simulates a kill by snapshotting the store there.</summary>
        internal Action<SummonFlowPoint> Checkpoint { get; set; }

        /// <summary>The saved intent, or null.</summary>
        public PendingSummon Pending => _runner.Sim.State.Pending.Summon;

        /// <summary>
        /// True when a pending intent should be re-run now: one is saved, SCR_OFFLINE_REWARD is not open (01 section 8.2
        /// step 3) and this boot or resume has not tried yet. The client checks it once per frame.
        /// </summary>
        public bool ShouldResolve => Pending != null && !_resolveAttempted && !_runner.TicksHeldForOfflineReward;

        /// <summary>SessionRunner.OnResume: a resume may try a pending intent again.</summary>
        internal void OnResume() => _resolveAttempted = false;

        /// <summary>
        /// SCR_SUMMON's x1 / x10 / ticket button. Fails with <see cref="SummonError.InFlight"/> while an intent is pending
        /// and <see cref="SummonError.SaveFailed"/> when the intent cannot be saved (nothing rolled, nothing charged).
        /// </summary>
        public Task<Result<SummonResultPayload, SummonError>> SummonAsync(Banner banner, int count, SummonPayment payment,
                                                                         CancellationToken ct = default)
        {
            if (Pending != null) return Task.FromResult(Result<SummonResultPayload, SummonError>.Fail(SummonError.InFlight));
            var request = SummonRequest.New(banner, count, payment);
            Hit(SummonFlowPoint.BeforeIntentSave);
            var state = _runner.Sim.State;
            state.Pending.Summon = new PendingSummon(request.RequestId, banner, count, payment.ToString(), _runner.Services.Clock.NowServerSeconds);
            if (!_runner.SaveNow(SaveTrigger.Summon))
            {
                state.Pending.Summon = null;   // the intent is not on disk, so the roll must not happen (01 section 8.5.1 step 2)
                return Task.FromResult(Result<SummonResultPayload, SummonError>.Fail(SummonError.SaveFailed));
            }
            return RunAsync(request, ct);
        }

        /// <summary>
        /// Re-runs the saved intent with its requestId (boot or resume, 01 sections 8.2 step 3 and 8.5.1 step 5). Returns a
        /// completed <see cref="SummonError.None"/> failure when nothing is pending.
        /// </summary>
        public Task<Result<SummonResultPayload, SummonError>> ResolveAsync(CancellationToken ct = default)
        {
            _resolveAttempted = true;
            var p = Pending;
            if (p == null) return Task.FromResult(Result<SummonResultPayload, SummonError>.Fail(SummonError.None));
            if (!Enum.TryParse(p.Payment, true, out SummonPayment payment) || !Enum.IsDefined(typeof(SummonPayment), payment))
            {
                Clear();   // an unreadable intent was never charged (step 4 not reached); drop it
                return Task.FromResult(Result<SummonResultPayload, SummonError>.Fail(SummonError.None));
            }
            return RunAsync(new SummonRequest(p.RequestId, p.Banner, p.Count, payment), ct);
        }

        async Task<Result<SummonResultPayload, SummonError>> RunAsync(SummonRequest request, CancellationToken ct)
        {
            var svc = _runner.Services.Summon ?? throw new InvalidOperationException("no summon service in this GameServices");
            Hit(SummonFlowPoint.AfterIntentSave);
            var mono = _runner.Mono;
            long started = mono.Ticks;
            var state = _runner.Sim.State;
            state.Banners.TryGetValue(request.Banner, out var bsBefore);
            int pityBefore = bsBefore?.Pity ?? 0;
            long expBefore = bsBefore?.Exp ?? 0;

            var result = await svc.PullAsync(request, ct).ConfigureAwait(false);
            Hit(SummonFlowPoint.AfterServiceReturned);
            if (!result.IsOk)
            {
                // A retryable failure keeps the intent for the next resolve (Remote); a refusal charged nothing, so drop it.
                if (result.Error != SummonError.Network && result.Error != SummonError.RateLimited) Clear();
                return result;
            }

            var applied = _runner.Issue(new ApplySummonResult(result.Value));
            if (!applied.Ok)
            {
                Clear();   // rejected means nothing was charged
                if (applied.Reason == CollectionRejectReasons.SUMMON_ALREADY_APPLIED)
                    return Result<SummonResultPayload, SummonError>.Fail(SummonError.Replayed);
                throw new InvalidOperationException("ApplySummonResult rejected a service payload: " + applied);
            }
            Hit(SummonFlowPoint.AfterApply);
            state.Pending.Summon = null;
            _runner.PublishDrained();
            _runner.SaveNow(SaveTrigger.Summon);
            Hit(SummonFlowPoint.AfterFinalSave);
            Track(result.Value, pityBefore, expBefore, (mono.Ticks - started) * 1000.0 / mono.Frequency);
            return result;
        }

        void Clear()
        {
            _runner.Sim.State.Pending.Summon = null;
            _runner.SaveNow(SaveTrigger.Summon);
        }

        void Hit(SummonFlowPoint p) => Checkpoint?.Invoke(p);

        /// <summary>
        /// 04 section 13's <c>summon</c> row, once per request after ApplySummonResult, then one <c>item_acquired</c> per
        /// result (acquireReason SUMMON). A replayed requestId returns before this, so it tracks nothing (04 AC 12).
        /// </summary>
        void Track(SummonResultPayload p, int pityBefore, long expBefore, double latencyMs)
        {
            var analytics = _runner.Services.Analytics;
            if (analytics == null) return;
            var def = _runner.Sim.Db.Banners[p.Banner];
            long diamonds = 0, tickets = 0;
            foreach (var d in p.Debits)
            {
                if (d.Currency == Currency.DIAMOND) diamonds += (long)-d.Amount;
                else tickets += (long)-d.Amount;
            }
            int lplus = 0;
            bool guarantee = false;
            var results = new List<AnalyticsEvents.SummonResultRow>(p.Pulls.Length);
            foreach (var r in p.Pulls)
            {
                if (r.Rarity >= Rarity.LEGENDARY) lplus++;
                guarantee |= r.GuaranteeApplied;
                results.Add(new AnalyticsEvents.SummonResultRow(r.ItemId, r.Rarity, r.IsNew, r.LevelBefore, r.LevelAfter, r.OverflowRockCandy,
                                                                r.GuaranteeApplied));
            }
            analytics.Track(new AnalyticsEvents.Summon(
                p.RequestId, p.Banner, p.Count, diamonds > 0 ? "DIAMOND" : "TICKET", diamonds, tickets, pityBefore, p.PityAfter,
                def.LevelFor(expBefore), def.LevelFor(p.BannerExpAfter), results, lplus, guarantee,
                p.StreamCounterAfter.HasValue ? AnalyticsEvents.Summon.LOCAL : AnalyticsEvents.Summon.SERVER, latencyMs));
            foreach (var r in p.Pulls)
                analytics.Track(new AnalyticsEvents.ItemAcquired(p.Banner, r.ItemId, r.Rarity, r.IsNew, r.CopiesAfter, r.LevelBefore, r.LevelAfter,
                                                                 AnalyticsEvents.ItemAcquired.SUMMON, p.RequestId));
        }
    }
}
