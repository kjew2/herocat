using System;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Rng
{
    /// <summary>
    /// The sim side of the IDungeonSeedService seam (00 contract 11, 01 section 8.8 step 2; issue #25). In M0 and bots a run
    /// seed is the next draw of the persisted `dungeon` stream, read without advancing it: the local service returns it with
    /// localSeed = true and Apply(StartDungeon) consumes exactly that one draw, so the service never writes state (as for
    /// summons, issue #6). Simulation.PreviewDungeon uses the same peek, so a preview and the local run that follows it share
    /// a seed. In MVP the server issues the seed (08 section 3.3) and the `dungeon` stream is untouched.
    /// </summary>
    public static class LocalDungeonSeed
    {
        /// <summary>The seed the next local run gets: Derive(seed(dungeon), dungeon.Counter). Throws if the stream is missing or malformed (02 R4).</summary>
        public static ulong Peek(GameState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!state.Rng.TryGetValue(RngStreams.Dungeon, out var stream) || !RngStreams.TryParseSeed(stream.Seed, out ulong seed))
                throw new InvalidOperationException("the `dungeon` stream is missing or malformed (02 R4)");
            return SplitMix64.Derive(seed, (ulong)stream.Counter);
        }
    }
}
