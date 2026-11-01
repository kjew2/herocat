using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HeroCat.Client.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests.Client
{
    /// <summary>The player build's content bundle and asset-address check (09 sections 3.2 and 10.1; issue #133).</summary>
    public class ContentBundleTests : TempDirTest
    {
        [Test]
        public void Mirror_CopiesTheTables_AndDropsStaleOnesWithTheirMeta()
        {
            string dst = Path.Combine(Dir, "StreamingAssets", "content");
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(dst, "old_table.json"), "{}");
            File.WriteAllText(Path.Combine(dst, "old_table.json.meta"), "guid");
            File.WriteAllText(Path.Combine(dst, "weapons.json"), "stale");
            int n = ContentBundle.Mirror(TestContent.Dir, dst);
            var src = Directory.GetFiles(TestContent.Dir, "*.json").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.That(n, Is.EqualTo(src.Count));
            Assert.That(Directory.GetFiles(dst).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal), Is.EqualTo(src));
            foreach (var f in src)
                Assert.That(File.ReadAllBytes(Path.Combine(dst, f)), Is.EqualTo(File.ReadAllBytes(Path.Combine(TestContent.Dir, f))), f);
            // The copy is a loadable bundle (manifest and sha256 verified).
            Assert.That(HeroCat.Runtime.Content.ContentLoader.LoadFromDirectory(dst, HeroCat.Sim.Content.Tier.M0).Version, Is.EqualTo(TestContent.Db.Version));
        }

        [Test]
        public void TheAssetColumns_MirrorTheExporterSchema()   // 09 section 3.2: tools/export_content.py's SCHEMA
        {
            string py = File.ReadAllText(Path.Combine(TestContent.RepoRoot, "tools", "export_content.py"));
            int start = py.IndexOf("SCHEMA = {", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThan(0), "export_content.py has a SCHEMA dict");
            var expected = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            string table = null;
            foreach (var line in py.Substring(start).Split('\n').Skip(1))
            {
                if (line.StartsWith("}", StringComparison.Ordinal)) break;
                var t = Regex.Match(line, "^    \"(\\w+)\": T\\(");
                if (t.Success) table = t.Groups[1].Value;
                foreach (Match m in Regex.Matches(line, "c\\(\"(\\w+)\", \"asset\""))
                {
                    if (!expected.TryGetValue(table, out var cols)) expected[table] = cols = new List<string>();
                    cols.Add(m.Groups[1].Value);
                }
            }
            Assert.That(expected, Is.Not.Empty);
            Assert.That(ContentAssets.Columns.Keys.OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(expected.Keys.OrderBy(k => k, StringComparer.Ordinal)));
            foreach (var kv in expected) Assert.That(ContentAssets.Columns[kv.Key], Is.EqualTo(kv.Value), kv.Key);
        }

        [Test]
        public void Missing_ListsEveryUnresolvedAddress_AndSkipsNullCells()
        {
            var all = ContentAssets.Missing(TestContent.Dir, _ => false);
            Assert.That(all, Is.Not.Empty);
            Assert.That(all.Any(m => m.Table == "weapons" && m.Column == "icon"), Is.True);
            Assert.That(all.All(m => Regex.IsMatch(m.Address, "^[a-z][a-z0-9_]{0,63}$")), Is.True, "09's address pattern");
            Assert.That(ContentAssets.Missing(TestContent.Dir, _ => true), Is.Empty);

            // One missing address is reported with its table, row and column; a null optional cell is not.
            string dir = Path.Combine(Dir, "content");
            Directory.CreateDirectory(dir);
            var doc = new JObject
            {
                ["table"] = "bosses",
                ["rows"] = new JArray(new JObject { ["id"] = "boss_1", ["sprite"] = "spr_boss_1", ["anim_set"] = "anim_boss", ["intro_vfx"] = null }),
            };
            File.WriteAllText(Path.Combine(dir, "bosses.json"), doc.ToString());
            var one = ContentAssets.Missing(dir, a => a != "anim_boss");
            Assert.That(one.Select(m => m.ToString()), Is.EqualTo(new[] { "bosses[boss_1].anim_set = anim_boss" }));
        }
    }
}
