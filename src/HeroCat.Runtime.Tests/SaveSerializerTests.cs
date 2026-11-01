using System;
using System.Text;
using HeroCat.Runtime.Save;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    public class SaveSerializerTests
    {
        static GameState RoundTrip(GameState s, SaveMeta meta = null)
        {
            var (v, body) = SaveSerializer.Decode(SaveSerializer.Encode(s, meta ?? new SaveMeta()));
            Assert.That(v, Is.EqualTo(SaveSchema.Current));
            return SaveSerializer.Materialize(body).state;
        }

        [Test]
        public void RoundTrip_PlayedState_IsExact()
        {
            var sim = TestContent.PlayedSim();
            Assert.That(sim.State.HeroBanner.TotalPulls, Is.GreaterThan(0), "fixture should have pulled");
            Assert.That(sim.State.Lifetime.Kills, Is.GreaterThan(0));
            var back = RoundTrip(sim.State);
            Assert.That(TestContent.Dump(back), Is.EqualTo(TestContent.Dump(sim.State)));
            SaveSystem.Validate(back, TestContent.Db);
        }

        [Test]
        public void Meta_RoundTrips()
        {
            var meta = new SaveMeta { LastSavedAtServerSec = 1793491200, SaveSequence = 412, DeviceInstallId = Guid.NewGuid(), DebugTainted = true };
            var (_, body) = SaveSerializer.Decode(SaveSerializer.Encode(TestContent.NewSim().State, meta));
            var back = SaveSerializer.Materialize(body).meta;
            Assert.That(back.LastSavedAtServerSec, Is.EqualTo(meta.LastSavedAtServerSec));
            Assert.That(back.SaveSequence, Is.EqualTo(meta.SaveSequence));
            Assert.That(back.DeviceInstallId, Is.EqualTo(meta.DeviceInstallId));
            Assert.That(back.DebugTainted, Is.True);
        }

        [TestCase(0UL)]
        [TestCase(ulong.MaxValue)]
        [TestCase(0x9f86d081884c7d65UL)]
        public void UlongSeedsAndCounters_RoundTripExactly(ulong value)
        {
            var s = TestContent.NewSim().State;
            s.Rng.ProfileSeed = value;
            s.Rng.Streams[StreamNames.Combat].Seed = value;
            s.Rng.Streams[StreamNames.Enhance].Counter = value;   // counters are JSON integers, exact past 2^53 too
            var (_, body) = SaveSerializer.Decode(SaveSerializer.Encode(s, null));
            Assert.That((string)body["State"]["Rng"]["ProfileSeed"], Is.EqualTo(value.ToString("x16")));
            var back = SaveSerializer.Materialize(body).state;
            Assert.That(back.Rng.ProfileSeed, Is.EqualTo(value));
            Assert.That(back.Rng.Streams[StreamNames.Combat].Seed, Is.EqualTo(value));
            Assert.That(back.Rng.Streams[StreamNames.Enhance].Counter, Is.EqualTo(value));
        }

        [Test]
        public void EnumsAreStrings_DoublesExact_NullSlotsExplicit()
        {
            var s = TestContent.NewSim().State;
            s.Gear.Slots[1] = new GearPiece(1, Rarity.L, 170, 6);
            s.Currencies.Gold = 0.1 + 0.2;   // not representable nicely; must survive bit-exactly
            s.Combat.EnemyHp = 1.2345678901234567e250;
            var body = Encoding.UTF8.GetString(SaveSerializer.Encode(s, null));
            StringAssert.Contains("\"Rarity\": \"L\"", body);
            StringAssert.Contains("\"Phase\": \"Spawning\"", body);
            StringAssert.Contains("null", body);
            var back = RoundTrip(s);
            Assert.That(back.Gear.Slots[1], Is.EqualTo(new GearPiece(1, Rarity.L, 170, 6)));
            Assert.That(back.Gear.Slots[0], Is.Null);
            Assert.That(BitConverter.DoubleToInt64Bits(back.Currencies.Gold), Is.EqualTo(BitConverter.DoubleToInt64Bits(0.1 + 0.2)));
            Assert.That(back.Combat.EnemyHp, Is.EqualTo(1.2345678901234567e250));
        }

        static byte[] Good() => SaveSerializer.Encode(TestContent.NewSim().State, null);

        static void Corrupt(Func<byte[], byte[]> mutate) =>
            Assert.Throws<SaveCorruptException>(() => SaveSerializer.Decode(mutate(Good())));

        static int HeaderEnd(byte[] f) => Array.IndexOf(f, (byte)'\n');

        [Test] public void Header_FlippedBodyByte_Corrupt() => Corrupt(f => { f[HeaderEnd(f) + 10] ^= 0x20; return f; });
        [Test] public void Header_TruncatedByOne_Corrupt() => Corrupt(f => f.AsSpan(0, f.Length - 1).ToArray());
        [Test] public void Header_TrailingByte_Corrupt() => Corrupt(f => { var g = new byte[f.Length + 1]; f.CopyTo(g, 0); g[^1] = (byte)' '; return g; });
        [Test] public void Header_NoNewline_Corrupt() => Corrupt(f => Encoding.ASCII.GetBytes("HEROCAT-SAVE fmt=json crc32=00000000 len=2"));
        [Test] public void Header_Garbage_Corrupt() => Corrupt(f => new byte[] { 0x00, 0xFF, 0x13, 0x37 });
        [Test] public void Header_Empty_Corrupt() => Corrupt(f => Array.Empty<byte>());

        [Test]
        public void Header_ValidCrcButNotJson_Corrupt() =>
            Corrupt(f => SaveSerializer.EncodeBody("{ \"SchemaVersion\": 1, \"State\": "));

        /// <summary>
        /// The key determinism property (specs/06 S2): saving mid-fight and loading into a fresh Simulation continues
        /// tick-for-tick, draw-for-draw like the run that never saved.
        /// </summary>
        [Test]
        public void SaveLoad_MidFight_ContinuesIdenticallyToUnsavedRun()
        {
            var a = TestContent.PlayedSim();
            a.Advance(12_345);
            TestContent.Spend(a);
            a.Advance(17);
            Assert.That(a.State.Combat.Phase, Is.EqualTo(CombatPhase.Fighting), "should be mid-fight");

            var bytes = SaveSerializer.Encode(a.State, new SaveMeta());
            var (state, _, _) = SaveSystem.DecodeFull(bytes, TestContent.Db);
            var b = new Simulation(TestContent.Db, state);
            Assert.That(TestContent.Dump(b.State), Is.EqualTo(TestContent.Dump(a.State)));

            for (int i = 0; i < 20; i++)
            {
                a.Advance(6000); TestContent.Spend(a);
                b.Advance(6000); TestContent.Spend(b);
            }
            a.Validate();
            Assert.That(TestContent.Dump(b.State), Is.EqualTo(TestContent.Dump(a.State)));
            foreach (var name in StreamNames.All)
                Assert.That(b.State.Rng.Streams[name].PeekDouble(b.State.Rng.Streams[name].Counter),
                            Is.EqualTo(a.State.Rng.Streams[name].PeekDouble(a.State.Rng.Streams[name].Counter)), name);
        }
    }
}
