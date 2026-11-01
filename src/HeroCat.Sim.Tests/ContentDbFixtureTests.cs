using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HeroCat.Runtime.Content;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>
    /// 09 acceptance 6 (C# half), 7 and 20 (issue #126): every loader-scope fixture in tests/content_fixtures/ (those with
    /// "csharp": true) makes ContentDb.Build report exactly the exporter's first line(s), doubles included.
    /// The JSON comes from `tools/content_fixtures.py emit`, which builds each fixture with the exporter's fixture-only
    /// --emit-invalid so the defect survives into the files; the exporter checks its own output against expected.txt
    /// first. Parse-level codes (E001-E011, E021, E070, E071, E094) cannot survive into JSON and are exporter-only, as
    /// are E07x and E09x (09 section 8.6 loader scope).
    /// </summary>
    public class ContentDbFixtureTests
    {
        static string _out;

        [OneTimeSetUp]
        public void EmitFixtures()
        {
            _out = Path.Combine(Path.GetTempPath(), "herocat-content-fixtures-" + Guid.NewGuid().ToString("N"));
            var psi = new ProcessStartInfo("python3", $"tools/content_fixtures.py emit --out \"{_out}\"")
            {
                WorkingDirectory = TestContent.RepoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd(), stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.That(p.ExitCode, Is.EqualTo(0), "content_fixtures.py emit failed (python3 is required, as for check.sh):\n" + stdout + stderr);
            }
        }

        [OneTimeTearDown]
        public void Cleanup()
        {
            if (_out != null && Directory.Exists(_out)) Directory.Delete(_out, true);
        }

        static IEnumerable<string> Fixtures() =>
            Directory.GetDirectories(_out).Select(Path.GetFileName).Where(n => n != "good_minimal").OrderBy(n => n, StringComparer.Ordinal);

        static string[] FirstLines(string name, int n)
        {
            var dtos = ContentLoader.LoadDtos(new FileContentSource(Path.Combine(_out, name)));
            var e = Assert.Throws<ContentBuildException>(() => ContentDb.Build(dtos, Tier.MVP), name);
            return e.Errors.Take(n).Select(x => x.ToString()).ToArray();
        }

        [Test]
        public void GoodMinimal_Builds_AtM0_AndMvp()
        {
            var dtos = ContentLoader.LoadDtos(new FileContentSource(Path.Combine(_out, "good_minimal")));
            Assert.DoesNotThrow(() => ContentDb.Build(dtos, Tier.M0));
            Assert.DoesNotThrow(() => ContentDb.Build(dtos, Tier.MVP));
        }

        [Test]
        public void EveryLoaderScopeFixture_ReportsTheExportersFirstLine()   // acceptance 6, 20
        {
            var names = Fixtures().ToList();
            Assert.That(names.Count, Is.GreaterThanOrEqualTo(30), "the csharp fixtures were emitted");
            var failures = new List<string>();
            foreach (var name in names)
            {
                var want = File.ReadAllLines(Path.Combine(_out, name, "expected.txt")).Where(l => l.Length > 0).ToArray();
                string[] got;
                try { got = FirstLines(name, want.Length); }
                catch (Exception ex) { failures.Add($"{name}: {ex.GetType().Name} {ex.Message}"); continue; }
                if (!got.SequenceEqual(want)) failures.Add($"{name}:\n  want {string.Join("\n       ", want)}\n  got  {string.Join("\n       ", got)}");
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        [Test]
        public void ThreeDefects_AreAllReported_InTableOrder()   // acceptance 7
        {
            var got = FirstLines("three_defects", 3).Select(l => l.Split(' ')[1]).ToArray();
            Assert.That(got, Is.EqualTo(new[] { "E043", "E030", "E061" }));
        }

        [Test]
        public void ShippedContent_PassesEveryLoaderRule()   // acceptance 2
        {
            var dtos = ContentLoader.LoadDtos(new FileContentSource(TestContent.Dir));
            Assert.DoesNotThrow(() => ContentDb.Build(dtos, Tier.M0));
            Assert.DoesNotThrow(() => ContentDb.Build(dtos, Tier.MVP));
        }

        [TestCase(10.0, "10.0")]
        [TestCase(0.1, "0.1")]
        [TestCase(1.0099999999999998, "1.0099999999999998")]
        [TestCase(1e-05, "1e-05")]
        [TestCase(0.0001, "0.0001")]
        [TestCase(1e16, "1e+16")]
        [TestCase(1234567890123456.0, "1234567890123456.0")]
        [TestCase(1.5e+300, "1.5e+300")]
        [TestCase(-2.5e-07, "-2.5e-07")]
        [TestCase(0.0, "0.0")]
        [TestCase(16.666666666666664, "16.666666666666664")]
        public void Doubles_PrintLikePythonRepr(double v, string repr)   // 09 section 8.1, acceptance 20
        {
            Assert.That(ContentDb.FormatDouble(v), Is.EqualTo(repr));
        }
    }
}
