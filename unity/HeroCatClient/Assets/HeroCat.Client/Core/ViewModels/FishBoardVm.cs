using System;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Events;
using HeroCat.Sim.Fish;

namespace HeroCat.Client.Core
{
    /// <summary>The drop-target hint under a dragged fish (07 section 5 Dragging row).</summary>
    public enum DropHint : byte { None, Ok, Bad, Neutral }

    /// <summary>How one board cell looks right now.</summary>
    public readonly struct CellLook : IEquatable<CellLook>
    {
        public CellLook(int level, bool selected, bool lifted, DropHint hint, bool capped, bool pulse)
        {
            Level = level;
            Selected = selected;
            Lifted = lifted;
            Hint = hint;
            Capped = capped;
            Pulse = pulse;
        }

        /// <summary>0 = empty.</summary>
        public int Level { get; }

        /// <summary>Tap-selected: ring in <c>accent</c>.</summary>
        public bool Selected { get; }

        /// <summary>The fish being dragged (scale 1.15, follows the finger).</summary>
        public bool Lifted { get; }

        public DropHint Hint { get; }

        /// <summary>At or above the level cap: crown (07 section 5, issue #41).</summary>
        public bool Capped { get; }

        /// <summary>An equal-level fish pulses once while another is selected.</summary>
        public bool Pulse { get; }

        public bool Equals(CellLook o) =>
            Level == o.Level && Selected == o.Selected && Lifted == o.Lifted && Hint == o.Hint && Capped == o.Capped && Pulse == o.Pulse;

        public override bool Equals(object obj) => obj is CellLook o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(Level, Selected, Lifted, (int)Hint, Capped, Pulse);
    }

    /// <summary>The Fish Board gesture states (07 section 5, 05 section 6).</summary>
    public enum BoardState { Idle, Pressing, Selected, Dragging, Info }

    /// <summary>
    /// The Fish Board half of SCR_MAIN (07 sections 4.3 and 5, 05 section 6; issue #114): the tank gauge, the Release /
    /// Release max / Merge all buttons with their disabled reasons, the 5 x 4 cells, and the gesture state machine that
    /// turns touches into exactly 05's commands. The client pre-filters for feedback (a drop on a different level sends
    /// nothing and shakes); the sim stays authoritative and the board always re-renders from the state after a command.
    /// Anchor ids for reject toasts: <see cref="AnchorRelease"/>, <see cref="AnchorReleaseMax"/>, <see cref="AnchorMergeAll"/>,
    /// and <c>"fish.cell.N"</c>.
    /// </summary>
    public sealed class FishBoardVm
    {
        public const string AnchorRelease = "fish.release", AnchorReleaseMax = "fish.release_max", AnchorMergeAll = "fish.merge_all",
                            AnchorDiscard = "fish.discard";

        readonly VmContext _c;
        readonly int[] _levels;
        int _maxLvl, _pairs = -1, _pointer = -1, _pressCell = -1, _infoCell = -1, _hoverCell = -1;
        float _pressX, _pressY;
        double _pressAt;
        bool _locked;

        public FishBoardVm(VmContext c)
        {
            _c = c;
            var k = c.K;
            Width = k.FishBoardW;
            Height = k.FishBoardH;
            _levels = new int[Width * Height];
            Cells = new Bindable<CellLook>[Width * Height];
            for (int i = 0; i < Cells.Length; i++) Cells[i] = new Bindable<CellLook>();
            Release = c.Button();
            ReleaseMax = c.Button();
            MergeAll = c.Button();
            Release.Label.Set(UiKeys.FishRelease);
            ReleaseMax.Label.Set(UiKeys.FishReleaseMax);
            MergeAll.Label.Set(UiKeys.FishMergeAll);
            InfoTitle = c.Label();
            InfoBody = c.Label();
            InfoDiscard = c.Label();
        }

        public int Width { get; }
        public int Height { get; }
        public int CellCount => _levels.Length;

        public readonly Bindable<CellLook>[] Cells;
        public readonly IntLabel TankCount = new IntLabel();
        public readonly IntLabel TankCap = new IntLabel();
        public readonly BarFill TankFill = new BarFill();

        /// <summary>Next-fish countdown (07 section 4.3); hidden while the tank is full.</summary>
        public readonly NumberLabel NextFishSec = new NumberLabel(NumberStyle.Countdown);

        public readonly Flag NextFishVisible = new Flag();
        public readonly ButtonState Release, ReleaseMax, MergeAll;
        public readonly IntLabel MergeAllCount = new IntLabel();

        /// <summary>The long-press info popover (07 section 5 Info row).</summary>
        public readonly Flag InfoVisible = new Flag();

        public readonly TextKeyLabel InfoTitle, InfoBody, InfoDiscard;

        public BoardState State { get; private set; }
        public int SelectedCell { get; private set; } = -1;
        public int DragCell { get; private set; } = -1;
        public int InfoCell => _infoCell;

        /// <summary>Finger position while dragging (reference px), so the view draws the lifted fish there.</summary>
        public float DragX { get; private set; }
        public float DragY { get; private set; }

        public int Level(int cell) => cell >= 0 && cell < _levels.Length ? _levels[cell] : 0;

        // ------------------------------------------------------------------------------------------ refresh

        /// <summary>Copies the board and the tank from the sim (05 section 12 rule 7: the view never decides validity).</summary>
        public void Refresh(Simulation sim, in Snapshot snap)
        {
            var s = sim.State;
            var cells = s.Fish.Cells;
            bool changed = false;
            for (int i = 0; i < _levels.Length; i++)
            {
                int lv = cells != null && i < cells.Length ? cells[i] : 0;
                if (lv != _levels[i]) { _levels[i] = lv; changed = true; }
            }
            int maxLvl = sim.FishMaxLvl;
            if (maxLvl != _maxLvl) { _maxLvl = maxLvl; changed = true; }
            if (changed || _pairs < 0) _pairs = sim.FishMergeablePairs;   // allocates in the sim: only when the board moved
            _locked = FishBoard.IsLocked(s);
            if (SelectedCell >= 0 && _levels[SelectedCell] == 0) { SelectedCell = -1; if (State == BoardState.Selected) State = BoardState.Idle; }

            TankCount.Set(snap.TankFish);
            TankCap.Set(snap.TankCap);
            TankFill.Set(snap.TankFish, snap.TankCap);
            bool full = snap.TankFish >= snap.TankCap;
            NextFishVisible.Set(!full);
            NextFishSec.Set(full ? 0 : Math.Ceiling(snap.TankRefillTicksLeft / (double)_c.K.TickHz));

            int empties = FishBoard.EmptyCells(s);
            int pairs = _pairs;
            MergeAllCount.Set(pairs);
            bool canRelease = !_locked && snap.TankFish > 0 && empties > 0;
            SetReleaseReason(Release, canRelease, snap.TankFish, empties);
            SetReleaseReason(ReleaseMax, canRelease, snap.TankFish, empties);
            MergeAll.Visible.Set(true);
            MergeAll.Enabled.Set(!_locked && pairs > 0);
            if (_locked) MergeAll.Reason.Set(UiKeys.FishBoardLocked);
            else if (pairs > 0) MergeAll.Reason.Set(null);
            else MergeAll.Reason.Set(UiKeys.FishNothingToMerge);
            WriteCells();
        }

        void SetReleaseReason(ButtonState b, bool can, int tank, int empties)
        {
            b.Visible.Set(true);
            b.Enabled.Set(can);
            if (can) b.Reason.Set(null);
            else if (_locked) b.Reason.Set(UiKeys.FishBoardLocked);
            else if (empties == 0) b.Reason.Set(UiKeys.FishBoardFull);
            else b.Reason.Set(UiKeys.FishTankEmpty);   // the countdown sits next to it (section 10 rule 9)
        }

        void WriteCells()
        {
            int selLevel = SelectedCell >= 0 ? _levels[SelectedCell] : 0;
            int dragLevel = DragCell >= 0 ? _levels[DragCell] : 0;
            for (int i = 0; i < _levels.Length; i++)
            {
                int lv = _levels[i];
                DropHint hint = DropHint.None;
                if (State == BoardState.Dragging && i == _hoverCell && i != DragCell)
                    hint = lv == 0 ? DropHint.Neutral : lv == dragLevel ? DropHint.Ok : DropHint.Bad;
                bool pulse = State == BoardState.Selected && i != SelectedCell && lv > 0 && lv == selLevel;
                Cells[i].Set(new CellLook(lv, i == SelectedCell, i == DragCell && State == BoardState.Dragging, hint, lv > 0 && lv >= _maxLvl, pulse));
            }
        }

        // ------------------------------------------------------------------------------------------ buttons (one command each)

        public void OnRelease() => Send(new ReleaseFish(), AnchorRelease);

        /// <summary>One sim command, 05 section 6.1 (issue #11); the view animates the events UI_RELEASE_MAX_ANIM_MS apart.</summary>
        public void OnReleaseMax() => Send(new ReleaseMax(), AnchorReleaseMax);

        /// <summary>One sim command; pair order is 05's MergeAll contract (lowest level first, reading order).</summary>
        public void OnMergeAll() => Send(new MergeAll(), AnchorMergeAll);

        // ------------------------------------------------------------------------------------------ gestures

        /// <summary>Touch down on <paramref name="cell"/> (-1 = off the board) at reference-pixel position x, y.</summary>
        public void PointerDown(int pointerId, int cell, float x, float y, double now)
        {
            if (_locked) return;
            if (_pointer >= 0 && pointerId != _pointer) return;   // while a drag is in progress other board input is ignored
            if (State == BoardState.Info)
            {
                CloseInfo();
                return;
            }
            _pointer = pointerId;
            _pressCell = cell;
            _pressX = x;
            _pressY = y;
            _pressAt = now;
            if (cell >= 0 && Level(cell) > 0)
            {
                if (State != BoardState.Selected) State = BoardState.Pressing;
            }
            WriteCells();
        }

        /// <summary>Finger moved; <paramref name="cell"/> is the cell under it (-1 = none).</summary>
        public void PointerMove(int pointerId, int cell, float x, float y, double now)
        {
            if (pointerId != _pointer) return;
            DragX = x;
            DragY = y;
            if (State != BoardState.Dragging && _pressCell >= 0 && Level(_pressCell) > 0)
            {
                float dx = x - _pressX, dy = y - _pressY;
                if (dx * dx + dy * dy >= UiConstants.DragStartPx * UiConstants.DragStartPx)
                {
                    State = BoardState.Dragging;   // a drag replaces any tap selection
                    SelectedCell = -1;
                    DragCell = _pressCell;
                    _c.Cues?.Feel(Haptic.Light, Sounds.FishPick);
                }
            }
            if (State == BoardState.Dragging) _hoverCell = cell;
            WriteCells();
        }

        /// <summary>Finger lifted over <paramref name="cell"/> (-1 = off the board).</summary>
        public void PointerUp(int pointerId, int cell, float x, float y, double now)
        {
            if (pointerId != _pointer) return;
            _pointer = -1;
            if (State == BoardState.Dragging)
            {
                Drop(cell);
                return;
            }
            if (State == BoardState.Info) return;
            Tap(_pressCell >= 0 ? _pressCell : cell);
        }

        /// <summary>Per frame: the long-press timer (<see cref="UiConstants.LongPressMs"/>).</summary>
        public void Update(double now)
        {
            if (_pointer < 0 || State == BoardState.Dragging || State == BoardState.Info) return;
            if (_pressCell < 0 || Level(_pressCell) == 0) return;
            if ((now - _pressAt) * 1000.0 >= UiConstants.LongPressMs) OpenInfo(_pressCell);
        }

        /// <summary>The app went to the background, or the board hid: a drag is cancelled and nothing was sent (07 section 5).</summary>
        public void Cancel()
        {
            if (State == BoardState.Dragging && DragCell >= 0) _c.Cues?.Push(CueKind.FishSnapBack, DragCell);
            _pointer = -1;
            _pressCell = -1;
            _hoverCell = -1;
            DragCell = -1;
            SelectedCell = -1;
            if (State != BoardState.Info) State = BoardState.Idle;
            WriteCells();
        }

        /// <summary>The popover's <c>[Release back to sea (+N coins)]</c>: the only path to DiscardFish (05).</summary>
        public void OnDiscard()
        {
            if (State != BoardState.Info || _infoCell < 0) return;
            int cell = _infoCell;
            CloseInfo();
            Send(new DiscardFish(cell), AnchorDiscard);
        }

        /// <summary>Tap outside the popover.</summary>
        public void CloseInfo()
        {
            _infoCell = -1;
            InfoVisible.Set(false);
            State = BoardState.Idle;
            _pointer = -1;
            _pressCell = -1;
            WriteCells();
        }

        void Tap(int cell)
        {
            int lv = Level(cell);
            if (State == BoardState.Selected && SelectedCell >= 0)
            {
                int src = SelectedCell;
                SelectedCell = -1;
                State = BoardState.Idle;
                if (cell == src) { WriteCells(); return; }   // tapping the selected fish again deselects it
                if (lv > 0 && lv == _levels[src])
                {
                    if (lv >= _maxLvl) _c.Cues?.Push(CueKind.FishAtCap, cell);
                    else Send(new MergeFish(src, cell), CellAnchor(cell));
                }
                else
                {
                    // Empty space or a different level clears the selection with the invalid feedback (05 section 6).
                    if (cell >= 0) Invalid(cell);
                }
                WriteCells();
                return;
            }
            _pressCell = -1;
            if (lv > 0)
            {
                SelectedCell = cell;
                State = BoardState.Selected;
                _c.Cues?.Feel(Haptic.Selection, Sounds.FishPick);
            }
            else State = BoardState.Idle;
            WriteCells();
        }

        void Drop(int cell)
        {
            int src = DragCell;
            DragCell = -1;
            _hoverCell = -1;
            _pressCell = -1;
            State = BoardState.Idle;
            if (cell < 0 || cell == src || src < 0)
            {
                if (src >= 0) _c.Cues?.Push(CueKind.FishSnapBack, src);
                WriteCells();
                return;
            }
            int lv = _levels[cell], srcLv = _levels[src];
            if (lv == 0)
            {
                _c.Cues?.Feel(Haptic.Light, Sounds.FishDrop);
                Send(new MoveFish(src, cell), CellAnchor(cell));   // 00 decision 22, cosmetic reposition
            }
            else if (lv == srcLv)
            {
                if (lv >= _maxLvl)
                {
                    _c.Cues?.Push(CueKind.FishAtCap, cell);
                    _c.Cues?.Push(CueKind.FishSnapBack, src);
                }
                else Send(new MergeFish(src, cell), CellAnchor(cell));
            }
            else
            {
                Invalid(cell);
                _c.Cues?.Push(CueKind.FishSnapBack, src);
            }
            WriteCells();
        }

        void Invalid(int cell)
        {
            _c.Cues?.Push(CueKind.FishInvalid, cell);
            _c.Cues?.Feel(Haptic.Failure, null);
        }

        void OpenInfo(int cell)
        {
            int lv = Level(cell);
            _infoCell = cell;
            State = BoardState.Info;
            SelectedCell = -1;
            _pointer = -1;
            _pressCell = -1;
            InfoVisible.Set(true);
            var db = _c.Db;
            var def = lv >= 1 && lv <= db.FishLevels.Count ? db.FishLevels[lv - 1] : null;
            InfoTitle.Set(def?.NameKey ?? UiKeys.FishLevel, lv);
            double board = def != null ? def.BoardDpsPct : 0;
            double missile = def != null ? def.MissilePct : 0;
            InfoBody.Set(UiKeys.FishInfoBody, LabelArg.Num(missile, NumberStyle.PctPlain), LabelArg.Num(board, NumberStyle.PctPlain));
            double coins = def != null ? def.DiscoveryCoins * _c.K.FishDiscardCoinsPct : 0;
            InfoDiscard.Set(UiKeys.FishDiscard, LabelArg.Num(Math.Floor(coins)));
            _c.Cues?.Feel(Haptic.Selection, null);
            WriteCells();
        }

        static string CellAnchor(int cell) => "fish.cell." + cell;

        void Send(Command c, string anchor) => _c.Commands?.Send(c, anchor);

        // ------------------------------------------------------------------------------------------ events

        public void OnEvent(Event e)
        {
            switch (e)
            {
                case FishReleased r:
                    _c.Cues?.Push(CueKind.FishPop, r.Cell, level: r.Level);
                    break;
                case FishMerged m:
                    _c.Cues?.Push(CueKind.FishMerge, m.Target, a: m.Source, level: m.NewLevel, value: m.Coins);
                    _c.Cues?.Feel(Haptic.Medium, Sounds.FishMerge(m.NewLevel));
                    break;
                case FishMoved mv:
                    _c.Cues?.Push(CueKind.FishMove, mv.Target, a: mv.Source, level: mv.Level);
                    break;
                case FishDiscarded d:
                    _c.Cues?.Push(CueKind.FishDiscard, d.Cell, level: d.Level, value: d.Coins);
                    break;
                case FishDiscovered fd:
                    _c.Cues?.Push(CueKind.FishDiscovered, level: fd.Level, value: fd.Coins);
                    _c.Cues?.Feel(Haptic.Success, Sounds.FishDiscover);
                    break;
            }
        }

        /// <summary>A rejected release on a full board: the merge-all button pulses once (07 section 5 Release row).</summary>
        public void OnRejected(ApplyResult r)
        {
            if (r.Reason == FishRejectReasons.BOARD_FULL) _c.Cues?.Push(CueKind.MergeAllPulse);
        }
    }
}
