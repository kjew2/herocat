using System;
using System.Linq;
using HeroCat.Runtime.Profile;
using HeroCat.Runtime.Save;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Rng;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>02 section 3.4 seeds and the genesis state (issue #72 defaults, #73 minimal, #106 tutorial grant).</summary>
    public class ProfileFactoryTests
    {
        [Test]
        public void NewProfile_HasSchemaV1Defaults()   // issue #72 acceptance
        {
            var s = TestContent.NewState();
            Assert.That(s.SchemaVersion, Is.EqualTo(1));
            Assert.That(s.Hero.Level, Is.EqualTo(1));
            Assert.That(s.Combat.Stage, Is.EqualTo(1));
            Assert.That(s.Training.Level, Has.Length.EqualTo(8));
            Assert.That(s.Fish.Cells, Has.Length.EqualTo(20));
            Assert.That(s.Loadout.Skills, Has.Length.EqualTo(6));
            Assert.That(s.Loadout.Companions, Has.Length.EqualTo(4));
            Assert.That(s.Loadout.Runes, Has.Length.EqualTo(6));
            Assert.That(s.Wallet.DungeonTicket, Has.Length.EqualTo(3));
            Assert.That(s.Wallet.SummonTicket, Has.Length.EqualTo(4));
            Assert.That(s.Time.DayKey, Is.EqualTo(-1));
            Assert.That(s.Combat.Fight, Is.Null);
            Assert.That(s.RngRoot, Is.EqualTo(TestContent.Root));
        }

        [Test]
        public void EightStreams_SeededFromRngRoot_ByShaFormula()
        {
            var s = TestContent.NewState();
            Assert.That(s.Rng.Keys, Is.EquivalentTo(RngStreams.All));
            Assert.That(RngStreams.All, Has.Length.EqualTo(8));
            foreach (var name in RngStreams.All)
            {
                Assert.That(s.Rng[name].Seed, Does.Match("^[0-9a-f]{16}$"));
                Assert.That(s.Rng[name].Counter, Is.EqualTo(0));
                Assert.That(s.Rng[name].Seed, Is.EqualTo(RngStreams.SeedToHex(ProfileFactory.StreamSeed(TestContent.Root, name))));
            }
            // Pinned vector: a change to the formula or to Guid byte order fails here (02 section 3.4).
            Assert.That(ProfileFactory.StreamSeed(new Guid("00000000-0000-0000-0000-000000000001"), "crit").ToString("x16"),
                        Is.EqualTo(PinnedCritSeed));
        }

        const string PinnedCritSeed = "4f1a17ab07860c1b";   // computed independently in Python: sha256(uuid.bytes_le + b"crit")[:8], little-endian

        [Test]
        public void TutorialGrant_IsExactly1500Diamonds_WithReasonTutorial_Once()   // issue #106 acceptance
        {
            var db = TestContent.Db;
            var s = TestContent.NewState();
            Assert.That(s.Wallet.Diamond, Is.EqualTo(1500));
            Assert.That(s.Stats.ClientDiamondsEarned[CurrencyReason.TUTORIAL], Is.EqualTo(1500));
            Assert.That(s.Tutorial.TutorialDiamondsGranted, Is.True);
            Assert.That(db.TutorialWeaponId, Is.EqualTo("weapon_c_01"));
            Assert.That(s.Weapons.Items[db.TutorialWeaponId].Copies, Is.EqualTo(1));
            Assert.That(s.Loadout.Weapon, Is.EqualTo(db.TutorialWeaponId));
            Assert.That(ProfileGrants.ApplyWelcome(s, db, TestContent.T0), Is.False, "a second call grants nothing");
            Assert.That(s.Wallet.Diamond, Is.EqualTo(1500));
        }

        static readonly Guid KnownPlayer = new Guid("6b1f2c40-1234-4abc-8def-0123456789ab");

        // Frozen vectors (issue #73), computed independently in Python: sha256(uuid.bytes_le + name)[:8], little-endian.
        [TestCase("combat", "5b6cb1b172a8d45e")]
        [TestCase("crit", "65fd0459791f96e9")]
        [TestCase("fish_spawn", "5992599aaa231034")]
        [TestCase("dungeon", "1a64341c45fe5fc3")]
        [TestCase("loot", "aac755d66e8bef8c")]
        [TestCase("summon_weapon", "65b883c6c50706c5")]
        [TestCase("summon_skill", "b34788b123bd255b")]
        [TestCase("summon_companion", "b11187f2d851c86a")]
        public void StreamSeed_Vectors_ForAKnownPlayer(string stream, string expectedHex)
        {
            var s = ProfileFactory.Create(TestContent.Db, KnownPlayer, TestContent.T0, TestContent.Device);
            Assert.That(s.RngRoot, Is.EqualTo(KnownPlayer), "a known player is its own RngRoot (issue #9)");
            Assert.That(s.Rng[stream].Seed, Is.EqualTo(expectedHex));
        }

        [Test]
        public void Guest_GetsARandomRngRoot_AndItsSeedsSurviveLinkingAndAnotherDevice()
        {
            var guest = ProfileFactory.Create(TestContent.Db, Guid.Empty, TestContent.T0, TestContent.Device);
            Assert.That(guest.PlayerId, Is.EqualTo(Guid.Empty));
            Assert.That(guest.RngRoot, Is.Not.EqualTo(Guid.Empty));
            var seeds = guest.Rng.ToDictionary(kv => kv.Key, kv => kv.Value.Seed);

            // The first guest auth assigns PlayerId; another install then writes the save.
            var json = SaveSerializer.ToJson(guest);
            json["PlayerId"] = KnownPlayer.ToString();
            var linked = SaveSerializer.Materialize(json);
            SaveStamp.Apply(linked, TestContent.T0 + 60, TestContent.Db, new Guid("0f9e8d7c-0000-4000-8000-000000000002"), ticking: false);
            var back = SaveSystem.DecodeFull(SaveSerializer.Encode(linked), TestContent.Db).state;

            Assert.That(back.PlayerId, Is.EqualTo(KnownPlayer));
            Assert.That(back.RngRoot, Is.EqualTo(guest.RngRoot));
            Assert.That(back.Rng.ToDictionary(kv => kv.Key, kv => kv.Value.Seed), Is.EqualTo(seeds));
            Assert.That(SaveValidator.Run(back, TestContent.T0 + 60, TestContent.Db), Is.Empty);
        }

        [Test]
        public void Create_PassesTheValidator_WithZeroRepairs()   // issue #73 acceptance
        {
            var s = ProfileFactory.Create(TestContent.Db, Guid.Empty, TestContent.T0, TestContent.Device);
            Assert.That(SaveValidator.Run(s, TestContent.T0, TestContent.Db, new SaveValidatorOptions { InstallId = TestContent.Device }), Is.Empty);
        }

        [Test]
        public void EnabledBanners_HaveState()
        {
            var s = TestContent.NewState();
            Assert.That(s.Banners.Keys, Is.EquivalentTo(new[] { Banner.WEAPON, Banner.SKILL }), "M0 build");
        }
    }
}
