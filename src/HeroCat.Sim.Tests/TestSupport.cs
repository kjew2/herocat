using System;
using System.IO;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Profile;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    internal static class TestContent
    {
        static readonly Lazy<string> _dir = new Lazy<string>(() =>
            FileContentSource.FindContentDirectory(TestContext.CurrentContext.TestDirectory)
            ?? throw new InvalidOperationException("content/manifest.json not found above the test directory"));

        static readonly Lazy<ContentDb> _m0 = new Lazy<ContentDb>(() => ContentLoader.LoadFromDirectory(_dir.Value, Tier.M0));
        static readonly Lazy<ContentDb> _mvp = new Lazy<ContentDb>(() => ContentLoader.LoadFromDirectory(_dir.Value, Tier.MVP));

        public static string Dir => _dir.Value;
        public static string RepoRoot => Path.GetDirectoryName(_dir.Value);
        public static ContentDb Db => _m0.Value;
        public static ContentDb Mvp => _mvp.Value;

        /// <summary>2026-01-01T00:00:00Z, a Thursday.</summary>
        public static readonly long T0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        public static readonly Guid Root = new Guid("7e57a7e0-0000-4000-8000-000000000007");

        public static GameState NewState(long now = -1) =>
            ProfileFactory.Create(Db, Guid.Empty, now < 0 ? T0 : now, new Guid("de71ce00-0000-4000-8000-000000000001"), Root);

        /// <summary>A sim on a fresh profile; when <paramref name="sync"/>, anchored at <paramref name="now"/> (rolls day 1).</summary>
        public static Simulation NewSim(long now = -1, bool sync = true)
        {
            long t = now < 0 ? T0 : now;
            var sim = new Simulation(NewState(t), Db);
            if (sync) sim.Apply(new ApplyServerTime(t));
            return sim;
        }
    }
}
