using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HeroCat.Runtime.Save;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Replay;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>02 sections 5-6, acceptance 1-4 (issue #74).</summary>
    public class SaveSerializerTests
    {
        static GameState Sample() => SaveSamples.EveryFieldNonDefault(TestContent.Db);

        static GameState RoundTrip(GameState s)
        {
            var (back, _, _) = SaveSystem.DecodeFull(SaveSerializer.Encode(s), TestContent.Db);
            return back;
        }

        [Test]
        public void Header_IsV1_WithCrcLenAndNoHmacInLocalMode()
        {
            var bytes = SaveSerializer.Encode(Sample());
            string header = Encoding.ASCII.GetString(bytes, 0, Array.IndexOf(bytes, (byte)'\n'));
            Assert.That(header, Does.Match(@"^HEROCAT-SAVE v=1 fmt=json crc32=[0-9a-f]{8} len=[0-9]+ hmac=-$"));
            var file = SaveSerializer.Decode(bytes);
            Assert.That(file.SchemaVersion, Is.EqualTo(1));
            Assert.That(file.HmacPresent, Is.False);
            Assert.That(file.Sha256, Has.Length.EqualTo(64));
        }

        [Test]
        public void EveryField_RoundTrips_ToAValueEqualState()   // acceptance 1
        {
            var s = Sample();
            var back = RoundTrip(s);
            Assert.That(back.Combat.Fight, Is.Null, "the transient fight is not saved (02 section 3.3)");
            s.Combat.Fight = null;
            Assert.That(StateEquality.Equal(s, back), Is.True);
            Assert.That(back.Pending.Summon, Is.EqualTo(s.Pending.Summon));
            Assert.That(back.Offline.Pending, Is.EqualTo(s.Offline.Pending));
            Assert.That(back.Combat.DungeonRun.Seed, Is.EqualTo(s.Combat.DungeonRun.Seed));
            Assert.That(back.Party.Slots[1].Skill, Is.Null);
            Assert.That(back.Party.Slots[0].Skill, Is.EqualTo(s.Party.Slots[0].Skill));
        }

        [Test]
        public void Doubles_SurviveBitExactly()
        {
            var back = RoundTrip(Sample());
            Assert.That(BitConverter.DoubleToInt64Bits(back.Wallet.Gold), Is.EqualTo(BitConverter.DoubleToInt64Bits(1.8423371e12)));
            Assert.That(BitConverter.DoubleToInt64Bits(back.Wallet.FishCoin), Is.EqualTo(BitConverter.DoubleToInt64Bits(double.MaxValue / 2)));
        }

        [Test]
        public void Streams_ContinueIdentically_AfterRoundTrip()   // acceptance 2
        {
            var s = TestContent.NewState();
            foreach (var st in s.Rng.Values) st.Counter = 5;
            var back = RoundTrip(s);
            foreach (var name in RngStreams.All)
                Assert.That(RngStreams.NextUnit(back.Rng[name]), Is.EqualTo(RngStreams.NextUnit(s.Rng[name])), name);
        }

        [Test]
        public void EqualStates_EncodeToIdenticalBytes_WhateverTheInsertionOrder()   // acceptance 3
        {
            var a = Sample();
            var b = StateCopy.DeepClone(a);
            b.Fish.DiscoveredLevels.Clear();
            foreach (var lv in new[] { 2, 3, 1 }) b.Fish.DiscoveredLevels.Add(lv);
            b.Inbox.ClaimedIds.Clear(); b.Inbox.ClaimedIds.Add("ibx_a"); b.Inbox.ClaimedIds.Add("ibx_b");
            var ex = new Dictionary<string, int>(b.Shop.ExchangeCounts.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value));
            b.Shop.ExchangeCounts.Clear();
            foreach (var kv in ex) b.Shop.ExchangeCounts[kv.Key] = kv.Value;
            var banners = b.Banners.Reverse().ToList();
            b.Banners.Clear();
            foreach (var kv in banners) b.Banners[kv.Key] = kv.Value;
            Assert.That(SaveSerializer.Encode(b), Is.EqualTo(SaveSerializer.Encode(a)));
        }

        [Test]
        public void Dictionaries_AndSets_AreWrittenSorted_EnumsByName()
        {
            var j = SaveSerializer.ToJson(Sample());
            Assert.That(((JObject)j["Banners"]).Properties().Select(p => p.Name), Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));
            Assert.That(j["Fish"]["DiscoveredLevels"].Values<int>(), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That((string)j["Combat"]["Mode"], Is.EqualTo("Dungeon"));
            Assert.That(j["Combat"]["Fight"], Is.Null, "Fight is skipped by ShouldSerializeFight");
            Assert.That(j["Wallet"]["HeroExp"], Is.Null, "HeroExp is not a balance");
            Assert.That(j["Rng"]["crit"]["Seed"].Type, Is.EqualTo(JTokenType.String));
        }

        [Test]
        public void Nan_EncodesAsValidJson()
        {
            var s = TestContent.NewState();
            s.Wallet.Gold = double.NaN;
            var bytes = SaveSerializer.Encode(s);
            var file = SaveSerializer.Decode(bytes);   // parses: NaN is a string token, rejected by the validator later (TODO(#75) P2)
            Assert.That(file.Body["Wallet"]["Gold"].Type, Is.EqualTo(JTokenType.String));
        }

        [Test]
        public void Corruption_Throws_HmacMismatchDoesNot()   // acceptance 4
        {
            var good = SaveSerializer.Encode(Sample());
            int nl = Array.IndexOf(good, (byte)'\n');
            var crc = (byte[])good.Clone(); crc[good.Length - 3] ^= 1;
            Assert.Throws<SaveCorruptException>(() => SaveSerializer.Decode(crc), "crc");
            Assert.Throws<SaveCorruptException>(() => SaveSerializer.Decode(good.Take(good.Length - 1).ToArray()), "len - 1");
            Assert.Throws<SaveCorruptException>(() => SaveSerializer.Decode(good.Concat(new byte[] { 32 }).ToArray()), "len + 1");
            var noNl = good.Take(nl).Concat(good.Skip(nl + 1)).ToArray();
            Assert.Throws<SaveCorruptException>(() => SaveSerializer.Decode(noNl), "missing newline");
            Assert.Throws<SaveCorruptException>(() => SaveSerializer.Decode(good.Take(nl + 20).ToArray()), "truncated");

            var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
            var signed = SaveSerializer.Encode(Sample(), key);
            Assert.That(SaveSerializer.Decode(signed, key).HmacValid, Is.True);
            var other = SaveSerializer.Decode(signed, Enumerable.Repeat((byte)7, 32).ToArray());
            Assert.That(other.HmacPresent, Is.True);
            Assert.That(other.HmacValid, Is.False);
        }

        [Test]
        public void Validate_RejectsStructuralDamage()
        {
            var db = TestContent.Db;
            var a = TestContent.NewState(); a.Rng.Remove("loot");
            Assert.Throws<SaveInvalidException>(() => SaveSystem.Validate(a, db), "R4 stream missing");
            var b = TestContent.NewState(); b.Rng["crit"] = new RngStreamState { Seed = "123" };
            Assert.Throws<SaveInvalidException>(() => SaveSystem.Validate(b, db), "R4 seed");
            var c = TestContent.NewState(); c.Fish.Cells = new int[3];
            Assert.Throws<SaveInvalidException>(() => SaveSystem.Validate(c, db), "R5 board");
            var d = TestContent.NewState(); d.Wallet.SummonTicket = new int[3];
            Assert.Throws<SaveInvalidException>(() => SaveSystem.Validate(d, db), "R5 tickets");
            Assert.DoesNotThrow(() => SaveSystem.Validate(TestContent.NewState(), db));
        }

        [Test]
        public void SelfCheck_PassesOnTheNewTree()
        {
            var r = SaveSelfCheck.Run(TestContent.Db);
            Assert.That(r.Ok, Is.True, r.ToString());
        }
    }
}
