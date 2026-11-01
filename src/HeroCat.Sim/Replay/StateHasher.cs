using System;
using System.Collections.Generic;
using System.Text;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>
    /// The canonical state hash (specs/03 section 22, specs/10 section 6): SHA-256 over a fixed binary encoding of
    /// GameState. Not JSON and independent of any serialiser. Fixed field order, maps sorted by ordinal key,
    /// doubles as raw IEEE-754 bits, everything little-endian, strings as int32 UTF-8 length (-1 = null) + bytes,
    /// arrays as int32 count (-1 = null) + elements.
    ///
    /// Not hashed: SchemaVersion and ContentVersion (save bookkeeping; content is pinned by ContentHash in the
    /// segment), Cert (it contains this hash) and PendingReceipt (an in-flight request, not sim state).
    /// Changing what is hashed or how is a new Version: bump it and regenerate Fixtures/Replay.
    /// </summary>
    public static class StateHasher
    {
        public const int Version = 1;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("HEROCAT-STATEHASH-v1");

        /// <summary>Lowercase hex SHA-256 of <see cref="Encode"/>.</summary>
        public static string Hash(GameState s) => Sha256.Hex(Sha256.Compute(Encode(s)));

        public static byte[] Encode(GameState s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            var w = new CanonicalWriter(1024);
            w.Bytes(Magic);

            w.Bytes(s.ProfileId.ToByteArray());   // fixed RFC-independent .NET layout on every platform
            w.I64(s.CreatedAtServerSec);
            w.I64(s.Tick);

            w.I64(s.Clock.LastSyncServerMs);
            w.I64(s.Clock.TickAtLastSync);

            var d = s.Daily;
            w.I64(d.DayKey);
            if (d.Progress == null) w.I32(-1); else { w.I32(d.Progress.Length); foreach (var v in d.Progress) w.I32(v); }
            if (d.Rewarded == null) w.I32(-1); else { w.I32(d.Rewarded.Length); foreach (var v in d.Rewarded) w.Bool(v); }

            var c = s.Currencies;
            w.F64(c.Gold);
            w.I64(c.Gems);
            w.I32(c.FreePullsDaily);
            w.I32(c.FreePullsBanked);
            w.I64(c.Shards);
            w.I64(c.EnhMaterials);
            w.I32(c.DungeonTickets);

            var p = s.Progression;
            w.I32(p.StatLevel);
            w.I32(p.ClassAdvances);
            w.I32(p.HighestFirstClearBossStage);
            w.I64(p.ChestTimerStart);
            w.Bool(p.TutorialGranted);

            var st = s.Stage;
            w.I32(st.Stage);
            w.I32(st.KillsInStage);
            w.I32((int)st.Boss);
            w.I32(st.BossFailsOnStage);

            var cb = s.Combat;
            w.I32((int)cb.Phase);
            w.I32(cb.SpawnTimer);
            w.I32(cb.AttackTimer);
            w.I32(cb.BossTimer);
            w.Bool(cb.EnemyIsBoss);
            w.F64(cb.EnemyHp);
            w.F64(cb.EnemyMaxHp);

            var slots = s.Gear.Slots;
            if (slots == null) w.I32(-1);
            else
            {
                w.I32(slots.Length);
                foreach (var g in slots)
                {
                    w.Bool(g != null);
                    if (g == null) continue;
                    w.I32(g.Slot);
                    w.I32((int)g.Rarity);
                    w.I32(g.Tier);
                    w.I32(g.Enhance);
                }
            }

            var h = s.Heroes;
            var copies = SortedKeys(h.Copies);
            w.I32(copies.Count);
            foreach (var k in copies) { w.Str(k); w.I32(h.Copies[k]); }
            if (h.Equipped == null) w.I32(-1); else { w.I32(h.Equipped.Length); foreach (var e in h.Equipped) w.Str(e); }
            w.Bool(h.AutoEquipBest);

            w.I32(s.HeroBanner.PityCounter);
            w.I64(s.HeroBanner.TotalPulls);

            w.U64(s.Rng.ProfileSeed);
            var streams = SortedKeys(s.Rng.Streams);
            w.I32(streams.Count);
            foreach (var k in streams)
            {
                var r = s.Rng.Streams[k];
                w.Str(k);
                w.Str(r?.Name);
                w.U64(r?.Seed ?? 0);
                w.U64(r?.Counter ?? 0);
            }

            var l = s.Lifetime;
            w.I64(l.Kills);
            w.I64(l.BossKills);
            w.I64(l.BossFails);
            w.I64(l.GearDrops);
            w.I64(l.EnhanceAttempts);
            w.I64(l.EnhanceSuccesses);
            w.F64(l.GoldEarned);
            w.F64(l.OfflineGoldEarned);
            w.I64(l.GemsEarned);
            w.I32(l.FirstWallStage);

            return w.ToArray();
        }

        static List<string> SortedKeys<T>(Dictionary<string, T> map)
        {
            var keys = map == null ? new List<string>() : new List<string>(map.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
    }

    /// <summary>Growable little-endian byte writer shared by StateHasher and CommandLogCodec.</summary>
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

        public byte[] ToArray()
        {
            var r = new byte[_n];
            Buffer.BlockCopy(_b, 0, r, 0, _n);
            return r;
        }
    }

    /// <summary>
    /// FIPS 180-4 SHA-256, hand-rolled so the hash has no platform crypto dependency (Unity IL2CPP stripping,
    /// browser WebAssembly). Integer-only, so it is bit-exact everywhere.
    /// </summary>
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
}
