using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using HeroCat.Runtime.Content;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>09 sections 6.2 and 8.5 (E900-E903), acceptance 2, 8, 16 (bundled half), 18 (issue #127).</summary>
    public class ContentLoaderTests
    {
        static MemoryContentSource Copy() => MemoryContentSource.CopyOf(new FileContentSource(TestContent.Dir));

        static string[] Codes(ContentBuildException e) => e.Errors.Select(x => x.Code).ToArray();

        [Test]
        public void Bundle_Builds_AtM0_AndMvp_WithTheManifestVersionAndHash()
        {
            var dtos = ContentLoader.LoadDtos(new FileContentSource(TestContent.Dir));
            var mvp = ContentDb.Build(dtos, Tier.MVP);
            var m0 = ContentDb.Build(dtos, Tier.M0);
            Assert.That(mvp.Version, Is.EqualTo(dtos.Manifest.ContentVersion));
            Assert.That(mvp.ContentHash, Is.EqualTo(dtos.Manifest.ContentHash));
            Assert.That(m0.Banners.Values.Count(b => b.Enabled), Is.EqualTo(3));
            Assert.That(m0.Banners[Banner.COMPANION].Enabled, Is.True, "companions are M0 (00 decision 107)");
            Assert.That(mvp.Banners[Banner.COMPANION].Enabled, Is.True);
            Assert.That(m0.Awakening, Is.Empty, "awakening rows are MVP");
            Assert.That(mvp.Awakening, Has.Count.EqualTo(5));
            Assert.That(m0.TutorialSteps, Has.Count.EqualTo(1), "only tut_welcome is M0");
            Assert.That(mvp.TutorialSteps, Has.Count.EqualTo(12));
        }

        [Test]
        public void FlippedByte_IsE900_UnlistedFile_IsE902_MissingFile_IsE902()
        {
            var src = Copy();
            src.Files["skills.json"][40] ^= 1;
            var e = Assert.Throws<ContentBuildException>(() => ContentLoader.LoadDtos(src));
            Assert.That(Codes(e), Is.EqualTo(new[] { "E900" }));
            Assert.That(e.Errors[0].Location, Is.EqualTo("skills.json"));

            src = Copy();
            src.Files["pets.json"] = Encoding.UTF8.GetBytes("{}");
            e = Assert.Throws<ContentBuildException>(() => ContentLoader.LoadDtos(src));
            Assert.That(e.Errors.Single().ToString(), Is.EqualTo("CONTENT E902 pets.json: pets.json present but not listed"));

            src = Copy();
            src.Files.Remove("tank.json");
            e = Assert.Throws<ContentBuildException>(() => ContentLoader.LoadDtos(src));
            Assert.That(Codes(e), Is.EqualTo(new[] { "E902" }));
        }

        [Test]
        public void ManifestSchemaVersion_IsE901_MinClientVersion_IsE903()
        {
            var src = Copy();
            src.Files["manifest.json"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(src.Files["manifest.json"]).Replace("\"schema_version\": 1", "\"schema_version\": 2"));
            Assert.That(Codes(Assert.Throws<ContentBuildException>(() => ContentLoader.LoadDtos(src))), Is.EqualTo(new[] { "E901" }));

            var e = Assert.Throws<ContentBuildException>(() => ContentLoader.LoadDtos(Copy(), clientVersion: "0.0.9"));
            Assert.That(e.Errors.Single().Message, Does.StartWith("content requires client >= 0.1.0, this client is 0.0.9"));
            Assert.DoesNotThrow(() => ContentLoader.LoadDtos(Copy(), clientVersion: "0.1.0"));
        }

        [Test]
        public void Build_IsCultureInvariant()   // acceptance 18
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var db = ContentLoader.LoadFromDirectory(TestContent.Dir, Tier.MVP);
                Assert.That(db.EnemyHp.Growth, Is.EqualTo(1.04));
            }
            finally { Thread.CurrentThread.CurrentCulture = prev; }
        }

        [Test]
        public void ContentVersion_OrdersNumerically()
        {
            Assert.That(ContentVersion.Compare("2026.11.03", "2026.11.03.1"), Is.LessThan(0));
            Assert.That(ContentVersion.Compare("2026.11.03.2", "2026.11.03.10"), Is.LessThan(0));
            Assert.That(ContentVersion.Compare("2026.12.01", "2026.11.30.9"), Is.GreaterThan(0));
            Assert.That(ContentVersion.Compare("2026.11.03", "2026.11.03"), Is.EqualTo(0));
            Assert.That(ContentVersion.IsValid("1.0"), Is.False);
            Assert.That(ContentVersion.IsValid("2026.11.03.0"), Is.False);
        }
    }
}
