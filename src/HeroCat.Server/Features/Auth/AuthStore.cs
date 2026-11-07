using System.Data.Common;
using HeroCat.Server.Infrastructure.Db;
using Npgsql;

namespace HeroCat.Server.Features.Auth;

/// <summary>An <c>accounts</c> row with its links, as the profile needs it.</summary>
public sealed record AccountRow(Guid PlayerId, string DisplayName, string AvatarId, string Region, int AgeGate, DateTimeOffset CreatedAt,
                                DateTimeOffset? LastLoginAt, string Status, IReadOnlyList<LinkedPlatformDto> Links);

public sealed record NewDevice(Guid DeviceId, byte[] HmacKeyEnc, string? Model, string? Os, string? ClientVersion);

public enum GuestOutcome { Created, AlreadyBound }

public sealed record GuestResult(GuestOutcome Outcome, Guid PlayerId, AccountRow? Account);

public enum RefreshOutcome { Rotated, Unknown, Expired, Revoked, Reused, DeviceMismatch }

public sealed record RefreshResult(RefreshOutcome Outcome, AccountRow? Account = null, Guid DeviceId = default);

public enum DeviceOutcome { Registered, Updated, Rotated, BoundElsewhere, RotationRefused }

/// <summary>The stored device a <c>/devices</c> call compares with.</summary>
public sealed record StoredDevice(Guid PlayerId, byte[] HmacKeyEnc, DateTimeOffset FirstSeenAt);

/// <summary>
/// The Auth service's tables (specs/02 section 12: <c>accounts</c>, <c>account_links</c>, <c>devices</c>,
/// <c>refresh_tokens</c>) in PostgreSQL. Each operation is one transaction.
/// </summary>
public sealed class AuthStore
{
    readonly IDbConnectionFactory _db;

    public AuthStore(IDbConnectionFactory db) => _db = db;

    /// <summary>
    /// Creates the account, its device and the first refresh token of a new family, or reports the player the device is
    /// already bound to (<c>DEVICE_ALREADY_BOUND</c>), also when a concurrent call bound it first. A device that logout
    /// released (<c>devices.released_at</c>, 00 decision 117) is rebound to the new guest with its new key.
    /// </summary>
    public async Task<GuestResult> CreateGuestAsync(Guid playerId, string displayName, string region, NewDevice device, byte[] refreshHash,
                                                    DateTimeOffset now, DateTimeOffset refreshExpiresAt, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var bound = await BoundPlayerAsync(c, null, device.DeviceId, ct).ConfigureAwait(false);
        if (bound is Guid existing) return new GuestResult(GuestOutcome.AlreadyBound, existing, null);

        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var acc = c.Command("auth.insert_account", 
            "insert into accounts (player_id, display_name, region, created_at, last_login_at) values (@p, @n, @region, @now, @now)", tx,
            ("p", playerId), ("n", displayName), ("region", region), ("now", now)))
            await acc.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // A released row keeps its device_id (saves and refresh_tokens reference it) and takes the new player and key.
        await using (var dev = c.Command("auth.insert_device", 
            "insert into devices (device_id, player_id, hmac_key_enc, model, os, client_version, first_seen_at, last_seen_at) " +
            "values (@d, @p, @k, @model, @os, @cv, @now, @now) on conflict (device_id) do update set " +
            "player_id = excluded.player_id, hmac_key_enc = excluded.hmac_key_enc, model = excluded.model, os = excluded.os, " +
            "client_version = excluded.client_version, push_token = null, first_seen_at = excluded.first_seen_at, " +
            "last_seen_at = excluded.last_seen_at, released_at = null " +
            "where devices.released_at is not null", tx,
            ("d", device.DeviceId), ("p", playerId), ("k", device.HmacKeyEnc), ("model", device.Model), ("os", device.Os),
            ("cv", device.ClientVersion), ("now", now)))
        {
            if (await dev.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                var winner = await BoundPlayerAsync(c, null, device.DeviceId, ct).ConfigureAwait(false);
                return new GuestResult(GuestOutcome.AlreadyBound, winner ?? Guid.Empty, null);
            }
        }

        await InsertRefreshAsync(c, tx, refreshHash, playerId, device.DeviceId, Guid.NewGuid(), now, refreshExpiresAt, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new GuestResult(GuestOutcome.Created, playerId, await ReadAccountAsync(c, null, playerId, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Rotates a refresh token (specs/08 section 2.1): the presented token is revoked and replaced by
    /// <paramref name="newHash"/> in the same family. A rotated-out token revokes the whole family (<c>TOKEN_REUSED</c>);
    /// a token revoked by logout (no successor) is <c>TOKEN_REVOKED</c>, as is one of a deleted or merged-away account.
    /// A same-device retry of a token rotated out at most <paramref name="reuseGrace"/> ago (<c>REFRESH_REUSE_GRACE_S</c>,
    /// a lost refresh response, issue #273) rotates the family's live token instead, so the old one never becomes valid again.
    /// </summary>
    public async Task<RefreshResult> RotateRefreshAsync(byte[] hash, Guid deviceId, byte[] newHash, DateTimeOffset now, DateTimeOffset newExpiresAt,
                                                        TimeSpan reuseGrace, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);

        Guid player, device, family;
        bool rotatedOut, revoked;
        DateTimeOffset expiresAt;
        DateTimeOffset? revokedAt;
        await using (var sel = c.Command("auth.lock_refresh", 
            "select player_id, device_id, family_id, replaced_by is not null, revoked_at is not null, expires_at, revoked_at " +
            "from refresh_tokens where token_hash = @h for update", tx, ("h", hash)))
        await using (var rd = await sel.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await rd.ReadAsync(ct).ConfigureAwait(false)) return new RefreshResult(RefreshOutcome.Unknown);
            player = rd.GetGuid(0);
            device = rd.GetGuid(1);
            family = rd.GetGuid(2);
            rotatedOut = rd.GetBoolean(3);
            revoked = rd.GetBoolean(4);
            expiresAt = rd.GetFieldValue<DateTimeOffset>(5);
            revokedAt = rd.IsDBNull(6) ? null : rd.GetFieldValue<DateTimeOffset>(6);
        }

        if (device != deviceId) return new RefreshResult(RefreshOutcome.DeviceMismatch);
        if (revoked && rotatedOut && revokedAt is DateTimeOffset at && now - at <= reuseGrace)
        {
            // A retry after a lost response: rotate the family's live token (at most one), if there still is one.
            await using var head = c.Command("auth.lock_family_head", 
                "select token_hash, expires_at from refresh_tokens where family_id = @f and revoked_at is null for update", tx, ("f", family));
            await using var hr = await head.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await hr.ReadAsync(ct).ConfigureAwait(false))
            {
                hash = hr.GetFieldValue<byte[]>(0);
                expiresAt = hr.GetFieldValue<DateTimeOffset>(1);
                revoked = rotatedOut = false;
            }
        }
        if (revoked && rotatedOut)
        {
            await RevokeFamilyAsync(c, tx, family, now, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new RefreshResult(RefreshOutcome.Reused, DeviceId: device);
        }
        if (revoked) return new RefreshResult(RefreshOutcome.Revoked);
        if (expiresAt <= now) return new RefreshResult(RefreshOutcome.Expired);

        var account = await ReadAccountAsync(c, tx, player, ct).ConfigureAwait(false);
        if (account == null || account.Status is "deleted" or "merged_away")
        {
            await RevokeFamilyAsync(c, tx, family, now, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new RefreshResult(RefreshOutcome.Revoked);
        }

        await InsertRefreshAsync(c, tx, newHash, player, device, family, now, newExpiresAt, ct).ConfigureAwait(false);
        await using (var old = c.Command("auth.replace_refresh", "update refresh_tokens set revoked_at = @now, replaced_by = @n where token_hash = @h", tx,
            ("now", now), ("n", newHash), ("h", hash)))
            await old.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using (var seen = c.Command("auth.touch_last_seen", 
            "update accounts set last_login_at = @now where player_id = @p; update devices set last_seen_at = @now where device_id = @d", tx,
            ("now", now), ("p", player), ("d", device)))
            await seen.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new RefreshResult(RefreshOutcome.Rotated, account with { LastLoginAt = now }, device);
    }

    /// <summary>
    /// Logout (specs/08 section 2.2, 00 decision 117): revokes the family of <paramref name="hash"/> if it belongs to
    /// <paramref name="playerId"/>, then releases that token's device if it is still bound to the player, in one
    /// transaction, so the device's next <c>/auth/guest</c> makes a new guest. False when the token is not the player's
    /// (nothing changes).
    /// </summary>
    public async Task<bool> RevokeFamilyOfAsync(Guid playerId, byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        Guid device;
        await using (var sel = c.Command("auth.refresh_device", "select device_id from refresh_tokens where token_hash = @h and player_id = @p", tx,
            ("h", hash), ("p", playerId)))
        {
            if (await sel.ExecuteScalarAsync(ct).ConfigureAwait(false) is not Guid d) return false;
            device = d;
        }
        await using (var cmd = c.Command("auth.revoke_family_of", 
            "update refresh_tokens set revoked_at = @now where revoked_at is null and family_id = " +
            "(select family_id from refresh_tokens where token_hash = @h and player_id = @p)", tx,
            ("now", now), ("h", hash), ("p", playerId)))
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using (var rel = c.Command("auth.release_device", 
            "update devices set released_at = @now where device_id = @d and player_id = @p and released_at is null", tx,
            ("now", now), ("d", device), ("p", playerId)))
            await rel.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<StoredDevice?> GetDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("auth.get_device", "select player_id, hmac_key_enc, first_seen_at from devices where device_id = @d", null, ("d", deviceId));
        await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await rd.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new StoredDevice(rd.GetGuid(0), rd.GetFieldValue<byte[]>(1), rd.GetFieldValue<DateTimeOffset>(2));
    }

    /// <summary>True when the player's save of record was uploaded by this device with a valid HMAC (the <c>/devices</c> rotation rule).</summary>
    public async Task<bool> SaveVerifiedFromAsync(Guid playerId, Guid deviceId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("auth.save_verified_from", "select exists(select 1 from saves where player_id = @p and device_id = @d and hmac_valid)", null,
            ("p", playerId), ("d", deviceId));
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Inserts the device for <paramref name="playerId"/>, or updates its metadata and (when <paramref name="newKeyEnc"/>
    /// is set) its key. False when the device belongs to another player (a concurrent insert included).
    /// </summary>
    public async Task<bool> UpsertDeviceAsync(Guid playerId, NewDevice device, byte[]? newKeyEnc, string? pushToken, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("auth.upsert_device", 
            "insert into devices (device_id, player_id, hmac_key_enc, model, os, client_version, push_token, first_seen_at, last_seen_at) " +
            "values (@d, @p, @k, @model, @os, @cv, @push, @now, @now) " +
            "on conflict (device_id) do update set " +
            "hmac_key_enc = coalesce(@newk, devices.hmac_key_enc), model = coalesce(excluded.model, devices.model), " +
            "os = coalesce(excluded.os, devices.os), client_version = coalesce(excluded.client_version, devices.client_version), " +
            "push_token = coalesce(excluded.push_token, devices.push_token), last_seen_at = excluded.last_seen_at " +
            "where devices.player_id = excluded.player_id", null,
            ("d", device.DeviceId), ("p", playerId), ("k", device.HmacKeyEnc), ("newk", newKeyEnc), ("model", device.Model), ("os", device.Os),
            ("cv", device.ClientVersion), ("push", pushToken), ("now", now));
        if (newKeyEnc == null) ((NpgsqlParameter)cmd.Parameters["newk"]).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea;
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<AccountRow?> GetAccountAsync(Guid playerId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await ReadAccountAsync(c, null, playerId, ct).ConfigureAwait(false);
    }

    static async Task<Guid?> BoundPlayerAsync(DbConnection c, DbTransaction? tx, Guid deviceId, CancellationToken ct)
    {
        await using var cmd = c.Command("auth.bound_player", "select player_id from devices where device_id = @d and released_at is null", tx, ("d", deviceId));
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid g ? g : null;
    }

    static async Task InsertRefreshAsync(DbConnection c, DbTransaction tx, byte[] hash, Guid player, Guid device, Guid family,
                                         DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var cmd = c.Command("auth.insert_refresh", 
            "insert into refresh_tokens (token_hash, player_id, device_id, family_id, issued_at, expires_at) values (@h, @p, @d, @f, @now, @exp)", tx,
            ("h", hash), ("p", player), ("d", device), ("f", family), ("now", now), ("exp", expiresAt));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    static async Task RevokeFamilyAsync(DbConnection c, DbTransaction tx, Guid family, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = c.Command("auth.revoke_family", "update refresh_tokens set revoked_at = @now where family_id = @f and revoked_at is null", tx,
            ("now", now), ("f", family));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    static async Task<AccountRow?> ReadAccountAsync(DbConnection c, DbTransaction? tx, Guid playerId, CancellationToken ct)
    {
        var links = new List<LinkedPlatformDto>();
        await using (var l = c.Command("auth.read_links", "select provider, provider_user_id, linked_at from account_links where player_id = @p order by linked_at", tx,
            ("p", playerId)))
        await using (var rd = await l.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await rd.ReadAsync(ct).ConfigureAwait(false))
                links.Add(new LinkedPlatformDto(rd.GetString(0), rd.GetString(1), rd.GetFieldValue<DateTimeOffset>(2).ToUnixTimeSeconds()));

        await using var cmd = c.Command("auth.read_account", 
            "select display_name, avatar_id, region, age_gate, created_at, last_login_at, status from accounts where player_id = @p", tx, ("p", playerId));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new AccountRow(playerId, r.GetString(0), r.GetString(1), r.GetString(2).Trim(), r.GetInt16(3), r.GetFieldValue<DateTimeOffset>(4),
                              r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.GetString(6), links);
    }
}
