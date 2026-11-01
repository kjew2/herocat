using System;
using HeroCat.Sim.Content;

namespace HeroCat.Client.Core
{
    /// <summary>A summon card's face (07 section 4.11): face down, the L/M border glow, flipping, face up.</summary>
    public enum CardState { FaceDown, Holding, Flipping, FaceUp }

    /// <summary>
    /// The summon reveal timeline (07 section 6, AC 10 and AC 15; issue #117), engine free. The result is already applied
    /// when it starts: the reveal is theatre and never gates state.
    /// <list type="number">
    /// <item>Single pull: one card, a 0.2 s pause, a 0.3 s flip, 0.6 s in total.</item>
    /// <item>10-pull: card i starts at 0.3 + 0.12 i s, +0.4 s per preceding L/M card; an L/M card holds a 0.4 s glow
    /// before its own 0.3 s flip.</item>
    /// <item>Reduced motion: no stagger and no hold; every card flips together, done by 0.6 s.</item>
    /// <item>Skip faces every card up in the same frame. A tap completes the flipping card and halves the remaining
    /// stagger; a second tap equals Skip.</item>
    /// </list>
    /// Time is the caller's seconds since <see cref="Start"/>'s <c>now</c>.
    /// </summary>
    public sealed class RevealSequencer
    {
        Rarity[] _rarity = Array.Empty<Rarity>();
        double[] _start = Array.Empty<double>();   // when the card's sequence starts (hold, then flip)
        double[] _flipAt = Array.Empty<double>();
        CardState[] _state = Array.Empty<CardState>();
        double _t0, _end;
        int _taps;

        public int Count => _rarity.Length;
        public bool Running { get; private set; }
        public bool Complete { get; private set; }

        /// <summary>Raised once per card as it turns face up (rarity flashes, sounds).</summary>
        public Action<int, Rarity> CardRevealed { get; set; }

        public CardState State(int i) => _state[i];

        /// <summary>Start of card <paramref name="i"/>'s sequence, seconds after Start.</summary>
        public double StartOf(int i) => _start[i] - _t0;

        public double FlipOf(int i) => _flipAt[i] - _t0;

        /// <summary>When the whole reveal is face up, seconds after Start.</summary>
        public double Duration => _end - _t0;

        public static bool IsLplus(Rarity r) => r >= Rarity.LEGENDARY;

        public void Start(Rarity[] cards, double now, bool reducedMotion)
        {
            int n = cards?.Length ?? 0;
            _rarity = n == 0 ? Array.Empty<Rarity>() : (Rarity[])cards.Clone();
            _start = new double[n];
            _flipAt = new double[n];
            _state = new CardState[n];
            _t0 = now;
            _taps = 0;
            Complete = false;
            Running = n > 0;
            if (n == 0) { Complete = true; _end = now; return; }

            if (n == 1)
            {
                _start[0] = now + UiConstants.RevealSinglePauseSec;
                _flipAt[0] = _start[0] + (!reducedMotion && IsLplus(_rarity[0]) ? UiConstants.RevealLplusHoldSec : 0);
                _end = Math.Max(now + UiConstants.RevealSingleTotalSec, _flipAt[0] + UiConstants.RevealFlipSec);
                return;
            }

            double shift = 0;
            for (int i = 0; i < n; i++)
            {
                if (reducedMotion)
                {
                    _start[i] = now + UiConstants.RevealFirstCardSec;
                    _flipAt[i] = _start[i];
                    continue;
                }
                _start[i] = now + UiConstants.RevealFirstCardSec + UiConstants.RevealStaggerSec * i + shift;
                bool hold = IsLplus(_rarity[i]);
                _flipAt[i] = _start[i] + (hold ? UiConstants.RevealLplusHoldSec : 0);
                if (hold) shift += UiConstants.RevealLplusHoldSec;
            }
            _end = 0;
            for (int i = 0; i < n; i++) _end = Math.Max(_end, _flipAt[i] + UiConstants.RevealFlipSec);
        }

        /// <summary>Advances the card states to <paramref name="now"/>.</summary>
        public void Update(double now)
        {
            if (!Running) return;
            for (int i = 0; i < _state.Length; i++)
            {
                if (_state[i] == CardState.FaceUp) continue;
                CardState s;
                if (now >= _flipAt[i] + UiConstants.RevealFlipSec) s = CardState.FaceUp;
                else if (now >= _flipAt[i]) s = CardState.Flipping;
                else if (now >= _start[i] && _flipAt[i] > _start[i]) s = CardState.Holding;
                else s = CardState.FaceDown;
                Set(i, s);
            }
            if (now >= _end) Finish();
        }

        /// <summary>
        /// A tap during the sequence (rule 5): completes the current card's flip and halves the remaining stagger; the second
        /// tap equals Skip.
        /// </summary>
        public void Tap(double now)
        {
            if (!Running) return;
            _taps++;
            if (_taps >= 2 || _state.Length == 1) { Skip(); return; }
            Update(now);
            int next = -1;
            for (int i = 0; i < _state.Length; i++)
            {
                if (_state[i] == CardState.Flipping || _state[i] == CardState.Holding) Set(i, CardState.FaceUp);
                else if (_state[i] == CardState.FaceDown && next < 0) next = i;
            }
            if (next < 0) { Finish(); return; }
            double shift = 0;
            double half = UiConstants.RevealStaggerSec / 2;
            for (int i = next; i < _state.Length; i++)
            {
                bool hold = _flipAt[i] > _start[i];   // an L/M card keeps its glow (only the stagger halves)
                _start[i] = now + half * (i - next) + shift;
                _flipAt[i] = _start[i] + (hold ? UiConstants.RevealLplusHoldSec : 0);
                if (hold) shift += UiConstants.RevealLplusHoldSec;
            }
            _end = now;
            for (int i = 0; i < _state.Length; i++)
                if (_state[i] != CardState.FaceUp) _end = Math.Max(_end, _flipAt[i] + UiConstants.RevealFlipSec);
        }

        /// <summary>Rule 4: every card face up in this frame, transitions disabled, summary shown.</summary>
        public void Skip()
        {
            if (!Running && Complete) return;
            for (int i = 0; i < _state.Length; i++) Set(i, CardState.FaceUp);
            Finish();
        }

        void Set(int i, CardState s)
        {
            if (_state[i] == s) return;
            _state[i] = s;
            if (s == CardState.FaceUp) CardRevealed?.Invoke(i, _rarity[i]);
        }

        void Finish()
        {
            Running = false;
            Complete = true;
        }
    }
}
