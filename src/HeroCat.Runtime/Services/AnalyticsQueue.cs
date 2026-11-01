using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// Where the offline queue persists (02 section 14.2): one JSON event per line. Implementations may throw IOException;
    /// <see cref="AnalyticsQueue"/> catches it and keeps the events in memory.
    /// </summary>
    public interface IAnalyticsQueueStore
    {
        /// <summary>Every line on disk, oldest first; empty when there is no file.</summary>
        IReadOnlyList<string> Load();

        void Append(string line);

        /// <summary>Replaces the file with <paramref name="lines"/> (temp file, then rename).</summary>
        void Rewrite(IReadOnlyList<string> lines);
    }

    /// <summary>Tests and the bot harness: nothing reaches the disk.</summary>
    public sealed class MemoryAnalyticsQueueStore : IAnalyticsQueueStore
    {
        public List<string> Lines { get; } = new List<string>();

        public IReadOnlyList<string> Load() => Lines.ToArray();
        public void Append(string line) => Lines.Add(line);

        public void Rewrite(IReadOnlyList<string> lines)
        {
            Lines.Clear();
            Lines.AddRange(lines);
        }
    }

    /// <summary>
    /// <c>{dataDir}/analytics/queue.ndjson</c> (02 section 8.4, excluded from OS backup like the saves). Appends one line
    /// per event; a kill mid-append leaves at most one torn last line, which the loader skips.
    /// </summary>
    public sealed class FileAnalyticsQueueStore : IAnalyticsQueueStore
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public FileAnalyticsQueueStore(string dataDir)
        {
            if (dataDir == null) throw new ArgumentNullException(nameof(dataDir));
            Directory = Path.Combine(dataDir, "analytics");
            FilePath = Path.Combine(Directory, "queue.ndjson");
            TempPath = FilePath + ".tmp";
        }

        public string Directory { get; }
        public string FilePath { get; }
        public string TempPath { get; }

        public IReadOnlyList<string> Load()
        {
            if (File.Exists(TempPath)) File.Delete(TempPath);   // a crash mid-rewrite: the old file is still whole
            if (!File.Exists(FilePath)) return Array.Empty<string>();
            return File.ReadAllLines(FilePath, Utf8);
        }

        public void Append(string line)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(FilePath, line + "\n", Utf8);
        }

        public void Rewrite(IReadOnlyList<string> lines)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append(l).Append('\n');
            File.WriteAllText(TempPath, sb.ToString(), Utf8);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(TempPath, FilePath);
        }
    }

    /// <summary>One queued event: its id (32 hex, the server's dedupe key) and its JSON line.</summary>
    public readonly struct QueuedEvent
    {
        public QueuedEvent(string id, string line) { Id = id; Line = line; }
        public string Id { get; }
        public string Line { get; }
    }

    /// <summary>
    /// The bounded offline queue of 02 section 14.2: at most <see cref="Capacity"/> events (ANALYTICS_QUEUE_MAX), the oldest
    /// dropped past it, persisted through an <see cref="IAnalyticsQueueStore"/>. Thread-safe: the main thread enqueues, the
    /// flush on the thread pool peeks and removes. The file may hold up to twice the cap between compactions; a load keeps
    /// the newest <see cref="Capacity"/> lines. A store failure never throws: the events stay in memory and
    /// <see cref="StoreErrors"/> counts it.
    /// </summary>
    public sealed class AnalyticsQueue
    {
        readonly object _lock = new object();
        readonly LinkedList<QueuedEvent> _events = new LinkedList<QueuedEvent>();
        readonly IAnalyticsQueueStore _store;
        readonly Func<string, string> _idOf;
        int _fileLines;

        /// <param name="idOf">Reads a persisted line's event id; null for a line that does not parse (skipped on load).</param>
        public AnalyticsQueue(IAnalyticsQueueStore store, Func<string, string> idOf, int capacity = RuntimeConfig.ANALYTICS_QUEUE_MAX)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _store = store ?? new MemoryAnalyticsQueueStore();
            _idOf = idOf ?? throw new ArgumentNullException(nameof(idOf));
            Capacity = capacity;
            Load();
        }

        public int Capacity { get; }

        /// <summary>Events dropped because the queue was full (oldest first), this process.</summary>
        public long Dropped { get; private set; }

        /// <summary>Lines skipped on load because they did not parse (a torn last line after a kill).</summary>
        public int SkippedOnLoad { get; private set; }

        public int StoreErrors { get; private set; }

        public int Count
        {
            get { lock (_lock) return _events.Count; }
        }

        public void Enqueue(QueuedEvent e)
        {
            lock (_lock)
            {
                _events.AddLast(e);
                bool dropped = false;
                while (_events.Count > Capacity)
                {
                    _events.RemoveFirst();
                    Dropped++;
                    dropped = true;
                }
                try
                {
                    if (dropped && _fileLines >= 2 * Capacity) RewriteLocked();
                    else
                    {
                        _store.Append(e.Line);
                        _fileLines++;
                    }
                }
                catch (Exception x) when (IsIo(x))
                {
                    StoreErrors++;
                }
            }
        }

        /// <summary>The oldest <paramref name="max"/> events, oldest first (they stay queued until <see cref="Remove"/>).</summary>
        public IReadOnlyList<QueuedEvent> Peek(int max)
        {
            lock (_lock)
            {
                var r = new List<QueuedEvent>(Math.Min(max, _events.Count));
                for (var n = _events.First; n != null && r.Count < max; n = n.Next) r.Add(n.Value);
                return r;
            }
        }

        /// <summary>Removes the events with these ids (those dropped meanwhile are already gone) and compacts the file.</summary>
        public void Remove(IReadOnlyList<QueuedEvent> sent)
        {
            if (sent == null || sent.Count == 0) return;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in sent) ids.Add(e.Id);
            lock (_lock)
            {
                for (var n = _events.First; n != null;)
                {
                    var next = n.Next;
                    if (ids.Contains(n.Value.Id)) _events.Remove(n);
                    n = next;
                }
                try
                {
                    RewriteLocked();
                }
                catch (Exception x) when (IsIo(x))
                {
                    StoreErrors++;
                }
            }
        }

        void Load()
        {
            IReadOnlyList<string> lines;
            try
            {
                lines = _store.Load();
            }
            catch (Exception x) when (IsIo(x))
            {
                StoreErrors++;
                return;
            }
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var id = _idOf(line);
                if (id == null)
                {
                    SkippedOnLoad++;
                    continue;
                }
                _events.AddLast(new QueuedEvent(id, line));
                if (_events.Count > Capacity)
                {
                    _events.RemoveFirst();
                    Dropped++;
                }
            }
            _fileLines = lines.Count;
            if (SkippedOnLoad > 0 || lines.Count > _events.Count)
            {
                try
                {
                    RewriteLocked();
                }
                catch (Exception x) when (IsIo(x))
                {
                    StoreErrors++;
                }
            }
        }

        void RewriteLocked()
        {
            var lines = new List<string>(_events.Count);
            foreach (var e in _events) lines.Add(e.Line);
            _store.Rewrite(lines);
            _fileLines = lines.Count;
        }

        static bool IsIo(Exception x) => x is IOException || x is UnauthorizedAccessException || x is NotSupportedException;
    }
}
