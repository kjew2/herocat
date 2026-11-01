using System;
using System.Collections.Generic;
using System.Text;

namespace HeroCat.Sim
{
    /// <summary>
    /// What a <see cref="CommandRecord"/> replays (specs/10 section 6, specs/03 section 22). Values are stable on the wire:
    /// append new kinds at the end, never renumber.
    /// </summary>
    public enum CommandKind : byte
    {
        /// <summary>BuyStatLevels(A). BuyStatLevel and BuyStatLevelsMax record the m they actually buy.</summary>
        BuyStatLevels,
        /// <summary>Enhance(slot A).</summary>
        Enhance,
        /// <summary>EquipHero(slot A, hero B = index in ContentDb.HeroList).</summary>
        EquipHero,
        /// <summary>UnequipHero(slot A).</summary>
        UnequipHero,
        /// <summary>A = 1: AutoEquipBestHeroes / SetAutoEquipHeroes(true). A = 0: SetAutoEquipHeroes(false).</summary>
        AutoEquipBest,
        AttemptBoss,
        Pull,
        Pull10,
        /// <summary>ClaimIdleChest.</summary>
        ClaimChest,
        /// <summary>SyncWallClock(nowServerSec A, applyCatchUp B != 0).</summary>
        SyncWallClock,
        /// <summary>P1: a server ledger grant (LedgerId). No local command yet; Replay rejects it.</summary>
        GrantFromLedger,
        /// <summary>A Debug.* call: A = (long)DebugOp | heroIndex &lt;&lt; 8, B = argument (GrantGold: raw double bits).</summary>
        Debug,

        // ---- additions beyond the specs/10 section 6 list (other public state-changing entry points)

        /// <summary>ApplyTutorialGrants.</summary>
        TutorialGrants,
        /// <summary>ApplyOfflineCatchUp(elapsedSec A) called directly (SyncWallClock's own catch-up is not recorded separately).</summary>
        OfflineCatchUp,
        /// <summary>EnsureDay(nowServerSec A) called directly.</summary>
        EnsureDay,
    }

    /// <summary>Sub-command of <see cref="CommandKind.Debug"/>, in the low byte of A.</summary>
    public enum DebugOp : byte
    {
        None = 0, GrantGold = 1, GrantGems = 2, GrantHeroCopies = 3, SetStatLevel = 4, SetStage = 5,
    }

    /// <summary>
    /// One player action at a tick. (A readonly struct rather than a record struct: the sim is C# 9.)
    /// Ticks are not logged; they are the gaps between records.
    /// </summary>
    public readonly struct CommandRecord : IEquatable<CommandRecord>
    {
        public CommandRecord(long tick, CommandKind kind, long a = 0, long b = 0, string ledgerId = null)
        {
            Tick = tick; Kind = kind; A = a; B = b; LedgerId = ledgerId;
        }

        public long Tick { get; }
        public CommandKind Kind { get; }
        public long A { get; }
        public long B { get; }

        /// <summary>Server ledger id for pulls, purchases and grants (P1); null for local play.</summary>
        public string LedgerId { get; }

        public CommandRecord WithTick(long tick) => new CommandRecord(tick, Kind, A, B, LedgerId);
        public CommandRecord WithArgs(long a, long b) => new CommandRecord(Tick, Kind, a, b, LedgerId);

        public bool Equals(CommandRecord o) => Tick == o.Tick && Kind == o.Kind && A == o.A && B == o.B &&
                                               string.Equals(LedgerId, o.LedgerId, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is CommandRecord o && Equals(o);
        public override int GetHashCode() => unchecked((int)Tick * 397 ^ (int)Kind * 31 ^ (int)A ^ (int)(B >> 3));
        public static bool operator ==(CommandRecord x, CommandRecord y) => x.Equals(y);
        public static bool operator !=(CommandRecord x, CommandRecord y) => !x.Equals(y);
        public override string ToString() => $"@{Tick} {Kind}({A}, {B}{(LedgerId != null ? ", " + LedgerId : "")})";
    }

    /// <summary>Receives a record whenever a command runs on a <see cref="Simulation"/>.</summary>
    public interface ICommandLog
    {
        void Append(in CommandRecord r);
    }

    /// <summary>The default: records nothing (the bot fast path).</summary>
    public sealed class NullCommandLog : ICommandLog
    {
        public static readonly NullCommandLog Instance = new NullCommandLog();
        NullCommandLog() { }
        public void Append(in CommandRecord r) { }
    }

    /// <summary>
    /// In-memory log. Bounded: past <see cref="Capacity"/> it stops recording and sets <see cref="Overflowed"/>
    /// (an overflowed log cannot be replayed; specs/10 caps a segment at CERT_MAX_COMMANDS = 20,000).
    /// </summary>
    public sealed class ListCommandLog : ICommandLog
    {
        public const int CertMaxCommands = 20_000;

        readonly List<CommandRecord> _records = new List<CommandRecord>();

        public ListCommandLog(int capacity = int.MaxValue)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
        }

        public int Capacity { get; }
        public bool Overflowed { get; private set; }
        public int Count => _records.Count;
        public IReadOnlyList<CommandRecord> Records => _records;

        public void Append(in CommandRecord r)
        {
            if (_records.Count >= Capacity) { Overflowed = true; return; }
            _records.Add(r);
        }

        public void AddRange(IEnumerable<CommandRecord> records)
        {
            foreach (var r in records) Append(r);
        }

        public CommandRecord[] ToArray() => _records.ToArray();

        /// <summary>After a certification (or a save that starts a new segment).</summary>
        public void Clear()
        {
            _records.Clear();
            Overflowed = false;
        }
    }

    /// <summary>A server time signed by ServerClock (specs/10 section 4.2). The sim does not verify signatures.</summary>
    public sealed class SignedTime
    {
        public SignedTime(long serverSec, string signature)
        {
            ServerSec = serverSec;
            Signature = signature;
        }

        public long ServerSec { get; }
        public string Signature { get; }
    }

    /// <summary>The log does not fit the state it is replayed on (out-of-order tick, unknown kind, unsigned time).</summary>
    public sealed class ReplayException : Exception
    {
        public ReplayException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Compact binary form of a command log (ProgressSegment.Commands, the save's log sidecar). Little-endian:
    /// "IRCL", version byte 1, int32 count, then per record int64 tick, byte kind, int64 A, int64 B,
    /// int32 ledger-id byte length (-1 = null) and its UTF-8 bytes.
    /// </summary>
    public static class CommandLogCodec
    {
        const byte Version = 1;
        static readonly byte[] Magic = { (byte)'I', (byte)'R', (byte)'C', (byte)'L' };

        public static byte[] Encode(IReadOnlyList<CommandRecord> records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            var w = new CanonicalWriter(16 + records.Count * 29);
            w.Bytes(Magic);
            w.U8(Version);
            w.I32(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                w.I64(r.Tick);
                w.U8((byte)r.Kind);
                w.I64(r.A);
                w.I64(r.B);
                w.Str(r.LedgerId);
            }
            return w.ToArray();
        }

        /// <summary>Throws FormatException on a malformed buffer.</summary>
        public static CommandRecord[] Decode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var r = new Reader(data);
            for (int i = 0; i < Magic.Length; i++)
                if (r.U8() != Magic[i]) throw new FormatException("not a command log (magic)");
            byte v = r.U8();
            if (v != Version) throw new FormatException($"unsupported command log version {v}");
            int n = r.I32();
            if (n < 0 || n > (data.Length - 9) / 29) throw new FormatException($"bad record count {n}");
            var result = new CommandRecord[n];
            for (int i = 0; i < n; i++)
            {
                long tick = r.I64();
                var kind = (CommandKind)r.U8();
                long a = r.I64(), b = r.I64();
                result[i] = new CommandRecord(tick, kind, a, b, r.Str());
            }
            if (!r.AtEnd) throw new FormatException("trailing bytes after command log");
            return result;
        }

        sealed class Reader
        {
            readonly byte[] _d;
            int _p;
            public Reader(byte[] d) { _d = d; }
            public bool AtEnd => _p == _d.Length;

            void Need(int n) { if (_p + n > _d.Length || n < 0) throw new FormatException("truncated command log"); }
            public byte U8() { Need(1); return _d[_p++]; }

            public int I32()
            {
                Need(4);
                int v = _d[_p] | _d[_p + 1] << 8 | _d[_p + 2] << 16 | _d[_p + 3] << 24;
                _p += 4;
                return v;
            }

            public long I64()
            {
                Need(8);
                ulong v = 0;
                for (int i = 7; i >= 0; i--) v = (v << 8) | _d[_p + i];
                _p += 8;
                return unchecked((long)v);
            }

            public string Str()
            {
                int len = I32();
                if (len == -1) return null;
                Need(len);
                string s = Encoding.UTF8.GetString(_d, _p, len);
                _p += len;
                return s;
            }
        }
    }
}
