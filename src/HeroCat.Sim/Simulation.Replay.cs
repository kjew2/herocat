using System;
using HeroCat.Sim.Content;

namespace HeroCat.Sim
{
    /// <summary>
    /// Command log, state hash and replay (specs/03 section 22, specs/10 sections 4.4 and 6). Every public command
    /// appends one <see cref="CommandRecord"/> to <see cref="CommandLog"/> when it runs (accepted or rejected);
    /// Tick() is never logged. Replaying a log on the genesis state reproduces the run, so equal
    /// <see cref="StateHash"/> values prove equal play.
    /// </summary>
    public sealed partial class Simulation
    {
        ICommandLog _log = NullCommandLog.Instance;
        string[] _heroIds;

        /// <summary>Where commands are recorded. Defaults to <see cref="NullCommandLog"/>; null resets to it.</summary>
        public ICommandLog CommandLog
        {
            get => _log;
            set => _log = value ?? NullCommandLog.Instance;
        }

        /// <summary>Lowercase hex SHA-256 of the canonical encoding of <see cref="State"/> (see <see cref="StateHasher"/>).</summary>
        public string StateHash() => StateHasher.Hash(State);

        /// <summary>Apply <paramref name="log"/>: Advance to each record's tick, then run its command.</summary>
        /// <param name="times">
        /// Optional signed server times (specs/10 section 4.2). When given, every SyncWallClock record's time must be
        /// one of them, else <see cref="ReplayException"/>. Signatures are the server's business, not checked here.
        /// </param>
        public void Replay(ReadOnlySpan<CommandRecord> log, SignedTime[] times) => Replay(log, times, -1);

        /// <summary>As <see cref="Replay(ReadOnlySpan{CommandRecord}, SignedTime[])"/>, then Advance to <paramref name="toTick"/> (-1: stop at the last record).</summary>
        public void Replay(ReadOnlySpan<CommandRecord> log, SignedTime[] times, long toTick)
        {
            foreach (var r in log)
            {
                if (r.Tick < State.Tick) throw new ReplayException($"record {r} is before the current tick {State.Tick}");
                Advance(r.Tick - State.Tick);
                if (r.Kind == CommandKind.SyncWallClock && times != null && !IsSigned(times, r.A))
                    throw new ReplayException($"record {r} has no signed time");
                ApplyCommand(r);
            }
            if (toTick >= 0)
            {
                if (toTick < State.Tick) throw new ReplayException($"end tick {toTick} is before the current tick {State.Tick}");
                Advance(toTick - State.Tick);
            }
        }

        static bool IsSigned(SignedTime[] times, long sec)
        {
            foreach (var t in times) if (t != null && t.ServerSec == sec) return true;
            return false;
        }

        /// <summary>Run one record's command now (ignores its Tick). Malformed arguments throw ReplayException.</summary>
        public void ApplyCommand(in CommandRecord r)
        {
            try
            {
                switch (r.Kind)
                {
                    case CommandKind.BuyStatLevels: BuyStatLevels(checked((int)r.A)); break;
                    case CommandKind.Enhance: Enhance(checked((int)r.A)); break;
                    case CommandKind.EquipHero: EquipHero(checked((int)r.A), HeroIdAt(r.B)); break;
                    case CommandKind.UnequipHero: UnequipHero(checked((int)r.A)); break;
                    case CommandKind.AutoEquipBest:
                        if (r.A != 0) AutoEquipBestHeroes(); else SetAutoEquipHeroes(false);
                        break;
                    case CommandKind.AttemptBoss: AttemptBoss(); break;
                    case CommandKind.Pull: Pull(); break;
                    case CommandKind.Pull10: Pull10(); break;
                    case CommandKind.ClaimChest: ClaimIdleChest(); break;
                    case CommandKind.SyncWallClock: SyncWallClock(r.A, r.B != 0); break;
                    case CommandKind.TutorialGrants: ApplyTutorialGrants(); break;
                    case CommandKind.OfflineCatchUp: ApplyOfflineCatchUp(r.A); break;
                    case CommandKind.EnsureDay: EnsureDay(r.A); break;
                    case CommandKind.Debug: ApplyDebug(r); break;
                    default: throw new ReplayException($"record {r}: kind {r.Kind} cannot be replayed locally");
                }
            }
            catch (ArgumentException e) { throw new ReplayException($"record {r}: {e.Message}", e); }
            catch (OverflowException e) { throw new ReplayException($"record {r}: {e.Message}", e); }
        }

        void ApplyDebug(in CommandRecord r)
        {
            var op = (DebugOp)(r.A & 0xFF);
            switch (op)
            {
                case DebugOp.GrantGold: Debug.GrantGold(BitConverter.Int64BitsToDouble(r.B)); break;
                case DebugOp.GrantGems: Debug.GrantGems(r.B); break;
                case DebugOp.GrantHeroCopies: Debug.GrantHeroCopies(HeroIdAt(r.A >> 8), checked((int)r.B)); break;
                case DebugOp.SetStatLevel: Debug.SetStatLevel(checked((int)r.B)); break;
                case DebugOp.SetStage: Debug.SetStage(checked((int)r.B)); break;
                default: throw new ReplayException($"record {r}: unknown debug op {op}");
            }
        }

        // ------------------------------------------------------------ recording helpers

        internal void Log(CommandKind kind, long a = 0, long b = 0)
        {
            if (ReferenceEquals(_log, NullCommandLog.Instance)) return;   // fast path
            _log.Append(new CommandRecord(State.Tick, kind, a, b, null));
        }

        internal bool Logging => !ReferenceEquals(_log, NullCommandLog.Instance);

        /// <summary>Index of a hero id in ContentDb.HeroList (the replay encoding of a hero argument).</summary>
        internal long HeroIndexOf(string heroId)
        {
            var ids = HeroIds();
            for (int i = 0; i < ids.Length; i++)
                if (string.Equals(ids[i], heroId, StringComparison.Ordinal)) return i;
            throw new ArgumentOutOfRangeException(nameof(heroId));
        }

        string HeroIdAt(long index)
        {
            var ids = HeroIds();
            if (index < 0 || index >= ids.Length) throw new ReplayException($"hero index {index} out of range");
            return ids[index];
        }

        string[] HeroIds()
        {
            if (_heroIds != null) return _heroIds;
            var list = Content.HeroList;
            var ids = new string[list.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = list[i].Id;
            return _heroIds = ids;
        }
    }
}
