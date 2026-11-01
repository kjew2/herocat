using System.Linq;
using HeroCat.Client.Core;
using HeroCat.Sim.Commands;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>The Fish Board interaction states (07 section 5, AC 16; 05 section 6; issue #114) on a scripted board.</summary>
    public class ClientFishBoardTests
    {
        // Row-major 5 x 4: cells 0 and 1 hold level 1, cell 2 level 2, cell 3 level 1; the rest empty.
        static ClientFixture Scripted(int tank = 3) => ClientFixture.Open(s =>
        {
            for (int i = 0; i < s.Fish.Cells.Length; i++) s.Fish.Cells[i] = 0;
            s.Fish.Cells[0] = 1;
            s.Fish.Cells[1] = 1;
            s.Fish.Cells[2] = 2;
            s.Fish.Cells[3] = 1;
            s.Fish.TankCount = tank;
            s.Fish.DiscoveredLevels.Add(1);
            s.Fish.DiscoveredLevels.Add(2);
            s.Fish.HighestLevel = 2;
        });

        static void Drag(ClientFixture f, int from, int to)
        {
            var b = f.Vm.Board;
            double t = f.Now;
            b.PointerDown(0, from, 0, 0, t);
            b.PointerMove(0, from, 5, 0, t + 0.01);
            b.PointerMove(0, to, 40, 0, t + 0.02);
            b.PointerUp(0, to, 40, 0, t + 0.03);
        }

        [Test]
        public void AC16_DragOntoEqualLevel_SendsExactlyOneMergeFish()
        {
            var f = Scripted();
            Drag(f, 0, 1);
            var m = f.LoggedOf<MergeFish>().Single();
            Assert.That((m.Source, m.Target), Is.EqualTo((0, 1)));
            f.Frame();
            Assert.That(f.Vm.Board.Level(1), Is.EqualTo(2), "the board re-renders from the state");
            Assert.That(f.Vm.Board.Level(0), Is.EqualTo(0));
        }

        [Test]
        public void AC16_DropOntoADifferentLevel_SendsNothing_AndShakes()
        {
            var f = Scripted();
            f.Vm.Cues.Clear();
            Drag(f, 0, 2);
            Assert.That(f.Logged<MergeFish>() + f.Logged<MoveFish>(), Is.EqualTo(0));
            Assert.That(f.Vm.Cues.Contains(CueKind.FishInvalid), Is.True);
            Assert.That(f.Vm.Cues.Contains(CueKind.FishSnapBack), Is.True);
            Assert.That(f.Vm.Board.State, Is.EqualTo(BoardState.Idle));
        }

        [Test]
        public void AC16_DropOntoAnEmptyCell_SendsOneMoveFish()
        {
            var f = Scripted();
            Drag(f, 0, 7);
            var mv = f.LoggedOf<MoveFish>().Single();
            Assert.That((mv.Source, mv.Target), Is.EqualTo((0, 7)));
        }

        [Test]
        public void AC16_MergeAllAndReleaseMax_SendOneCommandEach()
        {
            var f = Scripted();
            Assert.That(f.Vm.Board.MergeAll.Enabled.Value, Is.True);
            f.Vm.Board.OnMergeAll();
            f.Vm.Board.OnReleaseMax();
            Assert.That(f.Logged<MergeAll>(), Is.EqualTo(1));
            Assert.That(f.Logged<ReleaseMax>(), Is.EqualTo(1));
            Assert.That(f.Logged<MergeFish>() + f.Logged<ReleaseFish>(), Is.EqualTo(0));
        }

        [Test]
        public void ATapUnderTheDragThreshold_NeverDrags_AndTapSelectMerges()
        {
            var f = Scripted();
            var b = f.Vm.Board;
            double t = f.Now;
            b.PointerDown(0, 0, 0, 0, t);
            b.PointerMove(0, 0, 3, 3, t + 0.01);   // under UI_DRAG_START_PX
            b.PointerUp(0, 0, 3, 3, t + 0.05);
            Assert.That(b.State, Is.EqualTo(BoardState.Selected));
            Assert.That(b.SelectedCell, Is.EqualTo(0));
            Assert.That(b.Cells[3].Value.Pulse, Is.True, "an equal-level fish pulses while one is selected");
            b.PointerDown(0, 3, 0, 0, t + 0.2);
            b.PointerUp(0, 3, 0, 0, t + 0.25);
            var m = f.LoggedOf<MergeFish>().Single();
            Assert.That((m.Source, m.Target), Is.EqualTo((0, 3)), "a second tap on an equal fish merges into it");
        }

        [Test]
        public void TapSelect_ThenADifferentLevel_ClearsWithTheInvalidFeedback()
        {
            var f = Scripted();
            var b = f.Vm.Board;
            b.PointerDown(0, 0, 0, 0, f.Now);
            b.PointerUp(0, 0, 0, 0, f.Now);
            f.Vm.Cues.Clear();
            b.PointerDown(0, 2, 0, 0, f.Now);
            b.PointerUp(0, 2, 0, 0, f.Now);
            Assert.That(b.State, Is.EqualTo(BoardState.Idle));
            Assert.That(f.Logged<MergeFish>(), Is.EqualTo(0));
            Assert.That(f.Vm.Cues.Contains(CueKind.FishInvalid), Is.True);
        }

        [Test]
        public void LongPress_OpensInfo_AndOnlyTheButtonSendsDiscardFish()
        {
            var f = Scripted();
            var b = f.Vm.Board;
            double t = f.Now;
            b.PointerDown(0, 2, 0, 0, t);
            b.Update(t + (UiConstants.LongPressMs - 10) / 1000.0);
            Assert.That(b.State, Is.Not.EqualTo(BoardState.Info));
            b.Update(t + UiConstants.LongPressMs / 1000.0);
            Assert.That(b.State, Is.EqualTo(BoardState.Info));
            Assert.That(b.InfoVisible.Value, Is.True);
            b.PointerUp(0, 2, 0, 0, t + 0.5);
            Assert.That(f.Logged<DiscardFish>(), Is.EqualTo(0));
            b.OnDiscard();
            Assert.That(f.LoggedOf<DiscardFish>().Single().Cell, Is.EqualTo(2));
            Assert.That(b.InfoVisible.Value, Is.False);
        }

        [Test]
        public void BackgroundedMidDrag_CancelsAndSendsNothing()
        {
            var f = Scripted();
            var b = f.Vm.Board;
            b.PointerDown(0, 0, 0, 0, f.Now);
            b.PointerMove(0, 1, 30, 0, f.Now);
            Assert.That(b.State, Is.EqualTo(BoardState.Dragging));
            f.Vm.OnPause();
            b.PointerUp(0, 1, 30, 0, f.Now);
            Assert.That(b.State, Is.EqualTo(BoardState.Idle));
            Assert.That(f.Logged<MergeFish>(), Is.EqualTo(0));
        }

        [Test]
        public void ReleaseButtons_NameTheirReason_BoardFullAndTankEmpty_MergeAllNothingToMerge()
        {
            var full = ClientFixture.Open(s =>
            {
                for (int i = 0; i < s.Fish.Cells.Length; i++) s.Fish.Cells[i] = 1 + i % 9;   // full, nine levels
                s.Fish.TankCount = 2;
            });
            Assert.That(full.Vm.Board.Release.Enabled.Value, Is.False);
            Assert.That(full.Vm.Board.Release.Reason.Key, Is.EqualTo(UiKeys.FishBoardFull));

            var empty = ClientFixture.Open(s =>
            {
                for (int i = 0; i < s.Fish.Cells.Length; i++) s.Fish.Cells[i] = 0;
                s.Fish.TankCount = 0;
            });
            var b = empty.Vm.Board;
            Assert.That(b.Release.Enabled.Value, Is.False);
            Assert.That(b.Release.Reason.Key, Is.EqualTo(UiKeys.FishTankEmpty));
            Assert.That(b.NextFishVisible.Value, Is.True, "the countdown to the next fish shows");
            Assert.That(b.MergeAll.Enabled.Value, Is.False);
            Assert.That(b.MergeAll.Reason.Key, Is.EqualTo(UiKeys.FishNothingToMerge));
        }

        [Test]
        public void AC20_BoardRefresh_IsChangeOnly()
        {
            var f = Scripted();
            var counters = new System.Collections.Generic.List<System.Func<int>>();
            SinkBinder.BindAll(f.Vm.Board, counters);
            f.Vm.Refresh();
            int n = SinkBinder.Total(counters);
            f.Vm.Refresh();
            Assert.That(SinkBinder.Total(counters), Is.EqualTo(n));
        }
    }
}
