using System;
using System.Collections.Generic;
using HeroCat.Sim.Commands;

namespace HeroCat.Sim.Replay
{
    /// <summary>
    /// Receives every command Simulation.Apply runs, accepted or rejected, stamped with its IssuedAtTick, before it is
    /// applied (01 section 2.2 rule 4). Runtime's JSONL command log (01 section 2.6, TODO(#67)) and the bot harness
    /// implement it; replaying the records on the genesis state reproduces the run.
    /// </summary>
    public interface ICommandLog
    {
        void Append(Command c);
    }

    /// <summary>Records nothing: the default and the bot fast path.</summary>
    public sealed class NullCommandLog : ICommandLog
    {
        public static readonly NullCommandLog Instance = new NullCommandLog();
        NullCommandLog() { }
        public void Append(Command c) { }
    }

    /// <summary>In-memory log (bots, tests).</summary>
    public sealed class ListCommandLog : ICommandLog
    {
        readonly List<Command> _records = new List<Command>();

        public IReadOnlyList<Command> Records => _records;
        public int Count => _records.Count;
        public void Append(Command c) => _records.Add(c);
        public void Clear() => _records.Clear();
    }

    /// <summary>The log does not fit the state it is replayed on (a record before the current tick).</summary>
    public sealed class ReplayException : Exception
    {
        public ReplayException(string message) : base(message) { }
    }
}
