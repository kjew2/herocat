using System.Text.Json;
using HeroCat.Server.Content;
using HeroCat.Server.Features.Summon;
using HeroCat.Server.Pipeline;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Summon;

/// <summary>Uniforms from a list, in order (04 section 3.4: tests script u1..u3).</summary>
sealed class ScriptedUniforms : IUniformSource
{
    readonly IReadOnlyList<double> _u;
    public ScriptedUniforms(IReadOnlyList<double> u) => _u = u;
    public int Used { get; private set; }
    public double Next() => _u[Used++];
}

/// <summary>A summon_&lt;banner&gt;-style seeded stream for every pull: deterministic server rolls in tests.</summary>
sealed class SeededUniforms : IUniformSourceFactory
{
    readonly StreamUniformSource _stream;
    public SeededUniforms(ulong seed) => _stream = new StreamUniformSource(seed, 0);
    public long Counter { get { lock (_stream) return _stream.Counter; } }
    public IUniformSource Create() => new Locked(_stream);

    sealed class Locked : IUniformSource
    {
        readonly StreamUniformSource _s;
        public Locked(StreamUniformSource s) => _s = s;
        public double Next() { lock (_s) return _s.Next(); }
    }
}

/// <summary>
/// The server's half of specs/08 acceptance 1 and the pure parts of 3 and 7 (issue #199): the server's pull path (the
/// shared Sim algorithm behind <see cref="SummonEngine"/>, over the content the server loads) reproduces every row of
/// Fixtures/Summon/vectors.json and the Python oracle's 1,000-pull stream sequence, and every audit record replays.
/// </summary>
[TestFixture]
public sealed class SummonEngineTests
{
    static readonly Lazy<JsonElement> Vectors = new(() =>
        JsonDocument.Parse(File.ReadAllText(RepoPaths.Of("Fixtures/Summon/vectors.json"))).RootElement.Clone());

    /// <summary>The server's shipped content set (copied beside the server binary), at the vectors' build tier.</summary>
    static readonly Lazy<ContentDb> VectorContent = new(() =>
        ContentLoader.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, ContentSetOptions.DefaultDirectory),
                                        Enum.Parse<Tier>(Vectors.Value.GetProperty("tier").GetString()!)));

    static readonly Lazy<IContentSets> Shipped = new(() => new DirectoryContentSets(Options.Create(new ContentSetOptions())));

    static Banner B(JsonElement e) => Enum.Parse<Banner>(e.GetString()!);
    static Rarity R(JsonElement e) => Enum.Parse<Rarity>(e.GetString()!);
    static double[] Us(JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    static BannerState StateAt(BannerDef def, int pity, int level) =>
        new() { Pity = pity, Exp = level > 1 ? def.Levels[level - 1].ExpRequired - def.ExpPerPull : 0 };

    [Test]
    public void The_shipped_content_is_the_vectors_content()
    {
        Assert.That(Vectors.Value.GetProperty("contentHash").GetString(), Is.EqualTo(VectorContent.Value.ContentHash),
                    "content changed: regenerate Fixtures/Summon/vectors.json with tools/gacha_model.py --vectors");
        Assert.That(Shipped.Value.Active.ContentHash, Is.EqualTo(VectorContent.Value.ContentHash));
        Assert.That(Shipped.Value.Active.Version, Is.EqualTo(Vectors.Value.GetProperty("contentVersion").GetString()));
    }

    [Test]
    public void Every_vector_row_reproduces_through_the_server_pull_path()
    {
        var db = VectorContent.Value;
        var rows = Vectors.Value.GetProperty("rows").EnumerateArray().ToArray();
        Assert.That(rows.Length, Is.GreaterThanOrEqualTo(500));
        foreach (var row in rows)
        {
            var def = db.Banners[B(row.GetProperty("banner"))];
            int pity = row.GetProperty("pity").GetInt32();
            var us = Us(row.GetProperty("u"));
            bool force = row.GetProperty("forceEplus").GetBoolean();
            string what = row.ToString();

            // As the audit replays a row (acceptance 7), and as a single pull rolls (the guarantee flag aside).
            var r = SummonEngine.Replay(db, def, pity, row.GetProperty("level").GetInt32(), force, us[0], us[1], us[2]);
            Assert.That(r.Rarity, Is.EqualTo(R(row.GetProperty("rarity"))), what);
            Assert.That(def.Pool[r.Rarity].ToList().IndexOf(r.ItemId), Is.EqualTo(row.GetProperty("poolIndex").GetInt32()), what);
            Assert.That(r.ItemId, Is.EqualTo(row.GetProperty("itemId").GetString()), what);
            Assert.That(r.PityAfter, Is.EqualTo(row.GetProperty("pityAfter").GetInt32()), what);
            Assert.That(r.GuaranteeApplied, Is.EqualTo(row.GetProperty("guaranteeApplied").GetBoolean()), what);
            Assert.That(r.BannerLevelAfter, Is.EqualTo(row.GetProperty("bannerLevelAfter").GetInt32()), what);

            if (force) continue;
            var st = StateAt(def, pity, row.GetProperty("level").GetInt32());
            var roll = SummonEngine.Roll(db, def, st, new CollectionState(), new ScriptedUniforms(us), 1);
            Assert.That(roll.Results[0].ItemId, Is.EqualTo(row.GetProperty("itemId").GetString()), what);
            var a = roll.Audits[0];
            Assert.That((a.U1, a.U2, a.U3), Is.EqualTo((us[0], us[1], us[2])), what);
            Assert.That((a.PityBefore, a.PityAfter, a.Guarantee), Is.EqualTo((pity, r.PityAfter, false)), what);
            Assert.That(a.BannerLevel, Is.EqualTo(row.GetProperty("bannerLevelAfter").GetInt32()), what);
            Assert.That(a.RateTableHash, Is.EqualTo(RateTableHash.Of(def, a.BannerLevel)), what);
        }
    }

    [Test]
    public void Every_vector_batch_reproduces_through_the_server_pull_path()
    {
        var db = VectorContent.Value;
        var batches = Vectors.Value.GetProperty("batches").EnumerateArray().ToArray();
        Assert.That(batches.Length, Is.EqualTo(20));
        foreach (var b in batches)
        {
            var def = db.Banners[B(b.GetProperty("banner"))];
            int pity = b.GetProperty("pity").GetInt32();
            var st = StateAt(def, pity, b.GetProperty("level").GetInt32());
            var us = Us(b.GetProperty("u"));
            var roll = SummonEngine.Roll(db, def, st, new CollectionState(), new ScriptedUniforms(us), 10);
            var exp = b.GetProperty("expect").EnumerateArray().ToArray();
            Assert.That(roll.Results.Length, Is.EqualTo(10));
            int before = pity;
            for (int i = 0; i < 10; i++)
            {
                Assert.That(roll.Results[i].Rarity, Is.EqualTo(R(exp[i].GetProperty("rarity"))));
                Assert.That(roll.Results[i].ItemId, Is.EqualTo(exp[i].GetProperty("itemId").GetString()));
                Assert.That(roll.Results[i].PityAfter, Is.EqualTo(exp[i].GetProperty("pityAfter").GetInt32()));
                Assert.That(roll.Results[i].GuaranteeApplied, Is.EqualTo(exp[i].GetProperty("guaranteeApplied").GetBoolean()));
                var a = roll.Audits[i];
                Assert.That(a.BatchIndex, Is.EqualTo(i));
                Assert.That(a.PityBefore, Is.EqualTo(before), "pity_before chains through the batch");
                Assert.That((a.U1, a.U2, a.U3), Is.EqualTo((us[3 * i], us[3 * i + 1], us[3 * i + 2])));
                if (roll.Results[i].GuaranteeApplied) Assert.That(a.Guarantee, Is.True, "a forced E ran with forceEplus");
                if (i < 9) Assert.That(a.Guarantee, Is.False, "only slot 10 can be forced");
                before = a.PityAfter;
            }
        }
    }

    [Test]
    public void The_python_oracles_seeded_1000_pull_sequence_reproduces_through_the_server_pull_path()
    {
        var d = Vectors.Value.GetProperty("drawCount");
        var db = VectorContent.Value;
        var def = db.Banners[Banner.WEAPON];
        var st = new BannerState();
        var rng = new StreamUniformSource((ulong)d.GetProperty("seed").GetInt64(), 0);
        var counts = new Dictionary<string, int>();
        for (int i = 0; i < d.GetProperty("pulls").GetInt32(); i++)
        {
            var r = SummonEngine.Roll(db, def, st, new CollectionState(), rng, 1).Results[0];
            counts[r.Rarity.ToString()] = counts.GetValueOrDefault(r.Rarity.ToString()) + 1;
        }
        Assert.That(rng.Counter, Is.EqualTo(d.GetProperty("counterAfter").GetInt64()));
        Assert.That(st.TotalPulls, Is.EqualTo(d.GetProperty("totalPulls").GetInt64()));
        Assert.That(st.Exp, Is.EqualTo(d.GetProperty("exp").GetInt64()));
        Assert.That(st.Pity, Is.EqualTo(d.GetProperty("pityAfter").GetInt32()));
        foreach (var c in d.GetProperty("rarityCounts").EnumerateObject())
            Assert.That(counts.GetValueOrDefault(c.Name), Is.EqualTo(c.Value.GetInt32()), c.Name);
    }

    [Test]
    public void Csprng_rolls_consume_exactly_three_uniforms_per_pull_and_every_audit_replays()
    {
        // Acceptance 3 (30 uniforms per batch) and 7 (each pull reproducible from its audit facts), on live CSPRNG draws.
        var db = Shipped.Value.Active;
        var byHash = HashIndex(db);
        foreach (var def in db.Banners.Values.Where(d => d.Enabled && d.Levels.Count > 0))
        {
            var st = new BannerState();
            var col = new CollectionState();
            for (int batch = 0; batch < 120; batch++)
            {
                var counting = new RecordingUniformSource(new CsprngUniformSource());
                var roll = SummonEngine.Roll(db, def, st, col, counting, batch % 3 == 0 ? 1 : 10);
                Assert.That(counting.Drawn.Count, Is.EqualTo(3 * roll.Results.Length));
                Assert.That(counting.Drawn, Has.All.GreaterThanOrEqualTo(0.0).And.LessThan(1.0));
                for (int i = 0; i < roll.Results.Length; i++)
                {
                    var a = roll.Audits[i];
                    var (tdef, level) = byHash[RateTableHash.Hex(a.RateTableHash)];
                    Assert.That(level, Is.EqualTo(a.BannerLevel));
                    var replay = SummonEngine.Replay(db, tdef, a.PityBefore, a.BannerLevel, a.Guarantee, a.U1, a.U2, a.U3);
                    Assert.That((replay.Rarity, replay.ItemId, replay.PityAfter), Is.EqualTo((roll.Results[i].Rarity, roll.Results[i].ItemId, a.PityAfter)));
                }
            }
            Assert.That(st.TotalPulls, Is.EqualTo(st.Exp / def.ExpPerPull));
        }
    }

    /// <summary>tableFor(rate_table_hash): every banner level of a content set by its hash.</summary>
    internal static Dictionary<string, (BannerDef def, int level)> HashIndex(ContentDb db)
    {
        var map = new Dictionary<string, (BannerDef, int)>();
        foreach (var def in db.Banners.Values)
            for (int level = 1; level <= def.Levels.Count; level++)
                map.Add(RateTableHash.Hex(RateTableHash.Of(def, level)), (def, level));
        return map;
    }

    [Test]
    public void Rate_table_hashes_are_distinct_per_banner_and_level_and_stable()
    {
        var db = Shipped.Value.Active;
        int tables = db.Banners.Values.Sum(d => d.Levels.Count);
        Assert.That(HashIndex(db).Count, Is.EqualTo(tables), "every (banner, level) has its own hash");
        var weapon = db.Banners[Banner.WEAPON];
        string text = RateTableHash.CanonicalText(weapon, 1);
        Assert.That(text, Does.StartWith("herocat.rate_table.v1\nbanner=WEAPON\nlevel=1\nrates=C:0.6,R:0.28,E:0.09,L:0.025,M:0.005\n"));
        Assert.That(text, Does.Contain("\npity=softStart:50,softStep:0.0325,hard:80,mShareOfLplus:"));
        Assert.That(text, Does.Contain("\nguarantee=batch:10,min:EPIC\n"));
        Assert.That(text, Does.Contain("\npool.C=" + string.Join(",", weapon.Pool[Rarity.COMMON]) + "\n"));
        Assert.That(RateTableHash.Hex(RateTableHash.Of(weapon, 1)), Has.Length.EqualTo(64).And.Match("^[0-9a-f]+$"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateTableHash.Of(weapon, weapon.Levels.Count + 1));
    }

    [Test]
    public void Csprng_unit_mapping_spans_zero_to_just_below_one()
    {
        Assert.That(CsprngUniformSource.ToUnit(0), Is.EqualTo(0.0));
        Assert.That(CsprngUniformSource.ToUnit(ulong.MaxValue), Is.LessThan(1.0).And.EqualTo(1.0 - Math.Pow(2, -53)));
    }

    [Test]
    public void Rarity_codes_round_trip()
    {
        foreach (var r in Enum.GetValues<Rarity>()) Assert.That(RarityCodes.Parse(RarityCodes.Of(r)), Is.EqualTo(r));
        Assert.That(RarityCodes.Of(Rarity.RARE), Is.EqualTo("R"));
    }

    [Test]
    public void Switches_disable_by_banner_and_schedule_only_the_banners_they_list()
    {
        var s = new SummonSwitches(new Dictionary<Banner, bool> { [Banner.WEAPON] = false },
                                   new[] { new BannerWindow(Banner.COMPANION, 100, 200), new BannerWindow(Banner.COMPANION, 300, null) });
        Assert.That(s.IsEnabled(Banner.WEAPON), Is.False);
        Assert.That(s.IsEnabled(Banner.SKILL), Is.True, "absent from summonEnabled is enabled");
        Assert.That(s.IsOpen(Banner.SKILL, 0), Is.True, "absent from banners[] is open");
        Assert.That(new[] { 99L, 100, 199, 200, 299, 300, 10_000 }.Select(t => s.IsOpen(Banner.COMPANION, t)),
                    Is.EqualTo(new[] { false, true, true, false, false, true, true }));
    }

    [Test]
    public void Content_sets_resolve_exact_versions_and_refuse_old_or_unknown_ones()
    {
        var db = Shipped.Value.Active;
        var sets = new DirectoryContentSets(new[] { db });
        Assert.That(sets.Resolve(db.Version), Is.SameAs(db));
        var old = Assert.Throws<ApiException>(() => sets.Resolve("2020.01.01"))!;
        Assert.That((old.Status, old.Code), Is.EqualTo((409, ErrorCodes.CONTENT_TOO_OLD)));
        var unknown = Assert.Throws<ApiException>(() => sets.Resolve("2999.01.01"))!;
        Assert.That((unknown.Status, unknown.Code), Is.EqualTo((400, ErrorCodes.VALIDATION)));
        var junk = Assert.Throws<ApiException>(() => sets.Resolve("latest"))!;
        Assert.That(junk.Code, Is.EqualTo(ErrorCodes.VALIDATION));
    }
}
