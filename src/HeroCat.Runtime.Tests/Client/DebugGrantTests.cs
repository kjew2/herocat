using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>The dev-build currency grant (debug panel; issue #291): a logged admin grant, repeatable.</summary>
    public class DebugGrantTests
    {
        [Test]
        public void DebugGrant_CreditsDiamondsAndTickets_EachCallANewGrant()
        {
            var f = ClientFixture.Open();
            long before = f.S.Sim.State.Wallet.Diamond;
            Assert.That(f.Vm.DebugGrant(Currency.DIAMOND, 10_000).Ok, Is.True);
            Assert.That(f.Vm.DebugGrant(Currency.DIAMOND, 10_000).Ok, Is.True, "a second press is a new grant id");
            Assert.That(f.S.Sim.State.Wallet.Diamond, Is.EqualTo(before + 20_000));
            Assert.That(f.Vm.DebugGrant(Currency.TICKET_SUMMON_COMPANION, 50).Ok, Is.True);
            Assert.That(f.S.Sim.State.Wallet.Get(Currency.TICKET_SUMMON_COMPANION), Is.EqualTo(50));
        }
    }
}
