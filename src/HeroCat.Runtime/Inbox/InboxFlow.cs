using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Save;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Sim.Commands;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Inbox
{
    /// <summary>What SCR_INBOX shows while a claim runs (06 rule 12.8: "Retrying..." rather than an error for the first 10 s).</summary>
    public enum InboxClaimStatus { Idle, Claiming, Retrying }

    /// <summary>
    /// The end of one item's claim. Ok: the server confirmed it (or it was already claimed) and the sim holds the id in
    /// Inbox.ClaimedIds. Applied: this call credited the grant (false for a reconciled double claim). ErrorCode: the
    /// server's or the sim's reason when not Ok. Retryable: the failure was the network; claiming again reuses the requestId.
    /// </summary>
    public sealed record InboxClaimOutcome(string InboxItemId, bool Ok, bool Applied, string ErrorCode, bool Retryable, InboxGrantPayload Grant)
    {
        internal static InboxClaimOutcome Fail(string id, string code, bool retryable) => new InboxClaimOutcome(id, false, false, code, retryable, null);
    }

    /// <summary>
    /// The Runtime inbox flow (01 section 8.9 step 6, 06 section 9, 00 contract 13; issue #176): ClaimInbox(id) and
    /// ClaimAllInbox() are server mutations, not sim commands. The flow asks <see cref="IInboxService"/> with a
    /// client-generated requestId and, only once the server confirms, applies each grant through the logged
    /// ApplyInboxGrant. INBOX_ALREADY_CLAIMED (AlreadyClaimed) carrying the original grants is success: applied once if
    /// the id is not in Inbox.ClaimedIds, else nothing. A network failure is retried with the same requestId and shows
    /// <see cref="InboxClaimStatus.Retrying"/> for up to <see cref="RetryWindowSec"/>; after that the claim fails as
    /// retryable and the next claim of the same item replays the same requestId, so a grant is never applied twice.
    /// Main thread only.
    /// </summary>
    public sealed class InboxFlow
    {
        /// <summary>06 rule 12.8: retry silently ("Retrying...") for the first 10 s.</summary>
        public const double RetryWindowSec = 10;

        readonly IInboxService _svc;
        readonly Func<GameState> _state;
        readonly Func<Command, ApplyResult> _issue;
        readonly Func<long> _now;
        readonly IMonoClock _mono;
        readonly Action _afterApply;
        readonly Func<TimeSpan, CancellationToken, Task> _delay;
        readonly Random _jitter;
        readonly IAnalytics _analytics;
        readonly Dictionary<string, string> _requestIds = new Dictionary<string, string>(StringComparer.Ordinal);
        string _claimAllRequestId;
        List<InboxItem> _items = new List<InboxItem>();

        /// <summary>The session's flow: its inbox service, the live state, Issue, and a publish plus save after each grant.</summary>
        public InboxFlow(SessionRunner runner)
            : this(runner?.Services.Inbox, () => runner.Sim.State, runner.Issue, () => runner.Services.Clock.NowServerSeconds, runner.Mono,
                   () => { runner.PublishDrained(); runner.SaveNow(SaveTrigger.Inbox); }, analytics: runner?.Services.Analytics)
        {
        }

        /// <param name="delay">Waits between retries (tests pass one that advances a fake monotonic clock).</param>
        /// <param name="jitter">Full-jitter source for the backoff (01 section 5.2).</param>
        /// <param name="analytics">Where 06 section 13's <c>inbox_claimed</c> goes; null sends nothing.</param>
        public InboxFlow(IInboxService service, Func<GameState> state, Func<Command, ApplyResult> issue, Func<long> nowServerSeconds,
                         IMonoClock mono, Action afterApply = null, Func<TimeSpan, CancellationToken, Task> delay = null, Random jitter = null,
                         IAnalytics analytics = null)
        {
            _analytics = analytics;
            _svc = service ?? throw new ArgumentNullException(nameof(service));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _issue = issue ?? throw new ArgumentNullException(nameof(issue));
            _now = nowServerSeconds ?? throw new ArgumentNullException(nameof(nowServerSeconds));
            _mono = mono ?? StopwatchMonoClock.Instance;
            _afterApply = afterApply;
            _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
            _jitter = jitter ?? new Random();
        }

        /// <summary>The claim in progress, for SCR_INBOX's "Retrying..." label.</summary>
        public InboxClaimStatus Status { get; private set; }

        /// <summary>The rows of the last <see cref="RefreshAsync"/>, minus the ones claimed since.</summary>
        public IReadOnlyList<InboxItem> Items => _items;

        /// <summary>Claimable rows: available, unexpired and not in Inbox.ClaimedIds (06 section 9).</summary>
        public IReadOnlyList<InboxItem> Available => InboxRules.Available(_items, _now(), _state().Inbox.ClaimedIds);

        /// <summary>Scheduled portions not deliverable yet (07 shows at most one per package, greyed; issue #51).</summary>
        public IReadOnlyList<InboxItem> Upcoming
        {
            get
            {
                long now = _now();
                var list = new List<InboxItem>();
                foreach (var i in _items) if (InboxRules.IsUpcoming(i, now)) list.Add(i);
                return list;
            }
        }

        /// <summary>06 rule 9.5: the HUD inbox badge.</summary>
        public int BadgeCount => InboxRules.BadgeCount(_items, _now(), _state().Inbox.ClaimedIds);

        /// <summary>GET /inbox. A failure propagates as ServiceException and keeps the previous rows.</summary>
        public async Task<IReadOnlyList<InboxItem>> RefreshAsync(CancellationToken ct = default)
        {
            var rows = await _svc.ListAsync(ct).ConfigureAwait(false);
            _items = rows != null ? new List<InboxItem>(rows) : new List<InboxItem>();
            return _items;
        }

        /// <summary>ClaimInbox(id): claim one item, then apply its grant once.</summary>
        public async Task<InboxClaimOutcome> ClaimAsync(string inboxItemId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(inboxItemId)) throw new ArgumentException("inbox item id required", nameof(inboxItemId));
            if (!_requestIds.TryGetValue(inboxItemId, out var requestId)) _requestIds[inboxItemId] = requestId = NewRequestId();
            InboxClaimResult result;
            try { result = await WithRetry(() => _svc.ClaimAsync(requestId, inboxItemId, ct), ct).ConfigureAwait(false); }
            catch (ServiceException e)
            {
                bool retryable = IsRetryable(e);
                if (!retryable) { _requestIds.Remove(inboxItemId); Forget(inboxItemId); }
                return InboxClaimOutcome.Fail(inboxItemId, e.Code, retryable);
            }
            _requestIds.Remove(inboxItemId);
            var outcome = ApplyGrant(inboxItemId, result.Grant, bulk: false);
            if (outcome.Applied) _afterApply?.Invoke();
            return outcome;
        }

        /// <summary>ClaimAllInbox(): one request for every available item; one outcome per row so a partial failure shows per row.</summary>
        public async Task<IReadOnlyList<InboxClaimOutcome>> ClaimAllAsync(CancellationToken ct = default)
        {
            _claimAllRequestId ??= NewRequestId();
            string requestId = _claimAllRequestId;
            InboxClaimAllResult result;
            try { result = await WithRetry(() => _svc.ClaimAllAsync(requestId, ct), ct).ConfigureAwait(false); }
            catch (ServiceException e)
            {
                bool retryable = IsRetryable(e);
                if (!retryable) _claimAllRequestId = null;
                return new[] { InboxClaimOutcome.Fail(null, e.Code, retryable) };
            }
            _claimAllRequestId = null;
            var outcomes = new List<InboxClaimOutcome>();
            bool applied = false;
            foreach (var row in result.Items ?? Array.Empty<InboxClaimItemResult>())
            {
                if (row == null) continue;
                if (!row.Ok)
                {
                    if (row.ErrorCode != InboxRules.INBOX_NOT_AVAILABLE) Forget(row.InboxItemId);   // expired or gone
                    outcomes.Add(InboxClaimOutcome.Fail(row.InboxItemId, row.ErrorCode, false));
                    continue;
                }
                var o = ApplyGrant(row.InboxItemId, row.Grant, bulk: true);
                applied |= o.Applied;
                outcomes.Add(o);
            }
            if (applied) _afterApply?.Invoke();
            return outcomes;
        }

        /// <summary>
        /// Applies a confirmed grant unless the id is already claimed (01 section 8.9 step 6). A grant this call credits
        /// sends 06 section 13's <c>inbox_claimed</c> (a reconciled double claim credits nothing and sends nothing).
        /// TODO(#271): item lines, and their <c>item_acquired</c> rows with acquireReason MAIL and this claim's requestId
        /// (04 section 13), once the sim applies them.
        /// </summary>
        InboxClaimOutcome ApplyGrant(string inboxItemId, InboxGrantPayload grant, bool bulk)
        {
            var listed = inboxItemId == null ? null : _items.Find(i => i.Id == inboxItemId);   // before Forget drops it
            Forget(inboxItemId);
            if (grant == null) return InboxClaimOutcome.Fail(inboxItemId, InboxRejectReasons.INBOX_BAD_GRANT, false);
            if (grant.InboxItemId != inboxItemId) grant = grant with { InboxItemId = inboxItemId };
            if (_state().Inbox.ClaimedIds.Contains(inboxItemId))
                return new InboxClaimOutcome(inboxItemId, true, false, null, false, grant);
            var r = _issue(new ApplyInboxGrant(grant));
            if (r.Ok)
            {
                TrackClaimed(inboxItemId, grant, listed, bulk);
                return new InboxClaimOutcome(inboxItemId, true, true, null, false, grant);
            }
            if (r.Reason == InboxRejectReasons.INBOX_ALREADY_APPLIED) return new InboxClaimOutcome(inboxItemId, true, false, null, false, grant);
            return InboxClaimOutcome.Fail(inboxItemId, r.Reason, false);
        }

        void TrackClaimed(string inboxItemId, InboxGrantPayload grant, InboxItem listed, bool bulk)
        {
            if (_analytics == null) return;
            long? age = listed != null && listed.CreatedAtServerSeconds > 0 ? Math.Max(0, _now() - listed.CreatedAtServerSeconds) : (long?)null;
            _analytics.Track(new AnalyticsEvents.InboxClaimed(inboxItemId, listed?.Type, grant.Lines, bulk, age));
        }

        async Task<T> WithRetry<T>(Func<Task<T>> call, CancellationToken ct)
        {
            long started = _mono.Ticks;
            Status = InboxClaimStatus.Claiming;
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { return await call().ConfigureAwait(false); }
                    catch (ServiceException e) when (IsRetryable(e))
                    {
                        double elapsed = (_mono.Ticks - started) / (double)_mono.Frequency;
                        if (elapsed >= RetryWindowSec) throw;
                        Status = InboxClaimStatus.Retrying;
                        double capMs = Math.Min(RuntimeConfig.HTTP_RETRY_CAP_MS, RuntimeConfig.HTTP_RETRY_BASE_MS * Math.Pow(2, Math.Min(attempt, 16)));
                        double waitMs = Math.Min(_jitter.NextDouble() * capMs, (RetryWindowSec - elapsed) * 1000.0);
                        // Rounded up to a whole TimeSpan tick so the last wait reaches the window's end exactly.
                        await _delay(TimeSpan.FromTicks((long)Math.Ceiling(waitMs * TimeSpan.TicksPerMillisecond)), ct).ConfigureAwait(false);
                    }
                }
            }
            finally { Status = InboxClaimStatus.Idle; }
        }

        /// <summary>Network-shaped failures retried with the same requestId (01 section 5.2: Offline, Timeout, 5xx, Retryable).</summary>
        static bool IsRetryable(ServiceException e)
        {
            var c = ErrorClassifier.Classify(e).Class;
            return c == ErrorClass.Offline || c == ErrorClass.Timeout || c == ErrorClass.ServerError || c == ErrorClass.Retryable;
        }

        void Forget(string inboxItemId)
        {
            if (inboxItemId != null) _items.RemoveAll(i => i.Id == inboxItemId);
        }

        static string NewRequestId() => Guid.NewGuid().ToString("N");
    }
}
