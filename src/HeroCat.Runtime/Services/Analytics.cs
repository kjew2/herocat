using System;
using System.Collections.Generic;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// The analytics seam (02 section 14): a snake_case event name (<see cref="AnalyticsNames"/>) plus the owner section's
    /// camelCase properties; typed events go through <see cref="AnalyticsExtensions.Track(IAnalytics, AnalyticsEvent)"/>.
    /// The sink adds the envelope (<see cref="AnalyticsSink"/>). Implementations never throw and never call back into the sim.
    /// </summary>
    public interface IAnalytics
    {
        void Track(string name, IReadOnlyDictionary<string, object> props = null);
        void Flush();
    }

    /// <summary>Drops everything. Default when no sink is supplied (a Local build has no backend, 01 section 1.2).</summary>
    public sealed class NullAnalytics : IAnalytics
    {
        public static readonly NullAnalytics Instance = new NullAnalytics();
        public void Track(string name, IReadOnlyDictionary<string, object> props = null) { }
        public void Flush() { }
    }

    /// <summary>In-memory list (bot harness, tests): no envelope, no queue, no files. Flush raises <see cref="Flushing"/>.</summary>
    public sealed class MemoryAnalytics : IAnalytics, IAnalyticsFlushHook
    {
        public readonly struct Entry
        {
            public Entry(string name, IReadOnlyDictionary<string, object> props) { Name = name; Props = props; }
            public string Name { get; }
            public IReadOnlyDictionary<string, object> Props { get; }
            public override string ToString() => Name;
        }

        public List<Entry> Events { get; } = new List<Entry>();

        public event Action Flushing;

        public int Flushes { get; private set; }

        public void Track(string name, IReadOnlyDictionary<string, object> props = null) =>
            Events.Add(new Entry(name, props ?? new Dictionary<string, object>()));

        public void Flush()
        {
            Flushing?.Invoke();
            Flushes++;
        }

        public int Count(string name)
        {
            int n = 0;
            foreach (var e in Events) if (e.Name == name) n++;
            return n;
        }
    }
}
