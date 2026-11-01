using System;
using System.Linq;
using System.Text;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using HeroCat.Sim.Time;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    public class Sha256Tests
    {
        [Test]
        public void KnownVectors()
        {
            Assert.That(Sha256.Hex(Sha256.Compute(Array.Empty<byte>())),
                Is.EqualTo("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));
            Assert.That(Sha256.Hex(Sha256.Compute(Encoding.ASCII.GetBytes("abc"))),
                Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        }

        [Test]
        public void MatchesPlatformSha256_AcrossBlockBoundaries()
        {
            var rnd = new Random(1);
            using var sys = System.Security.Cryptography.SHA256.Create();
            foreach (int n in new[] { 1, 55, 56, 63, 64, 65, 119, 120, 128, 1000, 4097 })
            {
                var data = new byte[n];
                rnd.NextBytes(data);
                Assert.That(Sha256.Compute(data), Is.EqualTo(sys.ComputeHash(data)), $"length {n}");
            }
        }
    }

    public class CommandLogTests
    {
        /// <summary>A debug-assisted run that exercises every command kind, recorded into <paramref name="log"/>.</summary>
        static Simulation RecordedRun(ListCommandLog log, int days = 2)
        {
            var sim = TestContent.NewSim();
            sim.CommandLog = log;
            var clock = new FakeClock(TestContent.T0);
            sim.SyncWallClock(clock);
            sim.ApplyTutorialGrants();
            sim.Debug.GrantGold(12345.678);
            sim.Debug.GrantGems(5000);
            sim.Pull10();
            var hero = TestContent.Db.HeroList[0].Id;
            sim.Debug.GrantHeroCopies(hero, 2);
            sim.SetAutoEquipHeroes(false);
            sim.UnequipHero(0);
            sim.EquipHero(0, hero);
            sim.AutoEquipBestHeroes();
            for (int d = 0; d < days; d++) GreedyBot.PlayDay(sim, clock, activeMinutes: 30);
            sim.Advance(1234);
            sim.EnsureDay(clock.NowServerSeconds);
            sim.ApplyOfflineCatchUp(60);
            sim.Debug.SetStatLevel(sim.State.Progression.StatLevel + 1);
            return sim;
        }

        static Simulation Genesis() => TestContent.NewSim();

        [Test]
        public void DefaultLogIsNull_AndTicksAreNotLogged()
        {
            var sim = TestContent.NewSim();
            Assert.That(sim.CommandLog, Is.SameAs(NullCommandLog.Instance));
            var log = new ListCommandLog();
            sim.CommandLog = log;
            sim.Advance(5000);
            Assert.That(log.Count, Is.EqualTo(0));
            sim.CommandLog = null;
            Assert.That(sim.CommandLog, Is.SameAs(NullCommandLog.Instance));
        }

        [Test]
        public void RecordedRun_ReplaysToIdenticalStateHash()
        {
            var log = new ListCommandLog();
            var sim = RecordedRun(log);
            var kinds = log.Records.Select(r => r.Kind).Distinct().ToList();
            foreach (var k in new[] { CommandKind.BuyStatLevels, CommandKind.Enhance, CommandKind.EquipHero, CommandKind.UnequipHero,
                                      CommandKind.AutoEquipBest, CommandKind.Pull, CommandKind.Pull10, CommandKind.ClaimChest,
                                      CommandKind.SyncWallClock, CommandKind.Debug, CommandKind.TutorialGrants,
                                      CommandKind.OfflineCatchUp, CommandKind.EnsureDay })
                Assert.That(kinds, Does.Contain(k));

            var replay = Genesis();
            var relog = new ListCommandLog();
            replay.CommandLog = relog;
            replay.Replay(log.ToArray(), null, sim.State.Tick);
            Assert.That(replay.StateHash(), Is.EqualTo(sim.StateHash()));
            Assert.That(relog.ToArray(), Is.EqualTo(log.ToArray()), "replaying re-records the same log");
            Assert.That(TestContent.Dump(replay.State), Is.EqualTo(TestContent.Dump(sim.State)));
        }

        [Test]
        public void TamperedRecord_ChangesHash()
        {
            var log = new ListCommandLog();
            var sim = RecordedRun(log);
            string expected = sim.StateHash();
            var records = log.ToArray();

            int i = Array.FindIndex(records, r => r.Kind == CommandKind.BuyStatLevels);
            var t1 = (CommandRecord[])records.Clone();
            t1[i] = t1[i].WithArgs(t1[i].A - 1, 0);   // one stat level fewer
            Assert.That(ReplayHash(t1, sim.State.Tick), Is.Not.EqualTo(expected));

            int j = Array.FindIndex(records, r => r.Kind == CommandKind.Debug && (r.A & 0xFF) == (long)DebugOp.GrantGems);
            var t2 = (CommandRecord[])records.Clone();
            t2[j] = t2[j].WithArgs(t2[j].A, t2[j].B + 1);   // one more gem
            Assert.That(ReplayHash(t2, sim.State.Tick), Is.Not.EqualTo(expected));

            var t3 = records.Where((r, k) => k != Array.FindIndex(records, x => x.Kind == CommandKind.Pull)).ToArray();
            Assert.That(ReplayHash(t3, sim.State.Tick), Is.Not.EqualTo(expected));

            Assert.That(ReplayHash(records, sim.State.Tick), Is.EqualTo(expected));
        }

        static string ReplayHash(CommandRecord[] log, long toTick)
        {
            var s = Genesis();
            s.Replay(log, null, toTick);
            return s.StateHash();
        }

        [Test]
        public void TamperedStateField_ChangesHash()
        {
            var sim = RecordedRun(new ListCommandLog(), days: 1);
            string h = sim.StateHash();
            Func<GameState, string> hash = StateHasher.Hash;

            var mutations = new Action<GameState>[]
            {
                s => s.Currencies.Gold = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(s.Currencies.Gold) + 1),   // last bit
                s => s.Currencies.Gems += 1,
                s => s.Stage.Stage += 1,
                s => s.Tick += 1,
                s => s.Gear.Slots[Array.FindIndex(s.Gear.Slots, g => g != null)] = s.Gear.Slots.First(g => g != null) with { Enhance = 99 },
                s => s.Heroes.Copies[s.Heroes.Copies.Keys.First()] += 1,
                s => s.Rng.Streams[StreamNames.Enhance].Counter += 1,
                s => s.Lifetime.Kills += 1,
                s => s.Combat.EnemyHp += 1,
                s => s.Daily.Progress[0] += 1,
                s => s.Heroes.Equipped[0] = null,
            };
            foreach (var m in mutations)
            {
                var c = sim.State.DeepClone();
                Assert.That(hash(c), Is.EqualTo(h));
                m(c);
                Assert.That(hash(c), Is.Not.EqualTo(h));
            }
        }

        [Test]
        public void Hash_IndependentOfMapInsertionOrder_AndExcludesBookkeeping()
        {
            var a = TestContent.NewState();
            var b = TestContent.NewState();
            a.Heroes.Copies["x"] = 1; a.Heroes.Copies["a"] = 2;
            b.Heroes.Copies["a"] = 2; b.Heroes.Copies["x"] = 1;
            var streams = b.Rng.Streams.Reverse().ToList();
            b.Rng.Streams.Clear();
            foreach (var kv in streams) b.Rng.Streams[kv.Key] = kv.Value;
            Assert.That(StateHasher.Hash(b), Is.EqualTo(StateHasher.Hash(a)));
            b.ContentVersion = "other";
            Assert.That(StateHasher.Hash(b), Is.EqualTo(StateHasher.Hash(a)));
            Assert.That(StateHasher.Hash(a), Has.Length.EqualTo(64));
        }

        [Test]
        public void Codec_RoundTrips()
        {
            var log = new ListCommandLog();
            RecordedRun(log, days: 1);
            log.Append(new CommandRecord(99, CommandKind.GrantFromLedger, 1, 2, "ledger-é-42"));
            var bytes = CommandLogCodec.Encode(log.Records);
            Assert.That(CommandLogCodec.Decode(bytes), Is.EqualTo(log.ToArray()));
            Assert.Throws<FormatException>(() => CommandLogCodec.Decode(bytes.Take(bytes.Length - 1).ToArray()));
            Assert.Throws<FormatException>(() => CommandLogCodec.Decode(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void Replay_RejectsBadLogs()
        {
            var sim = Genesis();
            sim.Advance(100);
            Assert.Throws<ReplayException>(() => sim.Replay(new[] { new CommandRecord(50, CommandKind.Pull) }, null));
            Assert.Throws<ReplayException>(() => Genesis().Replay(new[] { new CommandRecord(0, CommandKind.GrantFromLedger) }, null));
            Assert.Throws<ReplayException>(() => Genesis().Replay(new[] { new CommandRecord(0, CommandKind.EquipHero, 0, 9999) }, null));

            var sync = new[] { new CommandRecord(10, CommandKind.SyncWallClock, TestContent.T0 + 1, 1) };
            Assert.Throws<ReplayException>(() => Genesis().Replay(sync, new[] { new SignedTime(TestContent.T0 + 2, "sig") }));
            Assert.DoesNotThrow(() => Genesis().Replay(sync, new[] { new SignedTime(TestContent.T0 + 1, "sig") }));
        }

        [Test]
        public void BoundedLog_StopsAtCapacity()
        {
            var log = new ListCommandLog(capacity: 3);
            var sim = TestContent.NewSim();
            sim.CommandLog = log;
            for (int i = 0; i < 5; i++) sim.AttemptBoss();
            Assert.That(log.Count, Is.EqualTo(3));
            Assert.That(log.Overflowed, Is.True);
            log.Clear();
            Assert.That(log.Overflowed, Is.False);
        }
    }
}
