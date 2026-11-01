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

    public ServerLimits Limits { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public RedisOptions Redis { get; set; } = new();
    public BlobOptions Blobs { get; set; } = new();
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
