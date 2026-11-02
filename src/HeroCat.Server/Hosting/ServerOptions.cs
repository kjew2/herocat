using Microsoft.Extensions.Configuration;

namespace HeroCat.Server.Hosting;

/// <summary>
/// The <c>HeroCat</c> configuration section (appsettings.json, environment variables <c>HeroCat__...</c>). Secrets
/// (database password, Redis auth token, signing keys) are not here: specs/08 section 9.3 reads them from Secrets
/// Manager at container start.
/// </summary>
public sealed class ServerOptions
{
    public const string Section = "HeroCat";

    /// <summary>The server version shown by <c>/health</c> (specs/10 section 6.3: the <c>YYYY.MM.DD[.N]</c> deploy tag).</summary>
    public string Version { get; set; } = "dev";

    /// <summary>The environment this deployment serves (specs/08 section 9.2: <c>dev</c>, <c>stage</c>, <c>prod</c>); <c>config_versions.env</c>.</summary>
    public string Env { get; set; } = "dev";

    public ServerLimits Limits { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public RedisOptions Redis { get; set; } = new();
    public BlobOptions Blobs { get; set; } = new();
    public KeysOptions Keys { get; set; } = new();
    public NetworkOptions Network { get; set; } = new();
    public AnalyticsOptions Analytics { get; set; } = new();
}

/// <summary>
/// The engineering constants of specs/08 "Constants introduced", keyed in appsettings by their spec names so a grep for
/// the spec name finds the value. ServerConstantsTests checks appsettings.json against the spec table.
/// Rate limits and alarm thresholds come with their endpoints (#193, #210).
/// </summary>
public sealed class ServerLimits
{
    [ConfigurationKeyName("IDEMPOTENCY_TTL_H")] public int IdempotencyTtlHours { get; set; }
    [ConfigurationKeyName("ACCESS_TOKEN_TTL_S")] public int AccessTokenTtlSeconds { get; set; }
    [ConfigurationKeyName("REFRESH_TOKEN_TTL_DAYS")] public int RefreshTokenTtlDays { get; set; }
    /// <summary>A same-device retry of a refresh token rotated out this recently is a lost response, not reuse (issue #273).</summary>
    [ConfigurationKeyName("REFRESH_REUSE_GRACE_S")] public int RefreshReuseGraceSeconds { get; set; }
    [ConfigurationKeyName("SUMMON_LOCK_TTL_S")] public int SummonLockTtlSeconds { get; set; }
    /// <summary>Seeds per day per dungeon kind.</summary>
    [ConfigurationKeyName("DUNGEON_SEEDS_PER_DAY")] public int DungeonSeedsPerDay { get; set; }
    [ConfigurationKeyName("DUNGEON_SEED_TTL_S")] public int DungeonSeedTtlSeconds { get; set; }
    [ConfigurationKeyName("REFUND_ABUSE_COUNT")] public RefundAbuseCount RefundAbuseCount { get; set; } = new();
    [ConfigurationKeyName("MAIL_GRANT_APPROVAL_DIAMONDS")] public int MailGrantApprovalDiamonds { get; set; }
    [ConfigurationKeyName("STAGE_BOUND_MULT")] public SeverityBound StageBoundMult { get; set; } = new();
    [ConfigurationKeyName("DPS_BOUND_MARGIN")] public double DpsBoundMargin { get; set; }
    [ConfigurationKeyName("GOLD_BOUND_MULT")] public SeverityBound GoldBoundMult { get; set; } = new();
    [ConfigurationKeyName("RESIM_SAMPLE_PCT")] public double ResimSamplePct { get; set; }
    [ConfigurationKeyName("BAN_REVIEW_SLA_H")] public int BanReviewSlaHours { get; set; }
    [ConfigurationKeyName("CLOCK_MAX_SKEW_ACCEPT_S")] public int ClockMaxSkewAcceptSeconds { get; set; }
    [ConfigurationKeyName("SAVE_BLOB_MAX_BYTES")] public int SaveBlobMaxBytes { get; set; }
    [ConfigurationKeyName("SAVE_HISTORY_KEEP")] public int SaveHistoryKeep { get; set; }
    /// <summary>specs/08 section 10.1 "Age of digital consent default" (the table gives it no UPPER_SNAKE name).</summary>
    [ConfigurationKeyName("AGE_OF_DIGITAL_CONSENT_DEFAULT")] public int AgeOfDigitalConsentDefault { get; set; }
    /// <summary>How old the in-process copy of the active config may get before a read reloads it (specs/08 section 6.2, issue #204).</summary>
    [ConfigurationKeyName("CONFIG_REFRESH_S")] public int ConfigRefreshSeconds { get; set; }
    /// <summary>Events per <c>POST /analytics/events</c> batch (specs/02 section 14.2).</summary>
    [ConfigurationKeyName("ANALYTICS_BATCH_MAX")] public int AnalyticsBatchMax { get; set; }
    /// <summary>An analytics event older than this is refused per event (specs/08 section 3.5).</summary>
    [ConfigurationKeyName("ANALYTICS_EVENT_MAX_AGE_DAYS")] public int AnalyticsEventMaxAgeDays { get; set; }
    /// <summary>An analytics event whose props serialise larger than this is refused per event (specs/08 section 3.5).</summary>
    [ConfigurationKeyName("ANALYTICS_PROPS_MAX_BYTES")] public int AnalyticsPropsMaxBytes { get; set; }
    /// <summary>Monthly partitions (diamond_ledger, summon_log) kept ahead of now by the worker (specs/02 section 12).</summary>
    [ConfigurationKeyName("PARTITION_AHEAD_MONTHS")] public int PartitionAheadMonths { get; set; }
    /// <summary>Daily partitions (events) kept ahead of now by the worker (specs/02 section 12).</summary>
    [ConfigurationKeyName("PARTITION_AHEAD_DAYS")] public int PartitionAheadDays { get; set; }
}

/// <summary>A bound with a <c>suspicious</c> and a <c>certain</c> level (specs/08 section 7.2).</summary>
public sealed class SeverityBound
{
    public double Suspicious { get; set; }
    public double Certain { get; set; }
}

/// <summary><c>REFUND_ABUSE_COUNT</c>: refunds per window that soft-flag, and that open a ban review (specs/08 section 5.4).</summary>
public sealed class RefundAbuseCount
{
    public int SoftFlag { get; set; }
    public int BanReview { get; set; }
    public int WindowDays { get; set; }
}

/// <summary>
/// Which proxy hops are trusted to report the client address (specs/08 section 9.1, issue #263). Only a peer inside
/// <see cref="TrustedProxies"/> may set <c>X-Forwarded-For</c>, and only its last hop is used; empty trusts nobody.
/// </summary>
public sealed class NetworkOptions
{
    /// <summary>CIDRs (<c>10.0.0.0/16</c>) or single addresses of the load balancer subnets, from the IaC outputs (#211).</summary>
    public List<string> TrustedProxies { get; set; } = new();
}

/// <summary>
/// The backend analytics sink (specs/02 section 14, specs/08 section 3.5). Neither value is a secret: the salt also ships in
/// the client build, so the server's <c>player_id_hash</c> equals the client's <c>playerIdHash</c> and Firebase rows join
/// the <c>events</c> table.
/// </summary>
public sealed class AnalyticsOptions
{
    /// <summary>The <c>events.env</c> value: <c>dev</c>, <c>stage</c>, <c>prod</c> (section 9.2), <c>local</c> for a local run.</summary>
    public string Env { get; set; } = "";

    /// <summary>The per-environment salt of <c>playerIdHash</c> (specs/02 section 14.1), the same one the client build uses.</summary>
    public string PlayerIdSalt { get; set; } = "";

    public string RequireEnv() =>
        string.IsNullOrWhiteSpace(Env) ? throw new InvalidOperationException("HeroCat:Analytics:Env is not configured") : Env;

    public string RequireSalt() =>
        string.IsNullOrEmpty(PlayerIdSalt) ? throw new InvalidOperationException("HeroCat:Analytics:PlayerIdSalt is not configured") : PlayerIdSalt;
}

public sealed class DatabaseOptions
{
    /// <summary>Npgsql connection string; pooling is Npgsql's (RDS Proxy at 100k DAU, specs/08 section 9.1).</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>The SQL migration set, relative to the app's base directory (the build copies server/db/migrations there).</summary>
    public string MigrationsPath { get; set; } = "db/migrations";

    /// <summary>Apply pending migrations when the api starts. Off by default: the deploy pipeline runs <c>--migrate</c> first.</summary>
    public bool MigrateOnStartup { get; set; }
}

public sealed class RedisOptions
{
    /// <summary>StackExchange.Redis configuration string (<c>host:6379,password=...</c>); empty means not configured.</summary>
    public string Configuration { get; set; } = "";
}

public sealed class BlobOptions
{
    /// <summary><c>s3</c> (AWS, or MinIO with <see cref="ServiceUrl"/>) or <c>memory</c> (tests, local development).</summary>
    public string Provider { get; set; } = BlobProviders.S3;

    /// <summary>Empty for AWS; the MinIO / LocalStack endpoint otherwise.</summary>
    public string ServiceUrl { get; set; } = "";

    public string Region { get; set; } = "us-east-1";

    /// <summary>MinIO needs path-style addressing.</summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>specs/08 section 9.1 bucket names (<c>herocat-saves-{env}</c>, <c>herocat-receipts-{env}</c>).</summary>
    public string SavesBucket { get; set; } = "";
    public string ReceiptsBucket { get; set; } = "";
}

public static class BlobProviders
{
    public const string S3 = "s3";
    public const string Memory = "memory";
}

/// <summary>
/// Key material (specs/08 section 9.3): the JWT signing keys, the support-code server key and the data key that encrypts
/// device HMAC keys. Values are base64 (PEM for RSA) inline or a <c>...File</c> path, never committed. TODO(#272): load
/// them from Secrets Manager / KMS instead of configuration.
/// </summary>
public sealed class KeysOptions
{
    /// <summary>Active RS256 signing keys; the first signs, every one verifies (two during a 90-day rotation).</summary>
    public List<SigningKeyOptions> SigningKeys { get; set; } = new();

    /// <summary>HMAC key of the support code <c>base32(hmac(playerId, serverKey))[:12]</c> (section 2.1), base64.</summary>
    public string RecoveryKey { get; set; } = "";
    public string RecoveryKeyFile { get; set; } = "";

    /// <summary>AES-256 data key for device HMAC keys and protected idempotency bodies, base64 of 32 bytes.</summary>
    public string DataKey { get; set; } = "";
    public string DataKeyFile { get; set; } = "";

    /// <summary>
    /// Generate any missing key in memory at start-up (logged). For local runs and tests only: tokens do not survive a
    /// restart or validate on another instance. On in appsettings.Development.json; off elsewhere.
    /// </summary>
    public bool AllowEphemeral { get; set; }
}

public sealed class SigningKeyOptions
{
    /// <summary>The JWT header <c>kid</c>.</summary>
    public string Kid { get; set; } = "";
    /// <summary>PKCS#8 or PKCS#1 PEM of the RSA private key, or the path to it in <see cref="PemFile"/>.</summary>
    public string Pem { get; set; } = "";
    public string PemFile { get; set; } = "";
}
