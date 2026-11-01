using System.IO;
using System.Linq;
using HeroCat.Bots;
using HeroCat.Bots.Golden;
using HeroCat.Bots.Measure;
using HeroCat.Bots.Policies;
using HeroCat.Bots.Report;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Fish;
using HeroCat.Sim.Replay;
using NUnit.Framework;

namespace HeroCat.Bots.Tests
{
    /// <summary>05 section 8.2's three Fish Board policies, the fish day columns and the AC14 cross-check (issue #105).</summary>
    public class FishPolicyTests
    {
        sealed class Recording : ICommandLog
        {
            public readonly System.Collections.Generic.List<Command> Commands = new System.Collections.Generic.List<Command>();
            public void Append(Command c) => Commands.Add(c);
        }

        static (RunResult run, Recording log, BotContext ctx) Run(IBotPolicy p, int days)
        {
            var log = new Recording();
            BotContext last = null;
            var run = new BotRunner(Content.Db) { CommandLog = log, AfterDay = (d, c) => last = c }.Run(p, 7, days);
            return (run, log, last);
        }

        [Test]
        public void TheFishDayColumns_AddUpToTheStateCounters()   // 10 section 3.5, issue #105 acceptance 3
        {
            var (run, _, ctx) = Run(new FishOnlyBot(FishPolicy.Attentive), 3);
            var s = ctx.State;
            Assert.That(run.Rows.Sum(r => r.Fish.Released), Is.EqualTo(s.Stats.FishReleased));
            Assert.That(run.Rows.Sum(r => r.Fish.Merges), Is.EqualTo(s.Stats.Merges));
            Assert.That(run.Rows.Sum(r => r.Fish.Discards), Is.EqualTo(s.Stats.FishDiscards));
            Assert.That(run.Rows.Sum(r => r.Fish.CoinsBoard), Is.EqualTo(s.Wallet.FishCoin).Within(1e-6), "all Fish Coins came from the board");
            var last = run.Rows.Last().Fish;
            Assert.That(last.HighestLevel, Is.EqualTo(s.Fish.HighestLevel));
            Assert.That(last.BoardDpsPct, Is.EqualTo(FishBoard.BoardDpsPct(s, Content.Db)));
            Assert.That(last.FishMult, Is.GreaterThan(1.0));
        }

        [Test]
        public void Lazy_ReleasesAndMergesOncePerLogin_AndNeverDiscards()   // 05 section 8.2, issue #105 acceptance 2
        {
            var (run, log, ctx) = Run(new FishOnlyBot(FishPolicy.Lazy), 2);
            int perLogin = FishBoard.TankCap(ctx.State, Content.Db);
            foreach (var r in run.Rows)
            {
                Assert.That(r.Fish.Released, Is.EqualTo(HarnessConstants.SessionsPerDay * perLogin), "a full tank per login");
                Assert.That(r.Fish.Discards, Is.EqualTo(0));
            }
            Assert.That(log.Commands.OfType<DiscardFish>(), Is.Empty);
            Assert.That(log.Commands.OfType<MergeFish>(), Is.Empty, "bulk commands only");
            Assert.That(log.Commands.OfType<ReleaseFish>(), Is.Empty);
        }

        [Test]
        public void Never_IssuesNoBoardCommand()
        {
            var (run, log, _) = Run(new FishOnlyBot(FishPolicy.Never), 1);
            Assert.That(log.Commands.Any(c => c is ReleaseMax || c is MergeAll || c is MergeFish || c is DiscardFish || c is ReleaseFish), Is.False);
            Assert.That(run.Rows.Single().Fish.FishMult, Is.EqualTo(1.0));
        }

        [Test]
        public void Attentive_OutpacesLazy_AndGreedyPlaysAttentive()   // 05 section 8.2, 03 section 8.3 step 5
        {
            var attentive = Run(new FishOnlyBot(FishPolicy.Attentive), 2).run.Rows.Last().Fish;
            var lazy = Run(new FishOnlyBot(FishPolicy.Lazy), 2).run.Rows.Last().Fish;
            Assert.That(attentive.Released, Is.GreaterThan(10 * lazy.Released));
            Assert.That(attentive.FishMult, Is.GreaterThan(lazy.FishMult));
            Assert.That(new GreedyF2PBot().Fish, Is.EqualTo(FishPolicy.Attentive));
            Assert.That(BotPolicies.Create("fish-lazy").Name, Is.EqualTo("fish-lazy"));
        }

        [Test]
        public void FishCheck_FailsWithoutTheModelOutput_AndComparesItsFishline()   // 05 AC14
        {
            var missing = FishCheck.Run(Content.Db, Path.Combine(Path.GetTempPath(), "no-such-fish-model.json"), 7, FishCheck.DefaultTolerance);
            Assert.That(missing.Ok, Is.False);
            string path = Path.Combine(Path.GetTempPath(), "herocat-fish-" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{\"fishline\": {\"attentive_day7\": " + missing.Attentive.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                                        ", \"lazy_day7\": 1.5}}");
                Assert.That(FishCheck.Run(Content.Db, path, 7, FishCheck.DefaultTolerance).Ok, Is.True);
                File.WriteAllText(path, "{\"fishline\": {\"attentive_day7\": " + (missing.Attentive * 1.2).ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                                        ", \"lazy_day7\": 1.5}}");
                Assert.That(FishCheck.Run(Content.Db, path, 7, FishCheck.DefaultTolerance).Ok, Is.False, "20% apart");
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void TheBoardVectors_AreDeterministic_AndKeepTheInvariants()   // 05 AC5, issue #103
        {
            string a = FishBoardVectors.Generate(Content.Db);
            Assert.That(FishBoardVectors.Generate(Content.Db), Is.EqualTo(a));
            foreach (var cmd in new[] { "ReleaseFish", "ReleaseMax", "MergeFish", "MergeAll", "MoveFish", "DiscardFish", "ComputeOfflineCatchup" })
                Assert.That(a.Split('\n').Any(l => l.Contains("\"type\":\"" + cmd + "\"") && l.Split(new[] { " | " }, System.StringSplitOptions.None)[2] == "ok"),
                            Is.True, cmd + " succeeds at least once");
        }
    }
}
