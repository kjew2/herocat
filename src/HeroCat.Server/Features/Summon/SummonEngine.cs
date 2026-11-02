using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;

namespace HeroCat.Server.Features.Summon;

/// <summary>Where the server's pulls draw their uniforms (specs/08 section 4.2): a CSPRNG in production, a seeded stream in tests.</summary>
public interface IUniformSourceFactory
{
    IUniformSource Create();
}

/// <summary>
/// Uniforms in [0, 1) from the OS CSPRNG: the top 53 bits of a random 64-bit word times 2^-53, so every double the
/// Sim's <see cref="StreamUniformSource"/> can produce is reachable and 1.0 never is.
/// </summary>
public sealed class CsprngUniformSource : IUniformSource
{
    public static readonly IUniformSourceFactory Factory = new CsprngFactory();

    public double Next()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        return ToUnit(BitConverter.ToUInt64(b));
    }

    public static double ToUnit(ulong x) => (x >> 11) * (1.0 / (1UL << 53));

    sealed class CsprngFactory : IUniformSourceFactory
    {
        public IUniformSource Create() => new CsprngUniformSource();
    }
}

/// <summary>Records every draw it hands out, so the audit row can log each pull's three uniforms (specs/08 section 4.4).</summary>
public sealed class RecordingUniformSource : IUniformSource
{
    readonly IUniformSource _inner;
    readonly List<double> _drawn = new();

    public RecordingUniformSource(IUniformSource inner) => _inner = inner;

    public IReadOnlyList<double> Drawn => _drawn;

    public double Next()
    {
        double u = _inner.Next();
        _drawn.Add(u);
        return u;
    }
}

/// <summary>One pull's audit facts beside its <see cref="PullResult"/> (a <c>summon_log</c> row, specs/08 section 4.4).</summary>
public sealed record PullAudit(int BatchIndex, double U1, double U2, double U3, int PityBefore, int PityAfter, int BannerLevel,
                               bool Guarantee, byte[] RateTableHash);

/// <summary>The outcome of one request's roll: the Sim's results and the audit facts, index for index.</summary>
public sealed record SummonRoll(PullResult[] Results, PullAudit[] Audits);

/// <summary>
/// The server side of the one pull algorithm (00 decision 56, issue #24): it runs the Sim's <see cref="PullAlgorithm"/>
/// unchanged on the server's own state rows and records what the audit log needs. Pure: no clock, no I/O.
/// </summary>
public static class SummonEngine
{
    /// <summary>
    /// Rolls <paramref name="count"/> pulls (1, or the banner's guarantee batch) on <paramref name="st"/> and
    /// <paramref name="col"/>, which it mutates as the Sim does. Each audit's level and rate table are the ones that pull
    /// used (EXP is added before the draw, 04 section 3.2); <c>Guarantee</c> is whether the slot ran with forceEplus.
    /// </summary>
    public static SummonRoll Roll(ContentDb db, BannerDef def, BannerState st, CollectionState col, IUniformSource rng, int count)
    {
        var rec = new RecordingUniformSource(rng);
        int pityBefore = st.Pity;
        var results = PullAlgorithm.PullBatch(db, def, st, col, rec, count);
        if (rec.Drawn.Count != PullAlgorithm.DrawsPerPull * results.Length)
            throw new InvalidOperationException($"expected {PullAlgorithm.DrawsPerPull * results.Length} draws, got {rec.Drawn.Count}");

        var audits = new PullAudit[results.Length];
        bool anyAtLeastMin = false;
        for (int i = 0; i < results.Length; i++)
        {
            var r = results[i];
            bool forced = results.Length == def.GuaranteeBatchSize && i == results.Length - 1 && !anyAtLeastMin;
            int d = i * PullAlgorithm.DrawsPerPull;
            audits[i] = new PullAudit(i, rec.Drawn[d], rec.Drawn[d + 1], rec.Drawn[d + 2], pityBefore, r.PityAfter, r.BannerLevelAfter, forced,
                                      RateTableHash.Of(def, r.BannerLevelAfter));
            if (r.Rarity >= def.GuaranteeMinRarity) anyAtLeastMin = true;
            pityBefore = r.PityAfter;
        }
        return new SummonRoll(results, audits);
    }

    /// <summary>
    /// Replays one logged pull (specs/08 acceptance 7): the Sim's <c>Pull</c> on the logged pity, level, guarantee and
    /// uniforms. Collection state does not change rarity or item, so an empty one is used.
    /// </summary>
    public static PullResult Replay(ContentDb db, BannerDef def, int pityBefore, int bannerLevel, bool guarantee, double u1, double u2, double u3)
    {
        // The pull adds its EXP before drawing, so start one pull's EXP below the level's threshold (level 1 at 0).
        long exp = bannerLevel > 1 ? def.Levels[bannerLevel - 1].ExpRequired - def.ExpPerPull : 0;
        var st = new BannerState { Pity = pityBefore, Exp = exp };
        return PullAlgorithm.Pull(db, def, st, new CollectionState(), new FixedUniforms(u1, u2, u3), guarantee);
    }

    sealed class FixedUniforms : IUniformSource
    {
        readonly double[] _u;
        int _i;
        public FixedUniforms(params double[] u) => _u = u;
        public double Next() => _u[_i++];
    }
}

/// <summary>The one-letter rarity codes of the wire (specs/08 section 4.3 <c>"rarity": "R"</c>) and of <c>summon_log.rarity</c>.</summary>
public static class RarityCodes
{
    public static string Of(Rarity r) => r switch
    {
        Rarity.COMMON => "C",
        Rarity.RARE => "R",
        Rarity.EPIC => "E",
        Rarity.LEGENDARY => "L",
        Rarity.MYTHIC => "M",
        _ => throw new ArgumentOutOfRangeException(nameof(r), r, null),
    };

    public static Rarity Parse(string code) => code.Trim() switch
    {
        "C" => Rarity.COMMON,
        "R" => Rarity.RARE,
        "E" => Rarity.EPIC,
        "L" => Rarity.LEGENDARY,
        "M" => Rarity.MYTHIC,
        _ => throw new FormatException("rarity code " + code),
    };
}

/// <summary>
/// <c>rate_table_hash</c> (specs/08 sections 4.4, 4.5): SHA-256 over a canonical text of everything a pull at one banner
/// level depends on, so the hash alone identifies the table to replay a logged pull with (acceptance 7): the banner,
/// the level, its five rates, the pity ramp, the M share of L+, the 10-pull guarantee, and the ordered item pool per
/// rarity. Numbers are invariant round-trip ("R") text. The wire form is lowercase hex; <c>summon_log</c> stores the bytes.
/// </summary>
public static class RateTableHash
{
    public const string Prefix = "herocat.rate_table.v1";

    static readonly Rarity[] Order = { Rarity.COMMON, Rarity.RARE, Rarity.EPIC, Rarity.LEGENDARY, Rarity.MYTHIC };

    public static byte[] Of(BannerDef def, int level) => SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalText(def, level)));

    public static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    public static string CanonicalText(BannerDef def, int level)
    {
        if (level < 1 || level > def.Levels.Count) throw new ArgumentOutOfRangeException(nameof(level), level, $"banner {def.Id} has {def.Levels.Count} levels");
        var rates = def.Levels[level - 1].Rates;
        var sb = new StringBuilder();
        sb.Append(Prefix).Append('\n');
        sb.Append("banner=").Append(def.Id).Append('\n');
        sb.Append("level=").Append(level.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("rates=").Append(string.Join(",", Order.Select(r => RarityCodes.Of(r) + ":" + Num(rates[r])))).Append('\n');
        sb.Append("pity=softStart:").Append(def.PitySoftStart.ToString(CultureInfo.InvariantCulture))
          .Append(",softStep:").Append(Num(def.PitySoftStep))
          .Append(",hard:").Append(def.PityHard.ToString(CultureInfo.InvariantCulture))
          .Append(",mShareOfLplus:").Append(Num(def.MShareOfLplus)).Append('\n');
        sb.Append("guarantee=batch:").Append(def.GuaranteeBatchSize.ToString(CultureInfo.InvariantCulture))
          .Append(",min:").Append(def.GuaranteeMinRarity).Append('\n');
        foreach (var r in Order)
            sb.Append("pool.").Append(RarityCodes.Of(r)).Append('=')
              .Append(string.Join(",", def.Pool.TryGetValue(r, out var ids) ? ids : Array.Empty<string>())).Append('\n');
        return sb.ToString();
    }

    static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
