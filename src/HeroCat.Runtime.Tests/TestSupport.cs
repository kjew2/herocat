using System;
using System.IO;
using HeroCat.Runtime.Save;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    internal static class TestContent
    {
        static readonly Lazy<ContentDb> _db = new Lazy<ContentDb>(() =>
        {
            string dir = FileContentSource.FindContentDirectory(TestContext.CurrentContext.TestDirectory)
                         ?? throw new InvalidOperationException("content/manifest.json not found above the test directory");
            return ContentDb.LoadFromDirectory(dir);
        });

        public static ContentDb Db => _db.Value;

        /// <summary>2026-01-01T00:00:00Z.</summary>
        public static readonly long T0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        public static readonly Guid ProfileId = new Guid(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);

        public static Simulation NewSim(ulong seed = 7) =>
            new Simulation(Db, GameState.NewProfile(Db, ProfileId, seed, T0));

        /// <summary>Canonical JSON of the state, for equality.</summary>
        public static string Dump(GameState s) => SaveSerializer.StateToJson(s);

        /// <summary>The reference spend policy (specs/04 section 9), trimmed.</summary>
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

        /// <summary>A sim that has done a bit of everything: pulls, kills, gear, enhancements.</summary>
        public static Simulation PlayedSim(ulong seed = 7)
        {
            var sim = NewSim(seed);
            sim.ApplyTutorialGrants();
            sim.SyncWallClock(T0, applyCatchUp: false);
            for (int i = 0; i < 10; i++) { sim.Advance(600); Spend(sim); }
            return sim;
        }
    }

    /// <summary>Fresh temp directory per test.</summary>
    public abstract class TempDirTest
    {
        protected string Dir;

        [SetUp]
        public void MakeDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), "herocat-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }

        [TearDown]
        public void RemoveDir()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }
}
