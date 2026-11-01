using System.Collections.Generic;

namespace HeroCat.Client.Core
{
    /// <summary>Picks a toast's styling (07 section 10 rule 3; for a reject, 01's RejectCode category picks it).</summary>
    public enum ToastStyle { Info, Reward, Rare, Reject, Error }

    /// <summary>The toast to draw now. <see cref="AnchorId"/> is set for a reject: it sits under the pressed button.</summary>
    public readonly struct Toast
    {
        public Toast(string text, ToastStyle style, string anchorId, int repeat, int serial)
        {
            Text = text;
            Style = style;
            AnchorId = anchorId;
            Repeat = repeat;
            Serial = serial;
        }

        public string Text { get; }
        public ToastStyle Style { get; }
        public string AnchorId { get; }

        /// <summary>Identical consecutive toasts collapse: 3 means "x3".</summary>
        public int Repeat { get; }

        /// <summary>Changes when a different toast (or a new repeat count) shows, so the view knows to re-animate.</summary>
        public int Serial { get; }

        /// <summary>Text plus the " x3" collapse suffix.</summary>
        public string Display => Repeat > 1 ? Text + " x" + Repeat : Text;
    }

    /// <summary>
    /// Toasts (07 section 10 rule 3; issue #109): one at a time; 2 s (<see cref="UiConstants.ToastSec"/>), 4 s for L/M
    /// acquisitions; identical consecutive toasts collapse into one with "x3"; toasts never carry actions. Text is already
    /// localized by the caller. Time is supplied by the caller (seconds, any monotonic origin).
    /// </summary>
    public sealed class ToastQueue
    {
        sealed class Item
        {
            public string Text, Anchor;
            public ToastStyle Style;
            public double Duration;
            public int Repeat = 1;
        }

        readonly Queue<Item> _queue = new Queue<Item>();
        Item _current, _last;
        double _shownAt;
        int _serial;

        /// <summary>A flood never builds an endless backlog: older queued toasts are dropped first.</summary>
        public const int MaxQueued = 6;

        public int QueuedCount => _queue.Count;

        public void Push(string text, double durationS = UiConstants.ToastSec) => Push(text, ToastStyle.Info, durationS);

        public void Push(string text, ToastStyle style, double durationS = UiConstants.ToastSec, string anchorId = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_last != null && _last.Text == text && _last.Anchor == anchorId && (_last == _current || _queue.Contains(_last)))
            {
                _last.Repeat++;
                if (_last == _current) _serial++;
                if (durationS > _last.Duration) _last.Duration = durationS;
                return;
            }
            if (_queue.Count >= MaxQueued) _queue.Dequeue();
            _last = new Item { Text = text, Style = style, Duration = durationS, Anchor = anchorId };
            _queue.Enqueue(_last);
        }

        /// <summary>The toast to display at <paramref name="now"/>; false when none.</summary>
        public bool TryCurrent(double now, out Toast toast)
        {
            if (_current != null && now - _shownAt >= _current.Duration) _current = null;
            if (_current == null && _queue.Count > 0)
            {
                _current = _queue.Dequeue();
                _shownAt = now;
                _serial++;
            }
            if (_current == null)
            {
                toast = default;
                return false;
            }
            toast = new Toast(_current.Text, _current.Style, _current.Anchor, _current.Repeat, _serial);
            return true;
        }

        /// <summary>The display text at <paramref name="now"/>, or null (allocates for a collapsed toast).</summary>
        public string Current(double now) => TryCurrent(now, out var t) ? t.Display : null;

        public void Clear()
        {
            _queue.Clear();
            _current = null;
            _last = null;
        }
    }
}
