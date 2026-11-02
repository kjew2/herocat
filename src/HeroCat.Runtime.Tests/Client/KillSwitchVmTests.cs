using System.Collections.Generic;
using HeroCat.Client.Core;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>SCR_SUMMON under the <c>summonEnabled</c> kill switch (01 section 8.12 step 1, AC 11; issue #144).</summary>
    public class KillSwitchVmTests
    {
        static KillSwitches Weapon(bool on) => new KillSwitches(true, true, true, true, new Dictionary<Banner, bool> { [Banner.WEAPON] = on });

        [Test]
        public void TheSummonButtons_FollowTheKillSwitch_OnTheNextRefresh()
        {
            var f = ClientFixture.Open();
            Assert.That(f.Vm.Context.Switches.IsSummonEnabled(Banner.WEAPON), Is.True, "Local config: everything on");
            var flags = Weapon(true);
            f.Vm.Context.Flags = () => flags;
            f.Vm.Main.OnTab(MainTab.Summon);
            f.Vm.Summon.SelectBanner(Banner.WEAPON);
            f.Vm.Refresh();
            Assert.That(f.Vm.Summon.Pull10.Enabled.Value, Is.True);

            flags = Weapon(false);   // a config refresh landed
            f.Frame();
            Assert.That(f.Vm.Summon.Pull1.Enabled.Value, Is.False);
            Assert.That(f.Vm.Summon.Pull10.Enabled.Value, Is.False);
            Assert.That(f.Vm.Summon.Pull10.Reason.Key, Is.EqualTo(SummonVm.DisabledKey));
            Assert.That(SummonVm.DisabledKey, Is.EqualTo("err.summon_disabled.title"));

            long diamonds = f.S.Sim.State.Wallet.Diamond;
            f.Vm.Summon.OnPull10();
            Assert.That(f.Logged<ApplySummonResult>(), Is.EqualTo(0), "nothing rolled");
            Assert.That(f.S.Sim.State.Wallet.Diamond, Is.EqualTo(diamonds));
            Assert.That(f.Vm.Router.IsOpen(ScreenIds.SummonResult), Is.False);

            flags = Weapon(true);
            f.Frame();
            Assert.That(f.Vm.Summon.Pull10.Enabled.Value, Is.True);
            Assert.That(f.Vm.Summon.Pull10.Reason.Key, Is.Null);
        }
    }
}
