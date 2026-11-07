using System.Data.Common;
using System.Text.Json;
using HeroCat.Server.Infrastructure.Db;

namespace HeroCat.Server.Features.Save;

/// <summary>A <c>saves</c> row: the player's save of record (specs/02 section 12).</summary>
public sealed record StoredSave(
    Guid PlayerId, long SaveSeq, Guid DeviceId, int SchemaVersion, string ContentVersion, string ClientVersion,
    long LastSavedServerTime, bool ClockAuthoritative, int SizeBytes, byte[] Sha256, string Hmac, bool HmacValid, string BlobKey,
    string Etag, double ProgressScore, string SummaryJson, string[] Flags, bool RollbackPending, bool CmdlogRequested, DateTimeOffset UploadedAt)
{
    public SaveMetaDto ToMeta()
    {
        using var doc = JsonDocument.Parse(SummaryJson);
        return new SaveMetaDto(PlayerId, DeviceId, SaveSeq, LastSavedServerTime, SchemaVersion, ContentVersion, ClientVersion, SizeBytes,
                               Convert.ToHexString(Sha256).ToLowerInvariant(), Hmac, ClockAuthoritative, ProgressScore, doc.RootElement.Clone(), Etag);
    }
}

/// <summary>
/// The Save service's tables (specs/02 section 12: <c>saves</c>, <c>save_history</c>) in PostgreSQL. An upload is one
/// transaction under a per-player advisory lock (<see cref="LockPlayerAsync"/>), so concurrent uploads from two devices
/// are serialised and the <c>If-Match</c> and <c>saveSeq</c> checks see the row they replace.
/// </summary>
public sealed class SaveStore
{
    const string Columns =
        "player_id, save_seq, device_id, schema_version, content_version, client_version, last_saved_server_time, clock_authoritative, " +
        "size_bytes, sha256, hmac, hmac_valid, blob_key, etag, progress_score, summary::text, flags, rollback_pending, cmdlog_requested, uploaded_at";

    readonly IDbConnectionFactory _db;

    public SaveStore(IDbConnectionFactory db) => _db = db;

    public ValueTask<DbConnection> OpenAsync(CancellationToken ct) => _db.OpenAsync(ct);

    public async Task<StoredSave?> GetAsync(Guid playerId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await GetAsync(c, null, playerId, ct).ConfigureAwait(false);
    }

    public static async Task<StoredSave?> GetAsync(DbConnection c, DbTransaction? tx, Guid playerId, CancellationToken ct)
    {
        await using var cmd = c.Command("save.get", $"select {Columns} from saves where player_id = @p", tx, ("p", playerId));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new StoredSave(r.GetGuid(0), r.GetInt64(1), r.GetGuid(2), r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetInt64(6),
                              r.GetBoolean(7), r.GetInt32(8), r.GetFieldValue<byte[]>(9), r.GetString(10), r.GetBoolean(11), r.GetString(12),
                              r.GetString(13), r.GetDouble(14), r.GetString(15), r.GetFieldValue<string[]>(16), r.GetBoolean(17), r.GetBoolean(18),
                              r.GetFieldValue<DateTimeOffset>(19));
    }

    /// <summary>Serialises this player's uploads until the transaction ends.</summary>
    public static async Task LockPlayerAsync(DbConnection c, DbTransaction tx, Guid playerId, CancellationToken ct)
    {
        await using var cmd = c.Command("save.lock_player", "select pg_advisory_xact_lock(hashtext('herocat/save'), hashtext(@p))", tx, ("p", playerId.ToString("D")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The device's HMAC key at rest (<c>devices.hmac_key_enc</c>), if the device is this player's and logout has not
    /// released it (<c>devices.released_at</c>, 00 decision 117): a logged-out player's access token, still valid until it
    /// expires, no longer verifies uploads under the released device's key (issue #322).
    /// </summary>
    public static async Task<byte[]?> DeviceKeyEncAsync(DbConnection c, DbTransaction tx, Guid playerId, Guid deviceId, CancellationToken ct)
    {
        await using var cmd = c.Command("save.device_key", "select hmac_key_enc from devices where device_id = @d and player_id = @p and released_at is null", tx,
            ("d", deviceId), ("p", playerId));
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as byte[];
    }

    /// <summary>The highest <c>saveSeq</c> this device uploaded that the server still knows (the save of record and history).</summary>
    public static async Task<long?> LastSeqOfDeviceAsync(DbConnection c, DbTransaction tx, Guid playerId, Guid deviceId, CancellationToken ct)
    {
        await using var cmd = c.Command("save.last_seq_of_device", 
            "select max(s) from (select save_seq s from saves where player_id = @p and device_id = @d " +
            "union all select save_seq from save_history where player_id = @p and device_id = @d) x", tx, ("p", playerId), ("d", deviceId));
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is long l ? l : null;
    }

    /// <summary>
    /// Step 7's rows: upserts the save of record (clearing <c>rollback_pending</c>) and inserts its history entry, whose
    /// insert trigger trims the player's history to <c>SAVE_HISTORY_KEEP</c> (migrations 0002, 0007). Two devices can
    /// reach the same <c>saveSeq</c> (a <c>SEQ_REGRESSION</c> upload); each keeps its own history entry and blob, keyed by
    /// the device (issue #295). One device never repeats a <c>saveSeq</c> (step 4's <c>SAVE_STALE</c>).
    /// </summary>
    public static async Task WriteAsync(DbConnection c, DbTransaction tx, StoredSave s, CancellationToken ct)
    {
        await using (var up = c.Command("save.upsert", 
            "insert into saves (player_id, save_seq, device_id, schema_version, content_version, client_version, last_saved_server_time, " +
            "clock_authoritative, size_bytes, sha256, hmac, hmac_valid, blob_key, etag, progress_score, summary, flags, rollback_pending, " +
            "cmdlog_requested, uploaded_at) values (@p, @seq, @d, @sv, @cv, @clv, @t, @ca, @size, @sha, @hmac, @hv, @key, @etag, @score, " +
            "cast(@summary as jsonb), @flags, false, @cmdlog, @now) " +
            "on conflict (player_id) do update set save_seq = excluded.save_seq, device_id = excluded.device_id, " +
            "schema_version = excluded.schema_version, content_version = excluded.content_version, client_version = excluded.client_version, " +
            "last_saved_server_time = excluded.last_saved_server_time, clock_authoritative = excluded.clock_authoritative, " +
            "size_bytes = excluded.size_bytes, sha256 = excluded.sha256, hmac = excluded.hmac, hmac_valid = excluded.hmac_valid, " +
            "blob_key = excluded.blob_key, etag = excluded.etag, progress_score = excluded.progress_score, summary = excluded.summary, " +
            "flags = excluded.flags, rollback_pending = false, cmdlog_requested = excluded.cmdlog_requested, uploaded_at = excluded.uploaded_at", tx,
            ("p", s.PlayerId), ("seq", s.SaveSeq), ("d", s.DeviceId), ("sv", s.SchemaVersion), ("cv", s.ContentVersion), ("clv", s.ClientVersion),
            ("t", s.LastSavedServerTime), ("ca", s.ClockAuthoritative), ("size", s.SizeBytes), ("sha", s.Sha256), ("hmac", s.Hmac),
            ("hv", s.HmacValid), ("key", s.BlobKey), ("etag", s.Etag), ("score", s.ProgressScore), ("summary", s.SummaryJson),
            ("flags", s.Flags), ("cmdlog", s.CmdlogRequested), ("now", s.UploadedAt)))
            await up.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var hist = c.Command("save.insert_history", 
            "insert into save_history (player_id, save_seq, device_id, blob_key, sha256, progress_score, summary, flags, uploaded_at) " +
            "values (@p, @seq, @d, @key, @sha, @score, cast(@summary as jsonb), @flags, @now) " +
            "on conflict (player_id, save_seq, device_id) do update set blob_key = excluded.blob_key, " +
            "sha256 = excluded.sha256, progress_score = excluded.progress_score, summary = excluded.summary, flags = excluded.flags, " +
            "uploaded_at = excluded.uploaded_at", tx,
            ("p", s.PlayerId), ("seq", s.SaveSeq), ("d", s.DeviceId), ("key", s.BlobKey), ("sha", s.Sha256), ("score", s.ProgressScore),
            ("summary", s.SummaryJson), ("flags", s.Flags), ("now", s.UploadedAt));
        await hist.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A download ends a pending rollback (specs/02 section 12 <c>saves.rollback_pending</c>).</summary>
    public async Task ClearRollbackAsync(Guid playerId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("save.clear_rollback", "update saves set rollback_pending = false where player_id = @p and rollback_pending", null, ("p", playerId));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The newest <paramref name="limit"/> history entries, newest first.</summary>
    public async Task<List<SaveHistoryEntryDto>> HistoryAsync(Guid playerId, int limit, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("save.history", 
            "select save_seq, device_id, uploaded_at, summary::text from save_history where player_id = @p " +
            "order by uploaded_at desc, save_seq desc, device_id desc limit @n", null, ("p", playerId), ("n", limit));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<SaveHistoryEntryDto>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(r.GetString(3));
            list.Add(new SaveHistoryEntryDto(r.GetInt64(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2).ToUnixTimeSeconds(), doc.RootElement.Clone()));
        }
        return list;
    }
}
