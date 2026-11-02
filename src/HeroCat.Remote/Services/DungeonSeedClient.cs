using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;
using Newtonsoft.Json.Linq;

namespace HeroCat.Remote.Services
{
    /// <summary>
    /// <see cref="IDungeonSeedService"/> over 08 section 3.3 (issue #146): <c>POST /dungeon/seed {kind, floor,
    /// clientContentVersion}</c> with the flow's requestId as X-Request-Id (00 contract 13), so a timed-out request retried
    /// with the same id gets the server's stored grant and seed back. The answer <c>{grantId, seed (16 hex), expiresAt,
    /// serverTime}</c> becomes a <see cref="DungeonSeedGrant"/> with LocalSeed false. Failures throw ServiceException:
    /// NET_OFFLINE / NET_TIMEOUT (retryable), DUNGEON_SEED_CAP, DUNGEON_LOCKED, CONTENT_TOO_OLD and the rest as the ApiClient
    /// classifies them; an unreadable answer is SRV_UNEXPECTED.
    /// </summary>
    public sealed class DungeonSeedClient : IDungeonSeedService
    {
        public const string Route = "/dungeon/seed";

        readonly ApiClient _client;
        readonly Func<string> _contentVersion;

        /// <param name="contentVersion">The running bundle's content_version (the body's clientContentVersion).</param>
        public DungeonSeedClient(ApiClient client, Func<string> contentVersion)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _contentVersion = contentVersion ?? (() => "");
        }

        public async Task<DungeonSeedGrant> RequestSeedAsync(DungeonSeedRequest request, CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var body = new JObject
            {
                ["kind"] = request.Kind.ToString(), ["floor"] = request.Floor, ["clientContentVersion"] = _contentVersion() ?? "",
            };
            var r = await _client.SendAsync<JToken>(ApiRequest.Mutation(HttpMethod.Post, Route, body, request.RequestId), ct).ConfigureAwait(false);
            if (!(r.EnsureOk() is JObject o)) throw Bad("expected a JSON object", r.Status);
            string grantId = o["grantId"] is JValue g && g.Type == JTokenType.String ? (string)g : null;
            string seedHex = o["seed"] is JValue s && s.Type == JTokenType.String ? (string)s : null;
            if (string.IsNullOrEmpty(grantId)) throw Bad("no grantId", r.Status);
            if (seedHex == null || seedHex.Length != 16 ||
                !ulong.TryParse(seedHex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong seed))
                throw Bad("seed is not 16 hex digits", r.Status);
            long expires = o["expiresAt"] is JValue e && e.Type == JTokenType.Integer ? (long)e : 0;
            return new DungeonSeedGrant(seed, grantId, expires, LocalSeed: false);
        }

        static ServiceException Bad(string message, int status) =>
            new ServiceException(ErrorClass.ServerError, ErrorClassifier.SRV_UNEXPECTED, Route + ": " + message, status);
    }
}
