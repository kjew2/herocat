using System;
using System.Collections.Generic;
using System.Text;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Replay
{
    /// <summary>
    /// The canonical state encoding (00 contract 12, specs/10 section 2): a fixed binary form of every GameState field in
    /// declaration order, independent of any serializer. Little-endian; doubles as raw IEEE-754 bits; strings as int32
    /// UTF-8 length (-1 = null) + bytes; arrays and lists as int32 count (-1 = null) + elements; dictionaries and sets
    /// sorted by key (ordinal for strings, ordinal value for enums); a nullable record as a presence byte + fields.
    ///
    /// <see cref="Hash"/> (StateHash) leaves out save bookkeeping that changes on every write, not in play:
    /// SchemaVersion, ContentVersion, ContentHash, DeviceId, SaveSeq and Time.LastSavedServerTime. It includes the
    /// transient Combat.Fight, so a mid-fight DeepClone hashes like its source. <see cref="Encode(GameState, bool)"/> with
    /// bookkeeping is the value-equality form (<see cref="StateEquality"/>). Changing what is encoded is a new
    /// <see cref="Version"/>: bump it and regenerate the golden files through the bot CLI.
    /// </summary>
    public static class StateHasher
    {
        public const int Version = 3;   // literal-ok: encoding version; 3 adds FightState.GoldBuffPct / GoldBuffTicksLeft (issue #156)
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("HEROCAT-STATEHASH-v3");

        /// <summary>Lowercase hex SHA-256 of the play-state encoding.</summary>
        public static string Hash(GameState s) => Sha256.Hex(Sha256.Compute(Encode(s, includeBookkeeping: false)));

        public static byte[] Encode(GameState s) => Encode(s, includeBookkeeping: false);

        public static byte[] Encode(GameState s, bool includeBookkeeping)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            var w = new CanonicalWriter(2048);   // literal-ok: initial buffer capacity, not balance
            w.Bytes(Magic);
            if (includeBookkeeping)
            {
                w.I32(s.SchemaVersion);
                w.Str(s.ContentVersion);
                w.Str(s.ContentHash);
                w.Guid(s.DeviceId);
                w.I64(s.SaveSeq);
                w.I64(s.Time?.LastSavedServerTime ?? 0);
            }
            w.Guid(s.PlayerId);
            w.Guid(s.RngRoot);

            var t = s.Time;
            w.I64(t.Tick); w.I64(t.LastTickServerTime); w.I64(t.AnchorServerTime); w.I64(t.AnchorTick);
            w.I64(t.CreatedServerTime); w.I32(t.DayKey); w.I32(t.WeekKey); w.I32(t.LoginDayKey);

            w.I32(s.Hero.Level); w.F64(s.Hero.Exp); w.I32(s.Hero.AwakenRank);
            w.Ints(s.Training.Level);

            var wa = s.Wallet;
            w.F64(wa.Gold); w.I64(wa.Diamond); w.F64(wa.FishCoin); w.I64(wa.RockCandy);
            w.Ints(wa.DungeonTicket); w.Ints(wa.SummonTicket); w.I64(wa.GuildCoin);

            Collection(w, s.Weapons); Collection(w, s.Skills); Collection(w, s.Companions); Collection(w, s.Runes);

            var lo = s.Loadout;
            w.Str(lo.Weapon); w.Strs(lo.Skills); w.Strs(lo.Companions); w.Strs(lo.Runes);

            var f = s.Fish;
            w.Ints(f.Cells); w.I32(f.TankCount); w.I32(f.TankRefillTick); w.I32(f.TankReleaseLevel);
            w.IntSet(f.DiscoveredLevels); w.I32(f.HighestLevel); w.I64(f.TotalMerges); w.Bool(f.AutoMergeEnabled);

            var banners = SortedEnumKeys(s.Banners);
            w.I32(banners.Count);
            foreach (var b in banners)
            {
                var st = s.Banners[b];
                w.I32((int)b);
                w.Bool(st != null);
                if (st == null) continue;
                w.I32(st.Pity); w.I64(st.Exp); w.I64(st.TotalPulls);
            }

            var c = s.Combat;
            w.I32((int)c.Mode); w.I32(c.Stage); w.I32(c.KillsInStage); w.I32((int)c.Boss); w.I32(c.BossFailsOnStage);
            w.Bool(c.Selected.HasValue); w.I32(c.Selected.GetValueOrDefault()); w.F64(c.Mp);
            w.Ints(c.SkillCooldowns); w.Ints(c.CompanionCooldowns);
            if (c.MissileQueue == null) w.I32(-1); else { w.I32(c.MissileQueue.Count); foreach (var m in c.MissileQueue) w.F64(m); }
            var dr = c.DungeonRun;
            w.Bool(dr != null);
            if (dr != null)
            {
                w.I32((int)dr.Kind); w.I32(dr.Floor); w.U64(dr.Seed); w.I64(dr.StreamCounter); w.Str(dr.GrantId);
                w.I32(dr.EnemyIndex); w.I32(dr.TicksLeft);
            }
            var fi = c.Fight;
            w.Bool(fi != null);
            if (fi != null)
            {
                w.I32((int)fi.Phase); w.I32(fi.SpawnTimer); w.F64(fi.EnemyHp); w.F64(fi.EnemyMaxHp); w.Bool(fi.EnemyIsBoss);
                w.I32(fi.BossTicksLeft); w.I32(fi.AttackProgress);
                w.I32(fi.ActiveDots.Count);
                foreach (var d in fi.ActiveDots) { w.I32(d.TicksLeft); w.F64(d.DamagePerTick); }
                w.F64(fi.AtkBuffPct); w.I32(fi.AtkBuffTicksLeft); w.F64(fi.GoldBuffPct); w.I32(fi.GoldBuffTicksLeft);
                w.I32(fi.PendingTaps); w.I32(fi.TapsThisSecond);
                w.I64(fi.TapWindowTick);
            }

            var p = s.Progress;
            w.I32(p.HighestCleared); w.I32(p.HighestFirstClearBoss); w.I32(p.StageCapReached);

            Quests(w, s.Quests.Daily); Quests(w, s.Quests.Weekly); Quests(w, s.Quests.Achievement);

            var dg = s.Dungeons;
            w.Ints(dg.BestFloor); w.Ints(dg.EntriesToday); w.Ints(dg.SweepsToday);
            w.I32(dg.RecentRuns.Count);
            foreach (var r in dg.RecentRuns) { w.Str(r.GrantId); w.I32((int)r.Kind); w.I32(r.Floor); w.Bool(r.Cleared); }

            w.Bool(s.Shop.DailyGiftClaimed); IntMap(w, s.Shop.ExchangeCounts);
            IntMap(w, s.Research.Level);
            w.StrSet(s.Inbox.ClaimedIds); w.I64(s.Inbox.LastFetchedServerTime);

            var o = s.Offline;
            w.Bool(o.Pending != null);
            if (o.Pending != null)
            {
                var cr = o.Pending;
                w.I64(cr.ElapsedSec); w.I64(cr.CreditedSec); w.Bool(cr.Capped); w.F64(cr.Gold); w.F64(cr.Exp); w.I32(cr.TankFill);
                w.I32(cr.Stage); w.F64(cr.KillTimeSec); w.I64(cr.ComputedServerTime);
            }
            w.I64(o.LifetimeOfflineSecCredited);

            var pe = s.Pending;
            w.Bool(pe.Summon != null);
            if (pe.Summon != null)
            {
                w.Str(pe.Summon.RequestId); w.I32((int)pe.Summon.Banner); w.I32(pe.Summon.Count); w.Str(pe.Summon.Payment);
                w.I64(pe.Summon.RequestedServerTime);
            }
            w.Bool(pe.Dungeon != null);
            if (pe.Dungeon != null)
            {
                w.Str(pe.Dungeon.RequestId); w.I32((int)pe.Dungeon.Kind); w.I32(pe.Dungeon.Floor); w.I64(pe.Dungeon.RequestedServerTime);
            }
            w.I32(pe.Receipts.Count);
            foreach (var r in pe.Receipts)
            {
                w.Str(r.RequestId); w.Str(r.Platform); w.Str(r.Sku); w.Str(r.TransactionId); w.Str(r.Payload); w.I64(r.RequestedServerTime);
            }

            w.I32(s.Tutorial.Step); w.Bool(s.Tutorial.Skipped); w.Bool(s.Tutorial.TutorialDiamondsGranted);

            var streams = SortedKeys(s.Rng);
            w.I32(streams.Count);
            foreach (var k in streams)
            {
                var r = s.Rng[k];
                w.Str(k);
                w.Bool(r != null);
                if (r == null) continue;
                w.Str(r.Seed); w.I64(r.Counter);
            }

            var x = s.Stats;
            w.I64(x.Kills); w.I64(x.BossKills); w.I64(x.BossFails); w.I64(x.HeroHits); w.I64(x.Taps); w.I64(x.Pulls);
            w.I64(x.Merges); w.I64(x.FishReleased); w.I64(x.FishDiscards); w.I64(x.DungeonClears); w.I64(x.TrainingLevelsBought);
            w.I32(x.FirstWallStage); w.I64(x.ActiveSeconds);
            w.F64(x.GoldEarned); w.F64(x.GoldSpent); w.F64(x.FishCoinEarned);
            w.I64(x.DiamondsEarned); w.I64(x.DiamondsSpent); w.I64(x.DiamondsPurchased);
            ReasonMap(w, x.ClientDiamondsEarned); ReasonMap(w, x.ClientDiamondsSpent);
            var tb = SortedEnumKeys(x.ClientTicketsEarned);
            w.I32(tb.Count);
            foreach (var b in tb) { w.I32((int)b); ReasonMap(w, x.ClientTicketsEarned[b]); }

            w.Bool(s.DebugTainted);
            return w.ToArray();
        }

        static void Collection(CanonicalWriter w, CollectionState c)
        {
            var keys = SortedKeys(c.Items);
            w.I32(keys.Count);
            foreach (var k in keys)
            {
                var it = c.Items[k];
                w.Str(k);
                w.Bool(it != null);
                if (it == null) continue;
                w.I32(it.Copies); w.I32(it.LimitBreaks); w.I64(it.FirstAcquiredServerTime);
            }
        }

        static void Quests(CanonicalWriter w, Dictionary<string, QuestProgress> q)
        {
            var keys = SortedKeys(q);
            w.I32(keys.Count);
            foreach (var k in keys)
            {
                var v = q[k];
                w.Str(k);
                w.Bool(v != null);
                if (v == null) continue;
                w.I64(v.Progress); w.I32((int)v.State);
            }
        }

        static void IntMap(CanonicalWriter w, Dictionary<string, int> m)
        {
            var keys = SortedKeys(m);
            w.I32(keys.Count);
            foreach (var k in keys) { w.Str(k); w.I32(m[k]); }
        }

        static void ReasonMap(CanonicalWriter w, Dictionary<CurrencyReason, long> m)
        {
            var keys = SortedEnumKeys(m);
            w.I32(keys.Count);
            foreach (var k in keys) { w.I32((int)k); w.I64(m[k]); }
        }

        internal static List<string> SortedKeys<T>(Dictionary<string, T> map)
        {
            var keys = map == null ? new List<string>() : new List<string>(map.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        internal static List<TKey> SortedEnumKeys<TKey, T>(Dictionary<TKey, T> map) where TKey : struct, Enum
        {
            var keys = map == null ? new List<TKey>() : new List<TKey>(map.Keys);
            keys.Sort((a, b) => Convert.ToInt32(a).CompareTo(Convert.ToInt32(b)));
            return keys;
        }
    }

    /// <summary>Value equality of two states over every persisted field plus the transient fight (02 acceptance 1).</summary>
    public static class StateEquality
    {
        public static bool Equal(GameState a, GameState b)
        {
            var x = StateHasher.Encode(a, includeBookkeeping: true);
            var y = StateHasher.Encode(b, includeBookkeeping: true);
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }
    }

    /// <summary>Growable little-endian byte writer shared by StateHasher and CommandLogCodec.</summary>
    // literal-ok-begin: little-endian byte encoding (widths and shifts), not balance
    internal sealed class CanonicalWriter
    {
        byte[] _b;
        int _n;

        public CanonicalWriter(int capacity) { _b = new byte[Math.Max(16, capacity)]; }

        void Ensure(int extra)
        {
            if (_n + extra <= _b.Length) return;
            var nb = new byte[Math.Max(_b.Length * 2, _n + extra)];
            Buffer.BlockCopy(_b, 0, nb, 0, _n);
            _b = nb;
        }

        public void U8(byte v) { Ensure(1); _b[_n++] = v; }
        public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);

        public void I32(int v)
        {
            Ensure(4);
            _b[_n++] = (byte)v; _b[_n++] = (byte)(v >> 8); _b[_n++] = (byte)(v >> 16); _b[_n++] = (byte)(v >> 24);
        }

        public void U64(ulong v)
        {
            Ensure(8);
            for (int i = 0; i < 8; i++) { _b[_n++] = (byte)v; v >>= 8; }
        }

        public void I64(long v) => U64(unchecked((ulong)v));
        public void F64(double v) => I64(BitConverter.DoubleToInt64Bits(v));

        public void Bytes(byte[] v)
        {
            Ensure(v.Length);
            Buffer.BlockCopy(v, 0, _b, _n, v.Length);
            _n += v.Length;
        }

        public void Str(string s)
        {
            if (s == null) { I32(-1); return; }
            var bytes = Encoding.UTF8.GetBytes(s);
            I32(bytes.Length);
            Bytes(bytes);
        }

        public void Guid(System.Guid g) => Bytes(g.ToByteArray());   // fixed .NET layout on every platform

        public void Ints(int[] a)
        {
            if (a == null) { I32(-1); return; }
            I32(a.Length);
            foreach (var v in a) I32(v);
        }

        public void Strs(string[] a)
        {
            if (a == null) { I32(-1); return; }
            I32(a.Length);
            foreach (var v in a) Str(v);
        }

        public void IntSet(HashSet<int> set)
        {
            if (set == null) { I32(-1); return; }
            var l = new List<int>(set);
            l.Sort();
            I32(l.Count);
            foreach (var v in l) I32(v);
        }

        public void StrSet(HashSet<string> set)
        {
            if (set == null) { I32(-1); return; }
            var l = new List<string>(set);
            l.Sort(StringComparer.Ordinal);
            I32(l.Count);
            foreach (var v in l) Str(v);
        }

        public byte[] ToArray()
        {
            var r = new byte[_n];
            Buffer.BlockCopy(_b, 0, r, 0, _n);
            return r;
        }
    }
    // literal-ok-end

    /// <summary>
    /// FIPS 180-4 SHA-256, hand-rolled so the hash has no platform crypto dependency (Unity IL2CPP stripping,
    /// browser WebAssembly). Integer-only, so it is bit-exact everywhere.
    /// </summary>
    // literal-ok-begin: FIPS 180-4 SHA-256 constants and bit operations, not balance
    public static class Sha256
    {
        static readonly uint[] K =
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
        };

        public static byte[] Compute(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            uint h0 = 0x6a09e667, h1 = 0xbb67ae85, h2 = 0x3c6ef372, h3 = 0xa54ff53a,
                 h4 = 0x510e527f, h5 = 0x9b05688c, h6 = 0x1f83d9ab, h7 = 0x5be0cd19;

            long bitLen = (long)data.Length * 8;
            int padded = ((data.Length + 9 + 63) / 64) * 64;
            var msg = new byte[padded];
            Buffer.BlockCopy(data, 0, msg, 0, data.Length);
            msg[data.Length] = 0x80;
            for (int i = 0; i < 8; i++) msg[padded - 1 - i] = (byte)(bitLen >> (8 * i));

            var w = new uint[64];
            for (int off = 0; off < padded; off += 64)
            {
                for (int i = 0; i < 16; i++)
                {
                    int j = off + i * 4;
                    w[i] = (uint)msg[j] << 24 | (uint)msg[j + 1] << 16 | (uint)msg[j + 2] << 8 | msg[j + 3];
                }
                for (int i = 16; i < 64; i++)
                {
                    uint s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
                    uint s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
                    w[i] = unchecked(w[i - 16] + s0 + w[i - 7] + s1);
                }

                uint a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7;
                for (int i = 0; i < 64; i++)
                {
                    uint S1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
                    uint ch = (e & f) ^ (~e & g);
                    uint t1 = unchecked(h + S1 + ch + K[i] + w[i]);
                    uint S0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
                    uint maj = (a & b) ^ (a & c) ^ (b & c);
                    uint t2 = unchecked(S0 + maj);
                    h = g; g = f; f = e; e = unchecked(d + t1);
                    d = c; c = b; b = a; a = unchecked(t1 + t2);
                }
                unchecked { h0 += a; h1 += b; h2 += c; h3 += d; h4 += e; h5 += f; h6 += g; h7 += h; }
            }

            var result = new byte[32];
            var hs = new[] { h0, h1, h2, h3, h4, h5, h6, h7 };
            for (int i = 0; i < 8; i++)
            {
                result[i * 4] = (byte)(hs[i] >> 24);
                result[i * 4 + 1] = (byte)(hs[i] >> 16);
                result[i * 4 + 2] = (byte)(hs[i] >> 8);
                result[i * 4 + 3] = (byte)hs[i];
            }
            return result;
        }

        public static string Hex(byte[] bytes)
        {
            const string digits = "0123456789abcdef";
            var c = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                c[i * 2] = digits[bytes[i] >> 4];
                c[i * 2 + 1] = digits[bytes[i] & 0xF];
            }
            return new string(c);
        }

        static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));
    }
    // literal-ok-end
}
