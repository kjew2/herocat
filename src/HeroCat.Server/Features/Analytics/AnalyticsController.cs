using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using HeroCat.Server.Hosting;
using HeroCat.Server.Pipeline;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Analytics;

/// <summary><c>POST /analytics/events</c> body: up to ANALYTICS_BATCH_MAX queued lines of 02 section 14.1, oldest first.</summary>
public sealed class AnalyticsEventsRequest
{
    /// <summary>Each element is one queued line: <c>{"name", "eventId", envelope fields..., "props": {...}}</c>.</summary>
    [Required] public List<JsonElement>? Events { get; set; }
}

/// <summary>One refused event: its position in the batch, its id when readable, and an <see cref="AnalyticsEventCodes"/> code.</summary>
public sealed record RejectedEvent(int Index, string? EventId, string Code);

/// <summary>
/// <c>POST /analytics/events</c> 200: <c>accepted</c> rows were new, <c>duplicates</c> were already stored (a retried event),
/// <c>rejected</c> were refused one by one. The client drops the whole batch from its queue on any 200.
/// </summary>
public sealed record AnalyticsEventsResponse(int Accepted, int Duplicates, IReadOnlyList<RejectedEvent> Rejected, long ServerTime);

/// <summary>
/// The backend economy-events ingest (specs/08 section 3.5, specs/02 section 14; issue #209). Idempotent by
/// <c>X-Request-Id</c> through the pipeline (the client derives it from the batch's event ids), and per event by
/// <c>(eventId, ts)</c> in the table, so a batch retried after the idempotency TTL still stores each event once.
/// </summary>
[ApiController]
public sealed class AnalyticsController : ControllerBase
{
    readonly AnalyticsEventStore _store;
    readonly TimeProvider _clock;
    readonly ServerOptions _options;

    public AnalyticsController(AnalyticsEventStore store, TimeProvider clock, IOptions<ServerOptions> options)
    {
        _store = store;
        _clock = clock;
        _options = options.Value;
    }

    [HttpPost("v1/analytics/events"), Auth(AuthPolicy.Jwt), RateLimit(60), Idempotent]
    [ProducesResponseType(typeof(AnalyticsEventsResponse), StatusCodes.Status200OK)]
    public async Task<AnalyticsEventsResponse> Post(AnalyticsEventsRequest request, CancellationToken ct)
    {
        var limits = _options.Limits;
        int batchMax = limits.AnalyticsBatchMax > 0 ? limits.AnalyticsBatchMax : 100;
        var events = request.Events ?? throw ApiException.Of(ErrorCodes.VALIDATION, "events is required.", new { field = "events" });
        if (events.Count == 0 || events.Count > batchMax)
            throw ApiException.Of(ErrorCodes.VALIDATION, $"events must hold 1 to {batchMax} events.", new { field = "events", max = batchMax });

        var now = _clock.GetUtcNow();
        Guid player = HttpContext.PlayerId()!.Value;
        int.TryParse(HttpContext.User.FindFirstValue(Caller.AgeClaim), NumberStyles.Integer, CultureInfo.InvariantCulture, out int age);
        var ctx = new IngestContext(
            AnalyticsEventRules.HashPlayerId(player, _options.Analytics.RequireSalt()), AnalyticsEventRules.EnvelopeAgeGate(age),
            _options.Analytics.RequireEnv(), now,
            TimeSpan.FromDays(limits.AnalyticsEventMaxAgeDays > 0 ? limits.AnalyticsEventMaxAgeDays : 30),
            TimeSpan.FromSeconds(limits.ClockMaxSkewAcceptSeconds > 0 ? limits.ClockMaxSkewAcceptSeconds : 120),
            limits.AnalyticsPropsMaxBytes > 0 ? limits.AnalyticsPropsMaxBytes : 4096);

        var rejected = new List<RejectedEvent>();
        var parsed = new List<(int Index, EventRow Row)>();
        for (int i = 0; i < events.Count; i++)
        {
            var p = AnalyticsEventParser.Parse(events[i], ctx);
            if (p.Row != null) parsed.Add((i, p.Row));
            else rejected.Add(new RejectedEvent(i, p.EventId, p.Code!));
        }

        // A row needs its day's partition (02 section 12: no default partition); a day the worker has not created, or one
        // before the table's first partition, is refused per event rather than failing the batch.
        var days = parsed.Select(x => AnalyticsEventRules.PartitionOf(x.Row.Ts)).Distinct().ToList();
        var existing = await _store.ExistingPartitionsAsync(days, ct).ConfigureAwait(false);
        var rows = new List<EventRow>(parsed.Count);
        foreach (var (index, row) in parsed)
        {
            if (existing.Contains(AnalyticsEventRules.PartitionOf(row.Ts))) rows.Add(row);
            else rejected.Add(new RejectedEvent(index, row.EventId.ToString("N"), AnalyticsEventCodes.EVENT_TS_OUT_OF_RANGE));
        }
        rejected.Sort((a, b) => a.Index.CompareTo(b.Index));

        int inserted = await _store.InsertAsync(rows, ct).ConfigureAwait(false);
        return new AnalyticsEventsResponse(inserted, rows.Count - inserted, rejected, now.ToUnixTimeSeconds());
    }
}
