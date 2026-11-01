using System;
using System.IO;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Profile;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    internal static class TestContent
    {
        static readonly Lazy<string> _dir = new Lazy<string>(() =>
            FileContentSource.FindContentDirectory(TestContext.CurrentContext.TestDirectory)
            ?? throw new InvalidOperationException("content/manifest.json not found above the test directory"));

        static readonly Lazy<ContentDb> _db = new Lazy<ContentDb>(() => ContentLoader.LoadFromDirectory(_dir.Value, Tier.M0));

        public static string Dir => _dir.Value;
        public static string RepoRoot => Path.GetDirectoryName(_dir.Value);
        public static ContentDb Db => _db.Value;

        /// <summary>2026-01-01T00:00:00Z.</summary>
        public static readonly long T0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        public static readonly Guid Root = new Guid("7e57a7e0-0000-4000-8000-000000000007");
        public static readonly Guid Device = new Guid("de71ce00-0000-4000-8000-000000000001");

        /// <summary>A LocalSaveStore over <paramref name="slots"/> with this test install's id.</summary>
        public static HeroCat.Runtime.Save.LocalSaveStore Store(HeroCat.Runtime.Save.ISaveSlotStore slots, HeroCat.Runtime.Services.IClock clock = null,
                                                                HeroCat.Runtime.Services.IAnalytics analytics = null) =>
            new HeroCat.Runtime.Save.LocalSaveStore(slots, clock ?? new HeroCat.Runtime.Services.FakeClock(T0 + 100), Device, analytics)
            {
                NewProfileFactory = (db, now) => ProfileFactory.Create(db, Guid.Empty, now, Device, Root),
            };

        public static GameState NewState(long now = 0) => ProfileFactory.Create(Db, Guid.Empty, now == 0 ? T0 : now, Device, Root);

        /// <summary>A sim on a new profile, anchored at T0 and ticked a little.</summary>
        public static Simulation PlayedSim()
        {
            var sim = new Simulation(NewState(), Db);
            sim.Apply(new ApplyServerTime(T0));
            sim.Advance(600);
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
