using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HeroCat.Runtime.Replay;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Fish;
using HeroCat.Sim.Rng;

namespace HeroCat.Bots.Golden
{
    /// <summary>
    /// Fixtures/Fish/board-vectors.txt (05 section 15 AC5, issue #103): the Fish Board golden vector for 10's cross-platform
    /// check. From the seed-7 genesis, a scripted mix of every board command (ReleaseFish, ReleaseMax, MergeFish, MergeAll,
    /// MoveFish, DiscardFish) with short clock gaps that refill the tank through ComputeOfflineCatchup (under OFFLINE_MIN_S,
    /// so no ticks run and combat never enters the vector). One line per step: the command as its command-log JSONL, the
    /// result, the board, the tank, the fish_spawn counter and Fish Coins; the full StateHash every <see cref="HashEvery"/>
    /// steps. The script's choices come from SplitMix64 over <see cref="ScriptSeed"/>, never from a sim stream.
    /// `fish-vectors --check` (check.sh --pr and the nightly replay legs) regenerates it and compares byte for byte.
    /// </summary>
    public static class FishBoardVectors
    {
        public const int Format = 1, Steps = 2000, HashEvery = 100;
        public const ulong Seed = 7UL, ScriptSeed = 0xF15B0A4DUL;
        public const string Regenerate = "dotnet run -c Release --project src/HeroCat.Bots -- fish-vectors --update";

        /// <summary>The repo's Fixtures/Fish folder: Fixtures is a sibling of the content folder.</summary>
        public static string DefaultPath(string contentDir) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contentDir).TrimEnd('/', '\\')), "Fixtures", "Fish", "board-vectors.txt");

        public static string Generate(ContentDb db)
        {
            var sim = BotRunner.NewSimulation(db, Seed);
            long now = BotRunner.EpochSeconds;
            sim.Apply(new ApplyServerTime(now));
            sim.DrainEvents();
            ulong draws = 0;
            int Next(int n) => (int)(SplitMix64.ToUnit(SplitMix64.Derive(ScriptSeed, draws++)) * n);

            var sb = new StringBuilder();
            sb.Append("# herocat fish board vectors v").Append(Format).Append(" (05 AC5; regenerate: ").Append(Regenerate).Append(")\n");
            sb.Append("# content ").Append(db.Version).Append(' ').Append(db.ContentHash).Append(", seed ").Append(Seed)
              .Append(", script seed ").Append(RngStreams.SeedToHex(ScriptSeed)).Append('\n');
            sb.Append("# step | command | result | cells | tank refillTick | fish_spawn counter | fishCoin\n");
            for (int step = 1; step <= Steps; step++)
            {
                var c = Pick(sim, Next, ref now);
                var r = sim.Apply(c);
                sim.DrainEvents();
                var f = sim.State.Fish;
                sb.Append(step.ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(CommandCodec.Encode(c)).Append(" | ")
                  .Append(r.Ok ? "ok" : r.Code + ":" + r.Reason).Append(" | ").Append(string.Join(",", f.Cells)).Append(" | ")
                  .Append(f.TankCount.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(f.TankRefillTick.ToString(CultureInfo.InvariantCulture))
                  .Append(" | ").Append(sim.State.Rng[RngStreams.FishSpawn].Counter.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(sim.State.Wallet.FishCoin.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                if (step % HashEvery == 0) sb.Append("hash ").Append(step.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(sim.StateHash()).Append('\n');
                FishBoard.Validate(sim.State, db);
            }
            return sb.ToString();
        }

        static Command Pick(Simulation sim, System.Func<int, int> next, ref long now)
        {
            var cells = sim.State.Fish.Cells;
            switch (next(10))
            {
                case 0:
                case 1:
                    return new ReleaseFish();
                case 2:
                    return new ReleaseMax();
                case 3:
                case 4:
                {
                    var pairs = EqualPairs(cells);   // mostly a legal merge; one in four is an arbitrary pair (reject paths)
                    if (pairs.Count > 0 && next(4) != 0) { var p = pairs[next(pairs.Count)]; return new MergeFish(p.a, p.b); }
                    return new MergeFish(next(cells.Length), next(cells.Length));
                }
                case 5:
                    return new MergeAll();
                case 6:
                    return new MoveFish(next(cells.Length), next(cells.Length));
                case 7:
                    return new DiscardFish(next(cells.Length));
                default:
                    // A gap under OFFLINE_MIN_S: the tank refills directly, no record, no ticks (05 section 7).
                    now += 1 + next(sim.Db.Constants.OfflineMinS - 1);
                    return new ComputeOfflineCatchup(now);
            }
        }

        static List<(int a, int b)> EqualPairs(int[] cells)
        {
            var pairs = new List<(int, int)>();
            for (int i = 0; i < cells.Length; i++)
                for (int j = 0; j < cells.Length; j++)
                    if (i != j && cells[i] > 0 && cells[i] == cells[j]) pairs.Add((i, j));
            return pairs;
        }

        public static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }
}
