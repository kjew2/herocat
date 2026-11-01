using System.Collections.Generic;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// specs/08 section 8, trimmed for the POC: snake_case event name plus a flat property bag. Implementations
    /// never throw and never call back into the sim.
    /// </summary>
    public interface IAnalytics
    {
        void Track(string name, IReadOnlyDictionary<string, object> props = null);
        void Flush();
    }

    /// <summary>Drops everything. Default when no sink is supplied.</summary>
    public sealed class NullAnalytics : IAnalytics
    {
        public static readonly NullAnalytics Instance = new NullAnalytics();
        public void Track(string name, IReadOnlyDictionary<string, object> props = null) { }
        public void Flush() { }
    }

    /// <summary>In-memory list (bot harness, tests).</summary>
    public sealed class MemoryAnalytics : IAnalytics
    {
        public readonly struct Entry
        {
            public Entry(string name, IReadOnlyDictionary<string, object> props) { Name = name; Props = props; }
            public string Name { get; }
            public IReadOnlyDictionary<string, object> Props { get; }
            public override string ToString() => Name;
        }

        public List<Entry> Events { get; } = new List<Entry>();

        public void Track(string name, IReadOnlyDictionary<string, object> props = null) =>
            Events.Add(new Entry(name, props ?? new Dictionary<string, object>()));

        public void Flush() { }

        public int Count(string name)
        {
            int n = 0;
            foreach (var e in Events) if (e.Name == name) n++;
            return n;
        }
    }
}
