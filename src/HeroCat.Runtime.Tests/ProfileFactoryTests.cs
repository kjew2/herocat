using System;
using HeroCat.Runtime.Profile;
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

        [Test]
        public void EnabledBanners_HaveState()
        {
            var s = TestContent.NewState();
            Assert.That(s.Banners.Keys, Is.EquivalentTo(new[] { Banner.WEAPON, Banner.SKILL }), "M0 build");
        }
    }
}
