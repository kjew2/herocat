using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>
    /// 00 contract 1 and 09 principle 5 as source greps over src/HeroCat.Sim (01 AC 1's list). check.sh runs the same rule
    /// as tools/check_sim_purity.py (issue #63), next to tools/check_clock_apis.py for 02 AC 18's Runtime clock rule.
    /// </summary>
    public class PurityTests
    {
        static readonly string[] Banned =
        {
            "UnityEngine", "System.IO", "System.Net", "Newtonsoft", "System.Reflection", "DateTime.Now", "DateTime.UtcNow",
            "DateTimeOffset.Now", "DateTimeOffset.UtcNow", "Stopwatch", "Math.Pow", "Math.Exp", "Math.Log",
        };

        static string[] SimSources() =>
            Directory.GetFiles(Path.Combine(TestContent.RepoRoot, "src", "HeroCat.Sim"), "*.cs", SearchOption.AllDirectories);

        [Test]
        public void Sim_ReferencesNoBannedApi()
        {
            var hits = (from f in SimSources()
                        from line in File.ReadAllLines(f).Select((text, i) => (text, i))
                        from b in Banned
                        where line.text.Contains(b) && !line.text.TrimStart().StartsWith("//") && !line.text.TrimStart().StartsWith("///")
                        select $"{Path.GetFileName(f)}:{line.i + 1}: {b}").ToList();
            Assert.That(hits, Is.Empty);
        }

        [Test]
        public void SimAssembly_ReferencesOnlyNetstandard()   // 01 AC 1: the built HeroCat.Sim.dll (Release in check.sh)
        {
            var refs = typeof(Simulation).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
            Assert.That(refs, Is.EqualTo(new[] { "netstandard" }));
        }

        [Test]
        public void OnlyTheLedger_WritesABalance()  // issue #106: no sim code writes a balance except through Apply
        {
            var assign = new Regex(@"Wallet\.(Gold|Diamond|FishCoin|RockCandy|GuildCoin)\s*([+\-*/]?=)(?!=)|\.(DungeonTicket|SummonTicket)\[[^\]]+\]\s*[+\-]?=(?!=)");
            var hits = (from f in SimSources()
                        where !f.EndsWith("Ledger.cs") && !f.EndsWith("GameState.cs") && !f.EndsWith("StateCopy.cs")
                        from line in File.ReadAllLines(f).Select((text, i) => (text, i))
                        where assign.IsMatch(line.text)
                        select $"{Path.GetFileName(f)}:{line.i + 1}").ToList();
            Assert.That(hits, Is.Empty);
        }
    }
}
