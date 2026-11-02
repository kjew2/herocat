using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HeroCat.Runtime.Replay;
using HeroCat.Runtime.Session;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Replay;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>The JSONL command log, its rotation and replay (01 section 2.6, AC 6; issue #67).</summary>
    public class CommandLogTests : TempDirTest
    {
        [Test]
        public void EveryCommandType_RoundTrips_ThroughOneJsonlLine()   // issue #67 acceptance 3
        {
            var types = CommandCodec.SimCommandTypes;
            Assert.That(types, Is.Not.Empty);
            foreach (var t in types)
            {
                var c = (Command)Sample(t, 0) with { IssuedAtTick = 4242 };
                string line = CommandCodec.Encode(c);
                Assert.That(line, Does.Not.Contain("\n"), t.Name);
                Assert.That(line, Does.StartWith("{\"tick\":4242,\"type\":\"" + CommandCodec.TagFor(t) + "\""), t.Name);
                var back = CommandCodec.Decode(line);
                Assert.That(back.GetType(), Is.EqualTo(t));
                Assert.That(back.IssuedAtTick, Is.EqualTo(4242));
                Assert.That(CommandCodec.Encode(back), Is.EqualTo(line), t.Name + " (records with arrays compare by reference, so compare lines)");
            }
        }

        [Test]
        public void TypeTags_AreStable_NotClrNames()
        {
            Assert.That(CommandCodec.TagFor(typeof(ApplyServerTime)), Is.EqualTo("ApplyServerTime"));
            // Every command the bots issue is pinned, not registered by type name on first use.
            foreach (var t in new[] { typeof(ClaimOfflineReward), typeof(TrainStat), typeof(ReleaseMax), typeof(MergeAll), typeof(MergeFish),
                                      typeof(DiscardFish), typeof(AttemptBoss), typeof(AutoEquipBestWeapon), typeof(AutoEquipBestSkills),
                                      typeof(ReleaseFish), typeof(MoveFish), typeof(LimitBreak), typeof(Awaken),
                                      typeof(ClaimQuest), typeof(ClaimAllQuests), typeof(Research),
                                      typeof(ClaimDailyGift), typeof(ExchangeItem),
                                      typeof(StartDungeon), typeof(SweepDungeon) })
                Assert.That(CommandCodec.RegisteredTypes, Does.Contain(t), t.Name);
            Assert.That(CommandCodec.Encode(new ApplyServerTime(123)), Does.Not.Contain("HeroCat."));
            Assert.Throws<FormatException>(() => CommandCodec.Decode("{\"tick\":1,\"type\":\"NoSuchCommand\"}"));
            Assert.Throws<FormatException>(() => CommandCodec.Decode("{\"tick\":1,\"type\":\"ApplyServerTime\",\"bogus\":1}"));
            Assert.Throws<FormatException>(() => CommandCodec.Decode("not json"));
        }

        [Test]
        public void AFishSession_ReplayedFromJsonl_HasTheSameStateHash()   // issue #103 acceptance 3, 05 section 12
        {
            var s = TestSession.Open();
            var memory = new MemoryJsonlCommandLog();
            s.Sim.CommandLog = new TeeLog(memory, s.Log);
            var cells = s.Sim.State.Fish.Cells;
            for (int round = 0; round < 6; round++)
            {
                s.Runner.Issue(new ReleaseFish());
                s.Runner.Issue(new ReleaseMax());
                s.Play(20);   // the tank refills on ticks
                int a = Array.FindIndex(cells, c => c > 0), b = Array.FindLastIndex(cells, c => c > 0);
                if (a >= 0 && b > a) s.Runner.Issue(new MergeFish(a, b));
                s.Runner.Issue(new MergeAll());
                int empty = Array.IndexOf(cells, 0), full = Array.FindIndex(cells, c => c > 0);
                if (empty >= 0 && full >= 0) s.Runner.Issue(new MoveFish(full, empty));
                if (round % 2 == 1 && full >= 0) s.Runner.Issue(new DiscardFish(Array.FindIndex(cells, c => c > 0)));
            }
            string live = s.Sim.StateHash();
            long endTick = s.Sim.Tick;
            var log = s.Log.Records.Take(2).Concat(memory.Commands()).ToList();
            foreach (var t in new[] { typeof(ReleaseFish), typeof(ReleaseMax), typeof(MergeFish), typeof(MergeAll), typeof(MoveFish), typeof(DiscardFish) })
                Assert.That(log.Any(c => c.GetType() == t), Is.True, t.Name + " is in the log");
            Assert.That(Replay(log, endTick), Is.EqualTo(live));
        }

        [Test]
        public void Rotation_KeepsTheFileUnderTheCap_DroppingWholeLinesFromTheHead()   // issue #67 acceptance 2
        {
            string path = Path.Combine(Dir, "saves", JsonlCommandLog.FileName);
            const int cap = 4096;
            long last = 0;
            using (var log = new JsonlCommandLog(path, cap))
            {
                for (long i = 1; i <= 2000; i++)
                {
                    log.Append(new ApplyServerTime(TestContent.T0 + i) { IssuedAtTick = i });
                    Assert.That(log.Length, Is.LessThanOrEqualTo(cap));
                    last = i;
                }
                Assert.That(log.Rotations, Is.GreaterThan(0));
                var kept = log.ReadAll();
                Assert.That(kept.Last().IssuedAtTick, Is.EqualTo(last), "the newest line survives");
                var ticks = kept.Select(c => c.IssuedAtTick).ToList();
                Assert.That(ticks, Is.EqualTo(Enumerable.Range((int)ticks[0], ticks.Count).Select(x => (long)x)), "a contiguous tail");
            }
            Assert.That(File.ReadAllText(path), Does.StartWith("{\"tick\":"), "the head is a whole line");
            Assert.That(JsonlCommandLog.Read(path).Count, Is.GreaterThan(0));
        }

        [Test]
        public void ReopeningTheLog_Appends()
        {
            string path = Path.Combine(Dir, JsonlCommandLog.FileName);
            using (var a = new JsonlCommandLog(path)) a.Append(new ApplyServerTime(1));
            using (var b = new JsonlCommandLog(path)) b.Append(new ApplyServerTime(2));
            Assert.That(JsonlCommandLog.Read(path).Cast<ApplyServerTime>().Select(c => c.ServerSeconds), Is.EqualTo(new long[] { 1, 2 }));
        }

        [Test]
        public void LiveSession_ReplayedFromJsonl_AndFromMemory_HasTheSameStateHash()   // 01 AC 6 (.NET half)
        {
            string path = Path.Combine(Dir, "saves", JsonlCommandLog.FileName);
            var memory = new MemoryJsonlCommandLog();
            string live;
            long endTick;
            using (var file = new JsonlCommandLog(path))
            {
                var s = TestSession.Open();
                s.Sim.CommandLog = new TeeLog(file, memory, s.Log);
                s.Play(150, 0.5);   // ticks, two WALL_CLOCK_SYNC_SEC re-anchors
                s.Runner.Issue(new ApplySummonResult(SamplePayload()));   // a summon payload is replayed, not re-rolled
                s.Runner.Issue(new ApplyInboxGrant(new InboxGrantPayload("iap_1", new[] { new GrantLine("currency", "DIAMOND", 10) }, CurrencyReason.IAP)));
                s.Play(30);
                s.Runner.OnPause();
                s.Clock.Advance(4000);
                s.Runner.OnResume();
                s.Play(10);
                live = s.Sim.StateHash();
                endTick = s.Sim.Tick;
                file.Dispose();
                // Boot's ApplyServerTime + ComputeOfflineCatchup ran before the tee; the in-memory ListCommandLog has them.
                var fromFile = s.Log.Records.Take(2).Concat(JsonlCommandLog.Read(path)).ToList();
                var fromMemory = s.Log.Records.Take(2).Concat(memory.Commands()).ToList();
                Assert.That(fromFile.Select(CommandCodec.Encode), Is.EqualTo(s.Log.Records.Select(CommandCodec.Encode)));
                Assert.That(fromFile.OfType<ApplyServerTime>().Count(), Is.GreaterThanOrEqualTo(3));

                Assert.That(Replay(fromFile, endTick), Is.EqualTo(live), "replayed from saves/cmdlog.jsonl");
                Assert.That(Replay(fromMemory, endTick), Is.EqualTo(live), "replayed from the bot harness's in-memory JSONL");
            }
        }

        static string Replay(System.Collections.Generic.IReadOnlyList<Command> log, long endTick)
        {
            var sim = new Simulation(TestContent.NewState(TestContent.T0), TestContent.Db);
            sim.Replay(log, endTick);
            return sim.StateHash();
        }

        static SummonResultPayload SamplePayload() =>
            new SummonResultPayload("0123456789abcdef0123456789abcdef", Banner.WEAPON, 1,
                                    new[] { new SummonPull("weapon_c_01", Rarity.COMMON, false, 1, 1, 2, 0, false) },
                                    1, 1, 10, new[] { new CurrencyDelta(Currency.DIAMOND, -100) }, null, 1, TestContent.T0);

        sealed class TeeLog : ICommandLog
        {
            readonly ICommandLog[] _logs;
            public TeeLog(params ICommandLog[] logs) { _logs = logs; }
            public void Append(Command c) { foreach (var l in _logs) l.Append(c); }
        }

        /// <summary>A sample value for every field shape a command can carry (records, arrays, enums, primitives).</summary>
        static object Sample(Type t, int depth)
        {
            if (t == typeof(string)) return "s_" + depth;
            if (t == typeof(long)) return 1234567890123L;
            if (t == typeof(int)) return 7;
            if (t == typeof(double)) return 1.5;
            if (t == typeof(bool)) return true;
            if (t == typeof(ulong)) return 0xfedcba9876543210UL;
            if (t == typeof(Guid)) return new Guid("00000000-0000-4000-8000-0000000000aa");
            var nullable = Nullable.GetUnderlyingType(t);
            if (nullable != null) return Sample(nullable, depth);
            if (t.IsEnum)
            {
                var values = Enum.GetValues(t);
                return values.GetValue(values.Length - 1);
            }
            if (t.IsArray)
            {
                var a = Array.CreateInstance(t.GetElementType(), 2);
                for (int i = 0; i < 2; i++) a.SetValue(Sample(t.GetElementType(), depth + 1), i);
                return a;
            }
            var ctor = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()
                       ?? throw new InvalidOperationException("no public constructor on " + t);
            var args = ctor.GetParameters().Select(p => Sample(p.ParameterType, depth + 1)).ToArray();
            return ctor.Invoke(args);
        }
    }
}
