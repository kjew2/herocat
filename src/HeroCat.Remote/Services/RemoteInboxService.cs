using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Economy;
using Newtonsoft.Json.Linq;

namespace HeroCat.Remote.Services
{
    /// <summary>
    /// <see cref="IInboxService"/> over 08 section 3.2 and the <see cref="ApiClient"/> (issue #176): <c>GET /inbox</c>,
    /// <c>POST /inbox/claim</c> and <c>POST /inbox/claim-all</c>, the requestId as X-Request-Id (00 contract 13; the
    /// ApiClient retries a mutation only with it). 08's wire format is followed: items carry <c>inboxItemId</c>, a
    /// lowercase <c>kind</c> (06's <c>type</c>, upper-cased here) and grant lines <c>{kind, id, amount}</c>;
    /// <c>params</c>/<c>bodyArgs</c> pass through untouched, so a SKU id is whatever the server sends (06/09's
    /// <c>dia_3</c>). A 409 INBOX_ALREADY_CLAIMED carrying the original grants (in the error body's <c>details</c>) is
    /// returned as an AlreadyClaimed success.
    /// </summary>
    public sealed class RemoteInboxService : IInboxService
    {
        public const string ListRoute = "/inbox";
        public const string ClaimRoute = "/inbox/claim";
        public const string ClaimAllRoute = "/inbox/claim-all";

        readonly ApiClient _client;
        readonly Dictionary<string, string> _typeById = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly object _lock = new object();

        public RemoteInboxService(ApiClient client) { _client = client ?? throw new ArgumentNullException(nameof(client)); }

        public async Task<IReadOnlyList<InboxItem>> ListAsync(CancellationToken ct = default)
        {
            var r = await _client.SendAsync<JToken>(ApiRequest.Get(ListRoute), ct).ConfigureAwait(false);
            var o = AsObject(r.EnsureOk(), ListRoute, r.Status);
            var list = new List<InboxItem>();
            if (o["items"] is JArray items)
                foreach (var t in items)
                    if (t is JObject row) list.Add(ParseItem(row, r.Status));
            return list;
        }

        public async Task<InboxClaimResult> ClaimAsync(string requestId, string inboxItemId, CancellationToken ct = default)
        {
            var req = ApiRequest.Mutation(HttpMethod.Post, ClaimRoute, new JObject { ["inboxItemId"] = inboxItemId }, requestId);
            var r = await _client.SendAsync<JToken>(req, ct).ConfigureAwait(false);
            if (r.Ok) return new InboxClaimResult(ParseGrant(AsObject(r.Body, ClaimRoute, r.Status), inboxItemId), AlreadyClaimed: false);
            if (r.Error?.Code == InboxRules.INBOX_ALREADY_CLAIMED && r.Error.Details is JObject d)
                return new InboxClaimResult(ParseGrant(d, inboxItemId), AlreadyClaimed: true);
            throw r.ToServiceException();
        }

        public async Task<InboxClaimAllResult> ClaimAllAsync(string requestId, CancellationToken ct = default)
        {
            var req = ApiRequest.Mutation(HttpMethod.Post, ClaimAllRoute, new JObject(), requestId);
            var r = await _client.SendAsync<JToken>(req, ct).ConfigureAwait(false);
            var o = AsObject(r.EnsureOk(), ClaimAllRoute, r.Status);
            var rows = new List<InboxClaimItemResult>();
            if (o["claimed"] is JArray claimed)
                foreach (var t in claimed)
                {
                    if (!(t is JObject row)) continue;
                    string id = (string)row["inboxItemId"];
                    string code = row["code"] is JValue c && c.Type == JTokenType.String ? (string)c : null;
                    rows.Add(code != null || id == null
                        ? new InboxClaimItemResult(id, null, code ?? ErrorClassifier.SRV_UNEXPECTED)
                        : new InboxClaimItemResult(id, ParseGrant(row, id), null));
                }
            return new InboxClaimAllResult(rows);
        }

        InboxItem ParseItem(JObject row, int status)
        {
            string id = (string)row["inboxItemId"] ?? throw Bad(ListRoute, "item without inboxItemId", status);
            string type = ((string)row["kind"] ?? (string)row["type"])?.ToUpperInvariant();
            lock (_lock) _typeById[id] = type;
            long available = row["availableAt"] is JValue a && a.Type == JTokenType.Integer ? (long)a : 0;
            long expires = row["expiresAt"] is JValue e && e.Type == JTokenType.Integer ? (long)e : InboxRules.NeverExpires;
            long created = row["createdAt"] is JValue cr && cr.Type == JTokenType.Integer ? (long)cr : 0;
            Dictionary<string, string> args = null;
            if ((row["params"] ?? row["bodyArgs"]) is JObject p)
            {
                args = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in p)
                    args[kv.Key] = kv.Value?.Type == JTokenType.String ? (string)kv.Value : kv.Value?.ToString(Newtonsoft.Json.Formatting.None);
            }
            var grant = new InboxGrantPayload(id, ParseLines(row["grants"]), InboxRules.ReasonFor(type));
            return new InboxItem(id, grant, expires, available, type, (string)row["titleKey"], (string)row["bodyKey"], args, created);
        }

        /// <summary>A claim response's grant: its lines and its <c>reason</c>, else the reason the listed item's kind implies.</summary>
        InboxGrantPayload ParseGrant(JObject o, string inboxItemId)
        {
            string id = (string)o["inboxItemId"] ?? inboxItemId;
            CurrencyReason reason;
            if (!(o["reason"] is JValue rv && rv.Type == JTokenType.String && Enum.TryParse((string)rv, false, out reason) &&
                  Enum.IsDefined(typeof(CurrencyReason), reason)))
            {
                string type;
                lock (_lock) _typeById.TryGetValue(id ?? "", out type);
                reason = InboxRules.ReasonFor(type);
            }
            return new InboxGrantPayload(id, ParseLines(o["grants"]), reason);
        }

        static GrantLine[] ParseLines(JToken grants)
        {
            var lines = new List<GrantLine>();
            if (grants is JArray arr)
                foreach (var t in arr)
                {
                    if (!(t is JObject g)) continue;
                    // 08's {kind, id, amount}; 06's example {currency, amount} reads as a currency line.
                    string kind = (string)g["kind"] ?? (g["currency"] != null ? Sim.Simulation.GrantKindCurrency : null);
                    string id = (string)g["id"] ?? (string)g["currency"];
                    long amount = g["amount"] is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? (long)v : 0;
                    lines.Add(new GrantLine(kind, id, amount));
                }
            return lines.ToArray();
        }

        static JObject AsObject(JToken body, string route, int status) =>
            body as JObject ?? throw Bad(route, "expected a JSON object", status);

        static ServiceException Bad(string route, string message, int status) =>
            new ServiceException(ErrorClass.ServerError, ErrorClassifier.SRV_UNEXPECTED, route + ": " + message, status);
    }
}
