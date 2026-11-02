using System;
using System.Collections.Generic;
using System.Threading;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Events;

namespace HeroCat.Runtime.Session
{
    /// <summary>
    /// Runtime-originated notices published on the <see cref="EventBus"/> next to the sim's events (01 sections 2.3 and
    /// 3.1). They are not sim events and never enter the command log or the state hash.
    /// </summary>
    public abstract record RuntimeNotice;

    /// <summary>A command Apply refused (01 section 2.2 rule 1: a reject emits no sim event; the UI reports it, section 7).</summary>
    public sealed record CommandRejected(Command Command, ApplyResult Result) : RuntimeNotice;

    /// <summary>
    /// A command Apply accepted, with the events that command emitted, in order (a copy; the same events are published
    /// again with the frame's drain). Published from SessionRunner.Issue right after the Apply, so a handler sees the
    /// state just after that command. Analytics groups per-command rows with it (05 section 13's <c>fish_release</c> and
    /// <c>fish_merge</c>, 06 section 13's <c>quest_claimed</c> bulk flag). Not a sim event; never logged.
    /// </summary>
    public sealed record CommandApplied(Command Command, IReadOnlyList<Event> Events) : RuntimeNotice;

    /// <summary>SAVE_FAIL_WARN_COUNT consecutive save failures (01 section 3.1).</summary>
    public sealed record SaveWriteFailed(int ConsecutiveFailures) : RuntimeNotice;

    /// <summary>
    /// Typed dispatch of drained sim events and runtime notices to view-models (01 section 2.3). Main thread only: the bus
    /// remembers the thread that created it and throws if published from another. Handlers run in subscription order.
    /// A handler must not mutate the sim: it calls <c>SessionRunner.Enqueue</c>, which applies on the next frame.
    /// Subscribing to a base type (for example <see cref="Event"/>) receives every derived event.
    /// </summary>
    public sealed class EventBus
    {
        sealed class Sub
        {
            public Type Type;
            public Action<object> Invoke;
            public bool Removed;
        }

        readonly List<Sub> _subs = new List<Sub>();
        readonly Dictionary<Type, Sub[]> _byType = new Dictionary<Type, Sub[]>();
        readonly int _thread;
        int _depth;

        public EventBus() { _thread = Thread.CurrentThread.ManagedThreadId; }

        /// <summary>True while handlers are running (SessionRunner refuses a direct Issue then, 01 AC 12).</summary>
        public bool IsPublishing => _depth > 0;

        public IDisposable Subscribe<T>(Action<T> handler) where T : Event => Add(typeof(T), handler);

        public IDisposable SubscribeNotice<T>(Action<T> handler) where T : RuntimeNotice => Add(typeof(T), handler);

        /// <summary>Dispatches each event, in order, to every matching handler.</summary>
        public void Publish(IReadOnlyList<Event> events)
        {
            if (events == null || events.Count == 0) return;
            CheckThread();
            _depth++;
            try
            {
                foreach (var e in events) Dispatch(e);
            }
            finally
            {
                _depth--;
            }
        }

        public void PublishReject(Command command, ApplyResult result) => PublishSystem(new CommandRejected(command, result));

        public void PublishSystem(RuntimeNotice notice)
        {
            if (notice == null) throw new ArgumentNullException(nameof(notice));
            CheckThread();
            _depth++;
            try
            {
                Dispatch(notice);
            }
            finally
            {
                _depth--;
            }
        }

        IDisposable Add<T>(Type type, Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            CheckThread();
            var s = new Sub { Type = type, Invoke = o => handler((T)o) };
            _subs.Add(s);
            _byType.Clear();
            return new Unsubscriber(this, s);
        }

        void Remove(Sub s)
        {
            if (s.Removed) return;
            s.Removed = true;
            _subs.Remove(s);
            _byType.Clear();
        }

        void Dispatch(object item)
        {
            var t = item.GetType();
            if (!_byType.TryGetValue(t, out var list))
            {
                var l = new List<Sub>();
                foreach (var s in _subs) if (s.Type.IsAssignableFrom(t)) l.Add(s);
                list = l.ToArray();
                _byType[t] = list;
            }
            foreach (var s in list)
                if (!s.Removed) s.Invoke(item);
        }

        void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread)
                throw new InvalidOperationException("EventBus is main-thread only (01 section 4); post to the MainThreadDispatcher");
        }

        sealed class Unsubscriber : IDisposable
        {
            readonly EventBus _bus;
            readonly Sub _sub;
            public Unsubscriber(EventBus bus, Sub sub) { _bus = bus; _sub = sub; }
            public void Dispose() => _bus.Remove(_sub);
        }
    }

    /// <summary>
    /// Where background work (HTTP continuations, content download) hands results to the main thread (01 section 4).
    /// <see cref="Post"/> is thread-safe; <see cref="RunPending"/> runs on the main thread at the start of each frame.
    /// Posted actions receive copies, never GameState: they turn results into commands via SessionRunner.Enqueue.
    /// </summary>
    public sealed class MainThreadDispatcher
    {
        readonly object _lock = new object();
        List<Action> _queue = new List<Action>();
        List<Action> _spare = new List<Action>();

        public void Post(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            lock (_lock) _queue.Add(action);
        }

        public int PendingCount
        {
            get { lock (_lock) return _queue.Count; }
        }

        /// <summary>Runs what was posted before this call; actions posted while running wait for the next call.</summary>
        public int RunPending()
        {
            List<Action> run;
            lock (_lock)
            {
                if (_queue.Count == 0) return 0;
                run = _queue;
                _queue = _spare;
                _spare = run;
            }
            int n = run.Count;
            try
            {
                foreach (var a in run) a();
            }
            finally
            {
                run.Clear();
            }
            return n;
        }
    }
}
