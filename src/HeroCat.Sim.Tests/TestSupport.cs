using System;
using System.Collections.Generic;
using System.Text.Json;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    internal static class TestContent
    {
        static readonly Lazy<ContentDb> _db = new Lazy<ContentDb>(() =>
        {
            string dir = FileContentSource.FindContentDirectory(TestContext.CurrentContext.TestDirectory)
                         ?? throw new InvalidOperationException("Content/manifest.json not found above the test directory");
            return ContentDb.LoadFromDirectory(dir);
        });

        public static ContentDb Db => _db.Value;

        /// <summary>2026-01-01T00:00:00Z.</summary>
        public static readonly long T0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        public static GameState NewState(ulong seed = 7, long created = -1) =>
            GameState.NewProfile(Db, new Guid(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11), seed, created < 0 ? T0 : created);

        public static Simulation NewSim(ulong seed = 7, IEventSink sink = null, long created = -1) =>
            new Simulation(Db, NewState(seed, created), sink);

        public static string Dump(GameState s) => JsonSerializer.Serialize(s);
    }

    /// <summary>Returns a scripted sequence of doubles, repeating the pattern.</summary>
    internal sealed class ScriptedRandom : IRandomSource
    {
        readonly double[] _pattern;
        public int Draws;

        public ScriptedRandom(params double[] pattern) { _pattern = pattern; }

        public Func<int, double> Override;   // draw index -> value, or NaN to fall back to the pattern

        public double NextDouble()
        {
            int i = Draws++;
            if (Override != null)
            {
                double v = Override(i);
                if (!double.IsNaN(v)) return v;
            }
            return _pattern[i % _pattern.Length];
        }
    }

    /// <summary>The reference spend policy (specs/04 section 9), one call per active minute.</summary>
    internal static class GreedyBot
    {
        public static void Spend(Simulation sim)
        {
            sim.BuyStatLevelsMax();
            int slot = sim.Query.WeakestGearSlot();
            if (slot >= 0 && sim.State.Gear.Slots[slot].Enhance < sim.Content.Constants.EnhMax &&
                sim.State.Currencies.Gold >= sim.Query.EnhanceCost)
                sim.Enhance(slot);
            if (sim.Query.IsChestReady) sim.ClaimIdleChest();
            while (sim.Query.CanPull && sim.Pull().Ok) { }
            if (sim.Query.IsWalled && sim.Query.BossClearable) sim.AttemptBoss();
        }

        /// <summary>One simulated day: activeMinutes of ticking + spending, the rest offline. Clock stays in step.</summary>
        public static void PlayDay(Simulation sim, Time.FakeClock clock, int activeMinutes = 120)
        {
            int tpm = 60 * sim.Content.Constants.TickHz;
            for (int m = 0; m < activeMinutes; m++)
            {
                sim.Advance(tpm);
                clock.Advance(60);
                sim.SyncWallClock(clock);
                Spend(sim);
            }
            clock.Advance(86400 - activeMinutes * 60);
            sim.SyncWallClock(clock);
            Spend(sim);
        }
    }
}
