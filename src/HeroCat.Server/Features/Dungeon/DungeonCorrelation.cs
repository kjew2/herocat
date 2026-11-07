using System.Data.Common;
using System.Text.Json;
using HeroCat.Server.Pipeline;
using SimDungeon = HeroCat.Sim.Content.Dungeon;

namespace HeroCat.Server.Features.Dungeon;

/// <summary>One <c>Dungeons.RecentRuns</c> entry of an uploaded save (specs/02 section 4).</summary>
public sealed record UploadedRun(string GrantId, string Kind, int Floor, bool Cleared);

/// <summary>What an uploaded save says about dungeons: <c>BestFloor</c> by kind name and <c>RecentRuns</c>.</summary>
public sealed record UploadedDungeons(IReadOnlyDictionary<string, int> BestFloor, IReadOnlyList<UploadedRun> RecentRuns)
{
    /// <summary>
    /// Reads <c>Dungeons</c> from the save body (JSON). Null when the body has no <c>Dungeons</c> object (nothing to
    /// correlate). <c>BestFloor</c> is by <c>Dungeon</c> ordinal; entries that are not well-formed are skipped.
    /// </summary>
    public static UploadedDungeons? Read(ReadOnlyMemory<byte> body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("Dungeons", out var d) ||
            d.ValueKind != JsonValueKind.Object)
            return null;
        var kinds = Enum.GetValues<SimDungeon>();
        var best = new Dictionary<string, int>(StringComparer.Ordinal);
        if (d.TryGetProperty("BestFloor", out var bf) && bf.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var v in bf.EnumerateArray())
            {
                if (i < kinds.Length && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int f)) best[kinds[i].ToString()] = Math.Max(0, f);
                i++;
            }
        }
        var runs = new List<UploadedRun>();
        if (d.TryGetProperty("RecentRuns", out var rr) && rr.ValueKind == JsonValueKind.Array)
            foreach (var r in rr.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                string? kind = r.TryGetProperty("Kind", out var k) ? k.ValueKind switch
                {
                    JsonValueKind.String => k.GetString(),
                    JsonValueKind.Number when k.TryGetInt32(out int o) && o >= 0 && o < kinds.Length => kinds[o].ToString(),
                    _ => null,
                } : null;
                if (kind == null || !r.TryGetProperty("Floor", out var fl) || !fl.TryGetInt32(out int floor)) continue;
                string grant = r.TryGetProperty("GrantId", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : "";
                bool cleared = r.TryGetProperty("Cleared", out var cl) && cl.ValueKind == JsonValueKind.True;
                runs.Add(new UploadedRun(grant, kind, floor, cleared));
            }
        return new UploadedDungeons(best, runs);
    }
}

/// <summary>
/// The seed/save correlation of <c>PUT /save</c> (specs/08 section 7.3 "Dungeon correlation", issue #52 decision; issue
/// #205), run inside the upload's transaction. As built:
/// <list type="number">
/// <item>each <c>RecentRuns</c> entry whose <c>GrantId</c> is a seed issued to this player for the same kind and floor
/// fills that row's <c>dungeon_seeds.result</c> (<c>cleared</c> / <c>failed</c>; the newest entry for a grant wins); any
/// other entry (unknown, empty, another player's, or another floor's grant) raises <c>DUNGEON_WITHOUT_SEED</c>;</item>
/// <item>every floor of <c>BestFloor[kind]</c> above the previous upload's (0 before any) needs at least one seed issued
/// to the player for that kind and floor, else <c>DUNGEON_WITHOUT_SEED</c>;</item>
/// <item>the upload's <c>BestFloor</c> becomes the player's <c>dungeon_progress</c>, which <c>POST /dungeon/seed</c>'s
/// unlock check reads.</item>
/// </list>
/// Both flags are <c>certain</c>, one per upload with the offending entries as evidence. A body without <c>Dungeons</c>
/// is not correlated.
/// </summary>
public sealed class DungeonCorrelation
{
    public async Task<IReadOnlyList<PlausibilityFlag>> CorrelateAsync(DbConnection c, DbTransaction tx, Guid player, long saveSeq,
                                                                     ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        UploadedDungeons? up;
        try { up = UploadedDungeons.Read(body); }
        catch (JsonException) { return Array.Empty<PlausibilityFlag>(); }   // the envelope already parsed it; unreachable
        if (up == null) return Array.Empty<PlausibilityFlag>();

        var unmatched = new List<object>();
        var grants = new List<(Guid id, UploadedRun run)>();
        foreach (var run in up.RecentRuns)
        {
            if (Guid.TryParse(run.GrantId, out var g)) grants.Add((g, run));
            else unmatched.Add(new { grantId = run.GrantId, kind = run.Kind, floor = run.Floor });
        }
        var issued = await DungeonStore.IssuedAsync(c, tx, player, grants.Select(x => x.id).Distinct().ToArray(), ct).ConfigureAwait(false);
        var results = new Dictionary<Guid, bool>();
        foreach (var (id, run) in grants)
        {
            if (issued.TryGetValue(id, out var seed) && seed.Kind == run.Kind && seed.Floor == run.Floor) results[id] = run.Cleared;   // newest last
            else unmatched.Add(new { grantId = run.GrantId, kind = run.Kind, floor = run.Floor });
        }
        foreach (var (id, cleared) in results) await DungeonStore.SetResultAsync(c, tx, player, id, cleared, ct).ConfigureAwait(false);

        var previous = await DungeonStore.BestFloorsAsync(c, tx, player, ct).ConfigureAwait(false);
        var unseeded = new List<object>();
        foreach (var (kind, best) in up.BestFloor)
        {
            int before = previous.GetValueOrDefault(kind);
            if (best <= before) continue;
            int[] floors = Enumerable.Range(before + 1, best - before).ToArray();
            var seeded = await DungeonStore.FloorsWithSeedAsync(c, tx, player, kind, floors, ct).ConfigureAwait(false);
            foreach (int f in floors)
                if (!seeded.Contains(f)) unseeded.Add(new { kind, floor = f, previousBestFloor = before, bestFloor = best });
        }
        await DungeonStore.UpsertBestFloorsAsync(c, tx, player, saveSeq, up.BestFloor, ct).ConfigureAwait(false);

        if (unmatched.Count == 0 && unseeded.Count == 0) return Array.Empty<PlausibilityFlag>();
        return new[]
        {
            new PlausibilityFlag(player, DungeonFlags.DUNGEON_WITHOUT_SEED, PlausibilityFlag.Certain, new { runs = unmatched, bestFloor = unseeded }),
        };
    }
}
