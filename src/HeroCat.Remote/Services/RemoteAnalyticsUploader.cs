using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Runtime.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HeroCat.Remote.Services
{
    /// <summary>
    /// The backend leg of the analytics sink (02 section 14.2, issue #297): sends a batch's economy events
    /// (<see cref="AnalyticsNames.IsEconomy"/>) as <c>POST /analytics/events</c> (08 section 3.5) over the
    /// <see cref="ApiClient"/>, with the batch's request id as <c>X-Request-Id</c>, so every retry of the same queued events
    /// is the same idempotent request. Called on the thread pool by <see cref="AnalyticsSink"/>.
    /// <list type="bullet">
    /// <item>Lines are sent byte for byte as queued, oldest first; a line that names an advertising id prop (08 section
    /// 10.1) is re-serialised without it, for every player.</item>
    /// <item>Lines that are not economy events, or do not parse, are not sent; the batch leaves the queue with the rest
    /// (the Firebase leg, which gets every event, is not built: TODO(batch6-gap-1)). A batch with no economy event sends nothing.</item>
    /// <item>Any 2xx is <see cref="UploadOutcome.Accepted"/>: the batch leaves the queue and the response's per-event
    /// <c>rejected</c> rows are final (a refused event can never succeed on a retry; they are only counted).</item>
    /// <item>Offline, timeout, 5xx, 408/425/429, 409 (<c>IDEMPOTENCY_IN_FLIGHT</c>), 401/403 (signed out, refresh refused,
    /// banned) and 426 (an update will send them) are <see cref="UploadOutcome.Retry"/>, after the ApiClient's own 01
    /// section 5.2 backoff: the queue stays intact and the next flush resends the same batch. A signed-out client (the
    /// credentials hold no token) does not send at all. Any other 4xx (400 <c>VALIDATION</c>) is
    /// <see cref="UploadOutcome.Rejected"/>, so one bad batch cannot block the queue.</item>
    /// <item>Consent and age (08 sections 10.1, 10.2) do not gate this leg: backend economy events are recorded for every
    /// player (legitimate interest), and the server stamps <c>age_gate</c> from the token, not from the envelope.</item>
    /// </list>
    /// </summary>
    public sealed class RemoteAnalyticsUploader : IAnalyticsUploader
    {
        public const string Route = "/analytics/events";

        /// <summary>08 section 3.5's advertising-id props; never uploaded (the server drops them too).</summary>
        public static readonly IReadOnlyCollection<string> AdvertisingIdProps = new HashSet<string>(StringComparer.Ordinal)
        {
            "advertisingId", "adId", "idfa", "idfv", "gaid", "aaid",
        };

        readonly ApiClient _client;
        long _batchesSent, _accepted, _duplicates, _refused, _skipped;

        public RemoteAnalyticsUploader(ApiClient client) { _client = client ?? throw new ArgumentNullException(nameof(client)); }

        /// <summary>Requests that got a 2xx.</summary>
        public long BatchesSent => Interlocked.Read(ref _batchesSent);

        /// <summary>The server's <c>accepted</c> rows, summed.</summary>
        public long EventsAccepted => Interlocked.Read(ref _accepted);

        /// <summary>The server's <c>duplicates</c> rows, summed (an event retried under another request id).</summary>
        public long EventsDuplicate => Interlocked.Read(ref _duplicates);

        /// <summary>The server's per-event <c>rejected</c> rows, summed; final, never resent.</summary>
        public long EventsRefused => Interlocked.Read(ref _refused);

        /// <summary>Queued lines this leg did not send: not an economy event, or not parseable.</summary>
        public long EventsSkipped => Interlocked.Read(ref _skipped);

        /// <summary>The last per-event refusal code the server sent (diagnostics); null before any.</summary>
        public string LastRefusalCode { get; private set; }

        public async Task<UploadOutcome> UploadAsync(AnalyticsBatch batch, CancellationToken ct = default)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            var events = Body(batch.Events, out int skipped);
            if (skipped > 0) Interlocked.Add(ref _skipped, skipped);
            if (events.Count == 0) return UploadOutcome.Accepted;
            var creds = _client.Options.Credentials;
            if (creds != null && creds.AccessToken == null) return UploadOutcome.Retry;   // not signed in yet: keep them queued

            var req = ApiRequest.Mutation(HttpMethod.Post, Route, new JObject { ["events"] = events }, batch.RequestId);
            var r = await _client.SendAsync<JToken>(req, ct).ConfigureAwait(false);
            var outcome = OutcomeOf(r.Status);
            if (outcome == UploadOutcome.Accepted)
            {
                Interlocked.Increment(ref _batchesSent);
                if (r.Ok && r.Body is JObject o) Count(o);
            }
            return outcome;
        }

        /// <summary>How the sink treats a response status (0: no response). See the class summary.</summary>
        public static UploadOutcome OutcomeOf(int status)
        {
            if (status >= 200 && status < 300) return UploadOutcome.Accepted;   // even with an unreadable body: it was stored
            if (status == 0 || status >= 500) return UploadOutcome.Retry;
            switch (status)
            {
                case 401: case 403: case 408: case 409: case 425: case 426: case 429:
                    return UploadOutcome.Retry;
            }
            return UploadOutcome.Rejected;
        }

        void Count(JObject o)
        {
            Interlocked.Add(ref _accepted, IntOf(o["accepted"]));
            Interlocked.Add(ref _duplicates, IntOf(o["duplicates"]));
            if (o["rejected"] is JArray rejected && rejected.Count > 0)
            {
                Interlocked.Add(ref _refused, rejected.Count);
                if (rejected[rejected.Count - 1] is JObject last && last["code"] is JValue c && c.Type == JTokenType.String) LastRefusalCode = (string)c;
            }
        }

        static long IntOf(JToken t) => t is JValue v && v.Type == JTokenType.Integer ? v.Value<long>() : 0;

        /// <summary>The <c>events</c> array: the economy lines, oldest first, as queued (advertising ids removed).</summary>
        public static JArray Body(IReadOnlyList<QueuedEvent> lines, out int skipped)
        {
            var events = new JArray();
            skipped = 0;
            foreach (var e in lines)
            {
                var o = Parse(e.Line);
                if (o == null || !(o[AnalyticsSink.NAME] is JValue n) || n.Type != JTokenType.String || !AnalyticsNames.IsEconomy((string)n))
                {
                    skipped++;
                    continue;
                }
                bool stripped = false;
                if (o[AnalyticsSink.PROPS] is JObject props)
                    foreach (var key in AdvertisingIdProps)
                        stripped |= props.Remove(key);
                events.Add(stripped ? (JToken)o : new JRaw(e.Line));
            }
            return events;
        }

        /// <summary>Parses a queued line without reinterpreting strings as dates (so a re-serialised line keeps its text).</summary>
        static JObject Parse(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            try
            {
                using var reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
                return JToken.ReadFrom(reader) as JObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
