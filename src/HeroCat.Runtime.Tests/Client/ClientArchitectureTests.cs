using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>
    /// 07 sections 3 and 17.1, 00 contract 8 (issue #109): no screen reads GameState or holds the Simulation; only Core's
    /// GameViewModel does. The Unity-only Screens/ are not compiled here, so this is a source scan.
    /// </summary>
    public class ClientArchitectureTests
    {
        static string ClientDir => Path.Combine(TestContent.RepoRoot, "unity", "HeroCatClient", "Assets", "HeroCat.Client");

        [Test]
        public void NoScreenReadsGameState_OrHoldsTheSimulation()
        {
            var bad = new List<string>();
            var rx = new Regex(@"\bGameState\b|\bSimulation\b|\.Sim\.State\b|HeroCat\.Sim\.State", RegexOptions.Compiled);
            foreach (var f in Directory.GetFiles(Path.Combine(ClientDir, "Screens"), "*.cs", SearchOption.AllDirectories))
            {
                int n = 0;
                foreach (var line in File.ReadLines(f))
                {
                    n++;
                    string code = line.TrimStart();
                    if (code.StartsWith("//") || code.StartsWith("///")) continue;
                    if (rx.IsMatch(code)) bad.Add(Path.GetFileName(f) + ":" + n + ": " + code);
                }
            }
            Assert.That(bad, Is.Empty);
        }

        [Test]
        public void TheClientNeverFormatsAGameNumberOutsideTextFmt()
        {
            var bad = new List<string>();
            var rx = new Regex(@"ToString\(""(F|N|0\.)", RegexOptions.Compiled);
            foreach (var dir in new[] { "Screens", "Core" })
                foreach (var f in Directory.GetFiles(Path.Combine(ClientDir, dir), "*.cs", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(f) == "TextFmt.cs" || Path.GetFileName(f) == "DebugPanel.cs") continue;
                    int n = 0;
                    foreach (var line in File.ReadLines(f))
                    {
                        n++;
                        if (rx.IsMatch(line)) bad.Add(Path.GetFileName(f) + ":" + n);
                    }
                }
            Assert.That(bad, Is.Empty, "07 section 7: numbers go through BigNumber / TextFmt (issue #112)");
        }
    }
}
