using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HeroCat.Sim.Content;
using HeroCat.Sim.Content.Dto;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>Strings never enter HeroCat.Sim (09 principle 5 and section 13.4, 00 contract 10; issue #129).</summary>
    public class LocalizationBoundaryTests
    {
        [Test]
        public void ContentDtos_CarryNoLocalization()
        {
            var fields = typeof(ContentDtos).GetFields().Select(f => f.Name).ToArray();
            Assert.That(fields, Has.None.Contains("Locali"));
            Assert.That(fields, Has.None.EqualTo("Strings"));
        }

        [Test]
        public void ContentDb_ExposesNoStringTable()
        {
            var members = typeof(ContentDb).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            Assert.That(members.Select(m => m.Name), Has.None.EqualTo("Text").And.None.EqualTo("Strings").And.None.Contains("Locali"));
            foreach (var p in typeof(ContentDb).GetProperties())
                Assert.That(p.PropertyType, Is.Not.AssignableTo<IReadOnlyDictionary<string, string>>(), p.Name);
        }

        [Test]
        public void SimSources_NeverNameALocalizationFile()   // the grep half
        {
            string sim = Path.Combine(TestContent.RepoRoot, "src", "HeroCat.Sim");
            var hits = Directory.GetFiles(sim, "*.cs", SearchOption.AllDirectories)
                                .Where(f => File.ReadAllText(f).Contains("\"localization", StringComparison.Ordinal))
                                .Select(f => Path.GetRelativePath(sim, f)).ToArray();
            Assert.That(hits, Is.Empty);
        }
    }
}
