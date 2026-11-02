using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>Art address -> Resources path rules (07 sections 15.2-15.3; issue #291).</summary>
    public class ArtAddressesTests
    {
        [TestCase("icon_comp_l_01", "Art/Icons/icon_comp_l_01")]
        [TestCase("portrait_comp_l_01", "Art/Portraits/portrait_comp_l_01")]
        [TestCase("art_splash_comp_m_01", "Art/Splash/art_splash_comp_m_01")]
        [TestCase("spr_comp_l_04", "Art/Pixel/spr_comp_l_04")]
        [TestCase("icon_weapon_e_06", "Art/Icons/icon_weapon_e_06")]
        [TestCase("spr_weapon_e_06", "Art/Pixel/spr_weapon_e_06")]
        [TestCase("vfx_comp_l_01", null)]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void ResourcePath_FollowsThePrefix(string address, string expected) =>
            Assert.That(ArtAddresses.ResourcePath(address), Is.EqualTo(expected));

        [Test]
        public void OnlyBattleSpritesArePixelArt()
        {
            Assert.That(ArtAddresses.IsPixel("spr_comp_l_01"), Is.True);
            Assert.That(ArtAddresses.IsPixel("spr_weapon_c_01"), Is.True);
            Assert.That(ArtAddresses.IsPixel("icon_comp_l_01"), Is.False);
            Assert.That(ArtAddresses.IsPixel("art_splash_comp_l_01"), Is.False);
        }

        [Test]
        public void Splash_IsForLegendaryAndMythicCompanionsOnly()
        {
            Assert.That(ArtAddresses.SplashFor(Banner.COMPANION, "comp_l_01", Rarity.LEGENDARY), Is.EqualTo("art_splash_comp_l_01"));
            Assert.That(ArtAddresses.SplashFor(Banner.COMPANION, "comp_m_01", Rarity.MYTHIC), Is.EqualTo("art_splash_comp_m_01"));
            Assert.That(ArtAddresses.SplashFor(Banner.COMPANION, "comp_e_01", Rarity.EPIC), Is.Null);
            Assert.That(ArtAddresses.SplashFor(Banner.WEAPON, "weapon_m_01", Rarity.MYTHIC), Is.Null);
        }

        [Test]
        public void EveryPilotAssetIsCommitted()   // issue #291: the pilot companions (and Yuki, added after) resolve to files
        {
            string res = System.IO.Path.Combine(TestContent.RepoRoot, "unity", "HeroCatClient", "Assets", "Resources");
            foreach (var id in new[] { "comp_l_01", "comp_l_04", "comp_m_01", "comp_l_02" })
                foreach (var a in new[] { "icon_" + id, "spr_" + id, ArtAddresses.PortraitFor(id), "art_splash_" + id })
                    Assert.That(System.IO.File.Exists(System.IO.Path.Combine(res, ArtAddresses.ResourcePath(a) + ".png")), a);
        }

        [Test]
        public void EveryWeaponPilotAssetIsCommitted()   // issue #290: the weapon pilot's rows resolve to their icon and held sprite
        {
            string res = System.IO.Path.Combine(TestContent.RepoRoot, "unity", "HeroCatClient", "Assets", "Resources");
            var db = TestContent.Db;
            foreach (var id in new[] { "weapon_e_06" })
                foreach (var a in new[] { db.Weapons[id].Icon, db.Weapons[id].Sprite })
                    Assert.That(System.IO.File.Exists(System.IO.Path.Combine(res, ArtAddresses.ResourcePath(a) + ".png")), a);
        }
    }
}
