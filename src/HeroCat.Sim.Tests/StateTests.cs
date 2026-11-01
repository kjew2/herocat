using HeroCat.Runtime.Save;
using HeroCat.Sim.Replay;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Sim.Tests
{
    /// <summary>02 section 3: DeepClone and value equality over every field (issue #72).</summary>
    public class StateTests
    {
        [Test]
        public void DeepClone_OfAnEveryFieldState_IsValueEqual_AndIndependent()
        {
            var s = SaveSamples.EveryFieldNonDefault(TestContent.Db);
            var c = StateCopy.DeepClone(s);
            Assert.That(StateEquality.Equal(s, c), Is.True);
            Assert.That(StateHasher.Hash(c), Is.EqualTo(StateHasher.Hash(s)));
            c.Fish.Cells[0] = 9;
            c.Weapons.Items["weapon_c_01"].Copies = 99;
            c.Combat.Fight.EnemyHp = 1;
            c.Stats.ClientTicketsEarned[HeroCat.Sim.Content.Banner.WEAPON][HeroCat.Sim.Economy.CurrencyReason.QUEST] = 50;
            Assert.That(s.Fish.Cells[0], Is.EqualTo(3));
            Assert.That(s.Weapons.Items["weapon_c_01"].Copies, Is.EqualTo(1));
            Assert.That(s.Combat.Fight.EnemyHp, Is.EqualTo(10.5));
            Assert.That(StateEquality.Equal(s, c), Is.False);
        }

        [Test]
        public void StateHash_IgnoresSaveBookkeeping_ButEqualityDoesNot()
        {
            var a = SaveSamples.EveryFieldNonDefault(TestContent.Db);
            var b = StateCopy.DeepClone(a);
            b.SaveSeq += 1;
            b.Time.LastSavedServerTime += 60;
            Assert.That(StateHasher.Hash(b), Is.EqualTo(StateHasher.Hash(a)));
            Assert.That(StateEquality.Equal(a, b), Is.False);
            b = StateCopy.DeepClone(a);
            b.Time.Tick += 1;
            Assert.That(StateHasher.Hash(b), Is.Not.EqualTo(StateHasher.Hash(a)));
        }
    }
}
