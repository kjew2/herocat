using System.Collections.Generic;
using System.Text;

namespace HeroCat.Client.Core
{
    /// <summary>Ring buffer of the last N feed lines, newest last (specs/07 section 8.1).</summary>
    public sealed class EventFeed
    {
        readonly string[] _lines;
        int _start, _count;

        public EventFeed(int capacity = 8) { _lines = new string[capacity]; }

        /// <summary>Bumped on every change so screens can skip rebuilding the text.</summary>
        public int Version { get; private set; }

        public void Add(string line)
        {
            if (_count < _lines.Length) _lines[(_start + _count++) % _lines.Length] = line;
            else { _lines[_start] = line; _start = (_start + 1) % _lines.Length; }
            Version++;
        }

        public void Clear() { _start = 0; _count = 0; Version++; }

        public string Text()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(_lines[(_start + i) % _lines.Length]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// One toast at a time, each shown for its duration; identical consecutive toasts collapse into one with
    /// an "xN" suffix (specs/07 section 8.1). Time is supplied by the caller (seconds, any monotonic origin).
    /// </summary>
    public sealed class ToastQueue
    {
        sealed class Item { public string Text; public double Duration; public int Repeat = 1; }

        readonly Queue<Item> _queue = new Queue<Item>();
        Item _current, _last;
        double _shownAt;

        public const int MaxQueued = 6;

        public void Push(string text, double durationS = 2.0)
        {
            if (_last != null && _last.Text == text && (_last == _current || _queue.Contains(_last)))
            {
                _last.Repeat++;
                return;
            }
            if (_queue.Count >= MaxQueued) _queue.Dequeue();   // never let a flood build an endless backlog
            _last = new Item { Text = text, Duration = durationS };
            _queue.Enqueue(_last);
        }

        /// <summary>The toast to display at <paramref name="now"/>, or null.</summary>
        public string Current(double now)
        {
            if (_current != null && now - _shownAt >= _current.Duration) _current = null;
            if (_current == null && _queue.Count > 0)
            {
                _current = _queue.Dequeue();
                _shownAt = now;
            }
            if (_current == null) return null;
            return _current.Repeat > 1 ? _current.Text + "  x" + _current.Repeat : _current.Text;
        }

        public void Clear() { _queue.Clear(); _current = null; _last = null; }
    }
}
