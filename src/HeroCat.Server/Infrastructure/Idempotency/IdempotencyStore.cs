using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HeroCat.Server.Infrastructure.Db;

namespace HeroCat.Server.Infrastructure.Idempotency;

/// <summary>
/// A response as the idempotency store keeps it (specs/08 section 1.1): status, the headers a replay must repeat and the
/// exact body bytes, so a replay is byte-identical. <see cref="Protected"/> marks a body stored encrypted under the
/// data key (a response that carries tokens, <c>/auth/guest</c>); the middleware encrypts and decrypts it.
/// </summary>
public sealed record StoredResponse(int StatusCode, string? ContentType, IReadOnlyDictionary<string, string> Headers, byte[] Body,
                                    bool Protected = false);

public enum IdempotencyOutcome
{
    /// <summary>No live row existed; an <c>in_flight</c> row now holds the id and the caller runs the handler.</summary>
    Started,
    /// <summary>A final row exists for the same request: replay <see cref="IdempotencyBegin.Response"/>.</summary>
    Replay,
    /// <summary>An <c>in_flight</c> row exists: <c>409 IDEMPOTENCY_IN_FLIGHT</c>.</summary>
    InFlight,
    /// <summary>The id is already used under another key (player): <c>403</c> and <c>DUPLICATE_REQUEST_ID</c>.</summary>
    OtherPlayer,
    /// <summary>The id is used by this player for a different request (method, route or body): <c>400 VALIDATION</c>.</summary>
    Mismatch,
}

public sealed record IdempotencyBegin(IdempotencyOutcome Outcome, StoredResponse? Response = null)
{
    public static readonly IdempotencyBegin Started = new(IdempotencyOutcome.Started);
    public static readonly IdempotencyBegin InFlight = new(IdempotencyOutcome.InFlight);
    public static readonly IdempotencyBegin OtherPlayer = new(IdempotencyOutcome.OtherPlayer);
    public static readonly IdempotencyBegin Mismatch = new(IdempotencyOutcome.Mismatch);
}

/// <summary>
/// The <c>idempotency</c> table of specs/02 section 12 (00 section 8.13, issue #194). A row is keyed by
/// <c>(player_id, request_id)</c>; <c>status_code = 0</c> marks it <c>in_flight</c>, any other value is the final status
/// of the stored response. A row older than <c>IDEMPOTENCY_TTL_H</c> counts as absent even before the purge removes it,
/// so a pending request older than that executes fresh (00 decision 28).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// <c>INSERT ... ON CONFLICT DO NOTHING</c> an <c>in_flight</c> row for <paramref name="key"/> and
    /// <paramref name="requestId"/>, or report what already holds the id.
    /// </summary>
    Task<IdempotencyBegin> BeginAsync(Guid key, string requestId, string route, string requestHash, DateTimeOffset now, CancellationToken ct);

    /// <summary>Turns the <c>in_flight</c> row final with the response to replay.</summary>
    Task CompleteAsync(Guid key, string requestId, StoredResponse response, CancellationToken ct);

    /// <summary>
    /// The same inside the handler's own transaction (specs/08 section 4.3: the final idempotency row commits with the
    /// ledger rows, so a crash between the two cannot leave the request <c>in_flight</c> for a day). Postgres only.
    /// </summary>
    Task CompleteAsync(DbConnection connection, DbTransaction transaction, Guid key, string requestId, StoredResponse response, CancellationToken ct);

    /// <summary>Deletes the <c>in_flight</c> row (a 5xx or a transient refusal): a retry with the same id runs fresh.</summary>
    Task AbandonAsync(Guid key, string requestId, CancellationToken ct);

    /// <summary>Deletes rows created before <paramref name="cutoff"/> (the hourly purge, specs/02 section 13.1). Returns how many.</summary>
    Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken ct);
}

/// <summary>The <c>response</c> jsonb of an idempotency row (specs/02 section 12, as built).</summary>
internal sealed class StoredRow
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("requestSha256")] public string RequestHash { get; set; } = "";
    [JsonPropertyName("contentType")] public string? ContentType { get; set; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; set; }
    /// <summary>Base64, so the bytes survive jsonb exactly (jsonb would re-order keys and drop whitespace of a JSON value).</summary>
    [JsonPropertyName("bodyBase64")] public string? BodyBase64 { get; set; }
    /// <summary>True when <see cref="BodyBase64"/> is the body encrypted under the data key.</summary>
    [JsonPropertyName("protected"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Protected { get; set; }

    public static string InFlight(string requestHash) => JsonSerializer.Serialize(new StoredRow { RequestHash = requestHash });

    public static string Final(string requestHash, StoredResponse r) => JsonSerializer.Serialize(new StoredRow
    {
        RequestHash = requestHash,
        ContentType = r.ContentType,
        Headers = new Dictionary<string, string>(r.Headers),
        BodyBase64 = Convert.ToBase64String(r.Body),
        Protected = r.Protected,
    });

    public StoredResponse ToResponse(int status) => new(status, ContentType,
        Headers ?? new Dictionary<string, string>(), BodyBase64 == null ? Array.Empty<byte>() : Convert.FromBase64String(BodyBase64), Protected);
}

public static class IdempotencyKeys
{
    /// <summary>
    /// The row key of an idempotent route that has no player yet (<c>/auth/guest</c>): a UUID derived from the device id,
    /// the first 16 bytes of <c>SHA-256("herocat/idempotency/device/" + lowercase deviceId)</c> with the RFC 9562
    /// version 8 and variant bits set (specs/02 section 12, issue #194). It cannot collide with a server-made random
    /// (version 4) player id.
    /// </summary>
    public static Guid ForDevice(string deviceId)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("herocat/idempotency/device/" + deviceId.Trim().ToLowerInvariant()));
        h[6] = (byte)((h[6] & 0x0F) | 0x80);
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>The request fingerprint a reused id is compared on: SHA-256 over method, route and body, lowercase hex.</summary>
    public static string RequestHash(string method, string path, ReadOnlySpan<byte> body)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(method.ToUpperInvariant() + " " + path.TrimEnd('/').ToLowerInvariant() + "\n"));
        sha.AppendData(body);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>The table in PostgreSQL.</summary>
public sealed class PostgresIdempotencyStore : IIdempotencyStore
{
    readonly IDbConnectionFactory _db;
    readonly TimeSpan _ttl;

    public PostgresIdempotencyStore(IDbConnectionFactory db, TimeSpan ttl)
    {
        _db = db;
        _ttl = ttl;
    }

    public async Task<IdempotencyBegin> BeginAsync(Guid key, string requestId, string route, string requestHash, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var liveSince = now - _ttl;

        await using (var other = c.Command("idempotency.other_player", 
            "select exists(select 1 from idempotency where request_id = @r and player_id <> @p and created_at > @since)", null,
            ("r", requestId), ("p", key), ("since", liveSince)))
        {
            if ((bool)(await other.ExecuteScalarAsync(ct).ConfigureAwait(false))!) return IdempotencyBegin.OtherPlayer;
        }

        // An expired row the purge has not reached yet no longer counts (00 decision 28).
        await using (var expired = c.Command("idempotency.delete_expired", "delete from idempotency where player_id = @p and request_id = @r and created_at <= @since", null,
            ("p", key), ("r", requestId), ("since", liveSince)))
            await expired.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using (var insert = c.Command("idempotency.insert", 
            "insert into idempotency (player_id, request_id, route, status_code, response, created_at) " +
            "values (@p, @r, @route, 0, cast(@response as jsonb), @now) on conflict do nothing", null,
            ("p", key), ("r", requestId), ("route", route), ("response", StoredRow.InFlight(requestHash)), ("now", now)))
        {
            if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1) return IdempotencyBegin.Started;
        }

        await using var read = c.Command("idempotency.read", "select status_code, response::text from idempotency where player_id = @p and request_id = @r", null,
            ("p", key), ("r", requestId));
        await using var rd = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await rd.ReadAsync(ct).ConfigureAwait(false)) return IdempotencyBegin.InFlight; // deleted between the two statements; the client retries
        int status = rd.GetInt16(0);
        var row = JsonSerializer.Deserialize<StoredRow>(rd.GetString(1))!;
        if (row.RequestHash != requestHash) return IdempotencyBegin.Mismatch;
        return status == 0 ? IdempotencyBegin.InFlight : new IdempotencyBegin(IdempotencyOutcome.Replay, row.ToResponse(status));
    }

    async Task CompleteOn(DbConnection c, DbTransaction? tx, Guid key, string requestId, StoredResponse response, CancellationToken ct)
    {
        // The row keeps the fingerprint its in_flight insert recorded.
        await using var cmd = c.Command("idempotency.complete", 
            "update idempotency set status_code = @s, " +
            "response = cast(@response as jsonb) || jsonb_build_object('requestSha256', response->'requestSha256') " +
            "where player_id = @p and request_id = @r and status_code = 0", tx,
            ("s", (short)response.StatusCode), ("response", StoredRow.Final("", response)), ("p", key), ("r", requestId));
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException($"no in_flight idempotency row for request {requestId}");
    }

    public async Task CompleteAsync(Guid key, string requestId, StoredResponse response, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await CompleteOn(c, null, key, requestId, response, ct).ConfigureAwait(false);
    }

    public Task CompleteAsync(DbConnection connection, DbTransaction transaction, Guid key, string requestId, StoredResponse response, CancellationToken ct) =>
        CompleteOn(connection, transaction, key, requestId, response, ct);

    public async Task AbandonAsync(Guid key, string requestId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("idempotency.abandon", "delete from idempotency where player_id = @p and request_id = @r and status_code = 0", null,
            ("p", key), ("r", requestId));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("idempotency.purge", "delete from idempotency where created_at < @cutoff", null, ("cutoff", cutoff));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Per-process rows: tests, and a local run without Postgres (as the blob store and rate limits do).</summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    sealed record Row(string Route, int Status, string RequestHash, StoredResponse? Response, DateTimeOffset CreatedAt);

    readonly Dictionary<(Guid, string), Row> _rows = new();
    readonly TimeSpan _ttl;

    public InMemoryIdempotencyStore(TimeSpan ttl) => _ttl = ttl;

    public int Count { get { lock (_rows) return _rows.Count; } }

    public Task<IdempotencyBegin> BeginAsync(Guid key, string requestId, string route, string requestHash, DateTimeOffset now, CancellationToken ct)
    {
        lock (_rows)
        {
            var liveSince = now - _ttl;
            if (_rows.Any(kv => kv.Key.Item2 == requestId && kv.Key.Item1 != key && kv.Value.CreatedAt > liveSince))
                return Task.FromResult(IdempotencyBegin.OtherPlayer);
            if (_rows.TryGetValue((key, requestId), out var row) && row.CreatedAt > liveSince)
            {
                if (row.RequestHash != requestHash) return Task.FromResult(IdempotencyBegin.Mismatch);
                return Task.FromResult(row.Status == 0 ? IdempotencyBegin.InFlight : new IdempotencyBegin(IdempotencyOutcome.Replay, row.Response));
            }
            _rows[(key, requestId)] = new Row(route, 0, requestHash, null, now);
            return Task.FromResult(IdempotencyBegin.Started);
        }
    }

    public Task CompleteAsync(Guid key, string requestId, StoredResponse response, CancellationToken ct)
    {
        lock (_rows)
        {
            if (!_rows.TryGetValue((key, requestId), out var row) || row.Status != 0)
                throw new InvalidOperationException($"no in_flight idempotency row for request {requestId}");
            // Round-trip through the row encoding, as Postgres does.
            var stored = JsonSerializer.Deserialize<StoredRow>(StoredRow.Final(row.RequestHash, response))!.ToResponse(response.StatusCode);
            _rows[(key, requestId)] = row with { Status = response.StatusCode, Response = stored };
        }
        return Task.CompletedTask;
    }

    public Task CompleteAsync(DbConnection connection, DbTransaction transaction, Guid key, string requestId, StoredResponse response, CancellationToken ct) =>
        CompleteAsync(key, requestId, response, ct);

    public Task AbandonAsync(Guid key, string requestId, CancellationToken ct)
    {
        lock (_rows)
        {
            if (_rows.TryGetValue((key, requestId), out var row) && row.Status == 0) _rows.Remove((key, requestId));
        }
        return Task.CompletedTask;
    }

    public Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        lock (_rows)
        {
            var old = _rows.Where(kv => kv.Value.CreatedAt < cutoff).Select(kv => kv.Key).ToList();
            foreach (var k in old) _rows.Remove(k);
            return Task.FromResult(old.Count);
        }
    }
}
