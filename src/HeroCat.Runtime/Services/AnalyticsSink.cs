using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace HeroCat.Runtime.Services
{
    /// <summary>One upload: at most ANALYTICS_BATCH_MAX JSON events, oldest first.</summary>
    public sealed class AnalyticsBatch
    {
        public AnalyticsBatch(string requestId, IReadOnlyList<QueuedEvent> events)
        {
            RequestId = requestId;
            Events = events;
        }

        /// <summary>
        /// The <c>X-Request-Id</c> (32 hex): SHA-256 of the batch's event ids, so a retry of the same events reuses it and a
        /// different set never does (08 section 1.1 idempotency; the server also dedupes per <c>eventId</c>).
        /// </summary>
        public string RequestId { get; }

        public IReadOnlyList<QueuedEvent> Events { get; }

        public static string RequestIdFor(IReadOnlyList<QueuedEvent> events)
        {
            var sb = new StringBuilder();
            foreach (var e in events) sb.Append(e.Id).Append(',');
            byte[] h;
            using (var sha = SHA256.Create()) h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            var hex = new StringBuilder(32);
            for (int i = 0; i < 16; i++) hex.Append(h[i].ToString("x2"));
            return hex.ToString();
        }
    }

    public enum UploadOutcome
    {
        /// <summary>Stored (or already stored): the events leave the queue.</summary>
        Accepted,

        /// <summary>Offline, timeout, 5xx, 429: the batch stays at the head and the next flush retries it.</summary>
        Retry,

        /// <summary>A non-retryable 4xx for the whole batch: dropped so one bad batch cannot block the queue.</summary>
        Rejected,
    }

    /// <summary>
    /// The remote side of the sink (02 section 14.2): Firebase for every event and the backend events route for the
    /// economy ones (<see cref="AnalyticsNames.IsEconomy"/>; route per #209). Called on the thread pool. TODO(#143): the
    /// ApiClient-based uploader; until then only <see cref="MemoryAnalyticsUploader"/> exists.
    /// </summary>
    public interface IAnalyticsUploader
    {
        Task<UploadOutcome> UploadAsync(AnalyticsBatch batch, CancellationToken ct = default);
    }

    /// <summary>A local fake: records each batch; <see cref="Next"/> scripts the outcomes (tests).</summary>
    public sealed class MemoryAnalyticsUploader : IAnalyticsUploader
    {
        readonly object _lock = new object();
        public List<AnalyticsBatch> Batches { get; } = new List<AnalyticsBatch>();

        /// <summary>Outcomes to return, in order; empty: Accepted.</summary>
        public Queue<UploadOutcome> Next { get; } = new Queue<UploadOutcome>();

        /// <summary>When set, thrown instead (a transport failure).</summary>
        public Exception Throw { get; set; }

        public Task<UploadOutcome> UploadAsync(AnalyticsBatch batch, CancellationToken ct = default)
        {
            lock (_lock)
            {
                Batches.Add(batch);
                if (Throw != null) throw Throw;
                return Task.FromResult(Next.Count > 0 ? Next.Dequeue() : UploadOutcome.Accepted);
            }
        }
    }

    /// <summary>
    /// Raised before a flush so in-memory aggregates (06 section 13's KILL/BOSS <c>currency_changed</c> window) reach the
    /// queue first. <see cref="AnalyticsSink"/> and <see cref="MemoryAnalytics"/> implement it.
    /// </summary>
    public interface IAnalyticsFlushHook
    {
        event Action Flushing;
    }

    /// <summary>
    /// The analytics sink of 02 section 14 (issue #155): adds the 14.1 envelope and an <c>eventId</c> to every event,
    /// serialises it to one JSON line in the bounded, persisted <see cref="AnalyticsQueue"/>, and on <see cref="Flush"/>
    /// uploads it in batches of at most ANALYTICS_BATCH_MAX. Track runs on the main thread; the upload runs on the thread
    /// pool (01 threading table). A retryable failure stops the flush and the batch is retried on the next one; events
    /// survive a kill on disk. Never throws, never calls back into the sim. A Local build without a backend uses
    /// <see cref="NullAnalytics"/> instead (01 section 1.2).
    /// </summary>
    public sealed class AnalyticsSink : IAnalytics, IAnalyticsFlushHook
    {
        public const string NAME = "name";
        public const string EVENT_ID = "eventId";
        public const string PROPS = "props";

        static readonly JsonSerializer Json = JsonSerializer.Create(new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
            FloatFormatHandling = FloatFormatHandling.String,
        });

        readonly AnalyticsEnvelope _envelope;
        readonly IAnalyticsUploader _uploader;
        readonly int _batchMax;
        readonly object _flushLock = new object();
        Task _flushing = Task.CompletedTask;
        bool _running, _again;

        public AnalyticsSink(AnalyticsEnvelope envelope, AnalyticsQueue queue, IAnalyticsUploader uploader,
                             int batchMax = RuntimeConfig.ANALYTICS_BATCH_MAX)
        {
            _envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            Queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _uploader = uploader;
            if (batchMax <= 0 || batchMax > RuntimeConfig.ANALYTICS_BATCH_MAX) throw new ArgumentOutOfRangeException(nameof(batchMax));
            _batchMax = batchMax;
        }

        /// <summary>The queue over <c>{dataDir}/analytics/queue.ndjson</c>, resumed from disk.</summary>
        public static AnalyticsQueue FileQueue(string dataDir) => new AnalyticsQueue(new FileAnalyticsQueueStore(dataDir), EventIdOf);

        public static AnalyticsQueue MemoryQueue(MemoryAnalyticsQueueStore store = null) => new AnalyticsQueue(store, EventIdOf);

        public AnalyticsEnvelope Envelope => _envelope;
        public AnalyticsQueue Queue { get; }

        /// <summary>Tracks dropped because the name was not snake_case or a prop could not be serialised.</summary>
        public int DroppedInvalid { get; private set; }

        /// <summary>Props dropped because they named an envelope field, the raw playerId, or were not camelCase.</summary>
        public int DroppedProps { get; private set; }

        public int UploadFailures { get; private set; }
        public int BatchesRejected { get; private set; }

        public event Action Flushing;

        public void Track(string name, IReadOnlyDictionary<string, object> props = null)
        {
            try
            {
                if (!AnalyticsNames.IsSnakeCase(name))
                {
                    DroppedInvalid++;
                    return;
                }
                var id = Guid.NewGuid().ToString("N");
                var o = new JObject { [NAME] = name, [EVENT_ID] = id };
                var env = new Dictionary<string, object>();
                _envelope.WriteTo(env);
                foreach (var f in AnalyticsEnvelope.Fields) o[f] = ToToken(env[f]);
                var p = new JObject();
                if (props != null)
                    foreach (var kv in props)
                    {
                        if (!AnalyticsNames.IsCamelCase(kv.Key) || kv.Key == "playerId" || env.ContainsKey(kv.Key))
                        {
                            DroppedProps++;
                            continue;
                        }
                        p[kv.Key] = ToToken(kv.Value);
                    }
                o[PROPS] = p;
                Queue.Enqueue(new QueuedEvent(id, o.ToString(Formatting.None)));
            }
            catch (Exception)
            {
                DroppedInvalid++;   // a prop Json.NET cannot write; analytics never throws into the game
            }
        }

        /// <summary>Pause, quit (01 section 3.3) and any other flush point: aggregates first, then a background upload.</summary>
        public void Flush()
        {
            RaiseFlushing();
            lock (_flushLock)
            {
                if (_running)
                {
                    _again = true;   // the running loop drains once more before it stops
                    return;
                }
                _running = true;
                _flushing = Task.Run(DrainLoopAsync);
            }
        }

        /// <summary>The background upload started by the last <see cref="Flush"/> (tests and shutdown wait on it).</summary>
        public Task Idle
        {
            get { lock (_flushLock) return _flushing; }
        }

        /// <summary><see cref="Flush"/>, then the upload until the queue is empty or a retryable failure stops it.</summary>
        public Task FlushAsync()
        {
            Flush();
            return Idle;
        }

        async Task DrainLoopAsync()
        {
            while (true)
            {
                try
                {
                    await DrainAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    UploadFailures++;   // the loop must reach the lock below, or no later flush would start
                }
                lock (_flushLock)
                {
                    if (!_again)
                    {
                        _running = false;
                        return;
                    }
                    _again = false;
                }
            }
        }

        async Task DrainAsync()
        {
            if (_uploader == null) return;   // no remote sink yet: the events wait on disk
            while (true)
            {
                var events = Queue.Peek(_batchMax);
                if (events.Count == 0) return;
                UploadOutcome outcome;
                try
                {
                    outcome = await _uploader.UploadAsync(new AnalyticsBatch(AnalyticsBatch.RequestIdFor(events), events)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    outcome = UploadOutcome.Retry;   // analytics never throws into the game (08 classifies on the uploader side)
                }
                if (outcome == UploadOutcome.Retry)
                {
                    UploadFailures++;
                    return;
                }
                if (outcome == UploadOutcome.Rejected) BatchesRejected++;
                Queue.Remove(events);
            }
        }

        void RaiseFlushing()
        {
            try
            {
                Flushing?.Invoke();
            }
            catch (Exception)
            {
                // a hook must not break the flush (analytics never throws into the game)
            }
        }

        static JToken ToToken(object v) => v == null ? JValue.CreateNull() : JToken.FromObject(v, Json);

        /// <summary>The <c>eventId</c> of a persisted line; null when the line does not parse (a torn tail after a kill).</summary>
        public static string EventIdOf(string line)
        {
            try
            {
                var o = JObject.Parse(line);
                var id = o.Value<string>(EVENT_ID);
                return string.IsNullOrEmpty(id) ? null : id;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
