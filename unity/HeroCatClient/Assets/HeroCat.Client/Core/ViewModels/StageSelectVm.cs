using System.Collections.Generic;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.State;

namespace HeroCat.Client.Core
{
    /// <summary>One stage chip of SCR_STAGE_SELECT (07 section 4.14).</summary>
    public readonly struct StageChip
    {
        public StageChip(int number, bool isBoss, bool isFrontier, bool isSelected, bool cleared)
        {
            Number = number; IsBoss = isBoss; IsFrontier = isFrontier; IsSelected = isSelected; Cleared = cleared;
        }

        public int Number { get; }
        public bool IsBoss { get; }
        public bool IsFrontier { get; }
        /// <summary>The stage being fought: the farmed side stage, or the frontier when nothing is selected.</summary>
        public bool IsSelected { get; }
        public bool Cleared { get; }
    }

    /// <summary>One chapter of the dropdown: stages <see cref="From"/>..<see cref="To"/> (<c>CHAPTER_WIDTH</c> wide).</summary>
    public readonly struct ChapterOption
    {
        public ChapterOption(int index, int from, int to) { Index = index; From = from; To = to; }
        public int Index { get; }
        public int From { get; }
        public int To { get; }
    }

    /// <summary>
    /// SCR_STAGE_SELECT (07 section 4.14, 03 section 2.7, 00 decision 67; issue #183). The chapter dropdown lists the
    /// <c>CHAPTER_WIDTH</c> groups up to the frontier's chapter; the chips of the chosen chapter run up to the frontier.
    /// A chip sends <c>SelectStage(n)</c> only for <c>n &lt;= Progress.HighestCleared</c>; the frontier chip and Return send
    /// <c>SelectStage(null)</c>; Challenge boss (only while walled) sends <c>AttemptBoss</c>, which fights the frontier boss,
    /// and closes the modal. Offline gold and EXP, the Dungeons unlock and first-clear diamonds read the frontier and
    /// <c>HighestCleared</c>, never the selected stage, and the modal says so (<see cref="UiKeys.StageSelectOfflineNote"/>).
    /// </summary>
    public sealed class StageSelectVm
    {
        public const string AnchorChallenge = "stage_select.challenge";
        public const string AnchorReturn = "stage_select.return";
        public static string AnchorChip(int n) => "stage_select.chip." + n;

        readonly VmContext _c;
        readonly List<ChapterOption> _chapters = new List<ChapterOption>();
        readonly List<StageChip> _chips = new List<StageChip>();
        int _builtChapter = -1, _builtFrontier = -1, _builtHighest = -1, _builtFought = -1;
        bool _chapterChosen;

        public StageSelectVm(VmContext c)
        {
            _c = c;
            HighestLine = c.Label();
            FrontierLine = c.Label();
            ChapterLine = c.Label();
            FarmingLine = c.Label();
            Return = c.Button();
            Challenge = c.Button();
            Return.Label.Set(UiKeys.StageSelectReturn);
            Challenge.Label.Set(UiKeys.StageSelectChallenge);
        }

        public readonly IntLabel HighestCleared = new IntLabel();
        public readonly IntLabel Frontier = new IntLabel();
        public readonly Flag Walled = new Flag();
        /// <summary>"Highest cleared: 239".</summary>
        public readonly TextKeyLabel HighestLine;
        /// <summary>"Frontier: 240" or "Frontier: 240 (walled)".</summary>
        public readonly TextKeyLabel FrontierLine;
        /// <summary>"Chapter 5 (201-250)" for the chosen chapter.</summary>
        public readonly TextKeyLabel ChapterLine;
        /// <summary>"Farming: 235" while a side stage is selected; empty on the frontier.</summary>
        public readonly TextKeyLabel FarmingLine;

        /// <summary>The snapshot's <c>SelectedStage</c> (02 <c>Combat.Selected</c>); 0 = the frontier.</summary>
        public readonly IntLabel Selected = new IntLabel();

        /// <summary>The chosen chapter (1-based).</summary>
        public readonly Bindable<int> Chapter = new Bindable<int>();

        public readonly ButtonState Return;
        public readonly ButtonState Challenge;

        public IReadOnlyList<ChapterOption> Chapters => _chapters;
        public IReadOnlyList<StageChip> Stages => _chips;
        /// <summary>Bumps when <see cref="Chapters"/> or <see cref="Stages"/> changed; the view rebuilds the chips.</summary>
        public int Version { get; private set; }

        /// <summary>The modal opened: it shows the chapter of the stage being fought.</summary>
        public void OnShown() => _chapterChosen = false;

        public void Refresh(Simulation sim, in Snapshot s)
        {
            var k = _c.K;
            int highest = sim.State.Progress.HighestCleared;
            int frontier = s.Stage;
            int? sel = s.SelectedStage;
            int fought = sel ?? frontier;
            bool walled = s.BossPhase == BossPhase.Walled;

            HighestCleared.Set(highest);
            Frontier.Set(frontier);
            Walled.Set(walled);
            Selected.Set(sel ?? 0);
            HighestLine.Set(UiKeys.StageSelectHighest, highest);
            FrontierLine.Set(walled ? UiKeys.StageSelectFrontierWalled : UiKeys.StageSelectFrontier, frontier);
            if (sel.HasValue) FarmingLine.Set(UiKeys.StageSelectFarming, sel.Value);
            else FarmingLine.Set(UiKeys.Raw);

            int top = highest > frontier ? highest : frontier;
            int lastChapter = k.ChapterOf(top < 1 ? 1 : top);
            if (!_chapterChosen) { Chapter.Set(k.ChapterOf(fought < 1 ? 1 : fought)); _chapterChosen = true; }
            if (Chapter.Value < 1) Chapter.Set(1);
            if (Chapter.Value > lastChapter) Chapter.Set(lastChapter);
            int ch = Chapter.Value;
            int from = (ch - 1) * k.ChapterWidth + 1;
            int to = ch * k.ChapterWidth;
            ChapterLine.Set(UiKeys.StageSelectChapter, ch, from, to);

            if (_chapters.Count != lastChapter || ch != _builtChapter || frontier != _builtFrontier || highest != _builtHighest || fought != _builtFought)
            {
                _chapters.Clear();
                for (int i = 1; i <= lastChapter; i++) _chapters.Add(new ChapterOption(i, (i - 1) * k.ChapterWidth + 1, i * k.ChapterWidth));
                _chips.Clear();
                int end = to < top ? to : top;
                for (int n = from; n <= end; n++)
                    _chips.Add(new StageChip(n, k.IsBossStage(n), n == frontier, n == fought, n <= highest));
                _builtChapter = ch; _builtFrontier = frontier; _builtHighest = highest; _builtFought = fought;
                Version++;
            }

            Return.Visible.Set(sel.HasValue);
            Return.Enabled.Set(sel.HasValue);
            Challenge.Visible.Set(walled);
            Challenge.Enabled.Set(walled);
        }

        /// <summary>True when the chip for <paramref name="n"/> may send a command: a cleared stage, or the frontier (= Return).</summary>
        public static bool Selectable(int n, int highestCleared, int frontier) => n >= 1 && (n <= highestCleared || n == frontier);

        // ------------------------------------------------------------------------------------------ input

        public void OnChapter(int chapter)
        {
            _chapterChosen = true;
            Chapter.Set(chapter < 1 ? 1 : chapter);
        }

        /// <summary>A chip tap: <c>SelectStage(n)</c> for a cleared stage; the frontier chip equals Return; anything above is not offered.</summary>
        public ApplyResult OnStage(int n)
        {
            int highest = (int)HighestCleared.Value, frontier = (int)Frontier.Value;
            if (!Selectable(n, highest, frontier)) return ApplyResult.Reject(RejectCode.Locked, CombatRejectReasons.STAGE_NOT_CLEARED);
            var cmd = new SelectStage(n == frontier ? (int?)null : n);
            return _c.Commands?.Send(cmd, AnchorChip(n)) ?? ApplyResult.Accepted;
        }

        public ApplyResult OnReturn() => _c.Commands?.Send(new SelectStage(null), AnchorReturn) ?? ApplyResult.Accepted;

        /// <summary>The frontier boss only (AttemptBoss returns to the frontier first); the modal closes so the fight is visible.</summary>
        public ApplyResult OnChallenge()
        {
            var r = _c.Commands?.Send(new AttemptBoss(), AnchorChallenge) ?? ApplyResult.Accepted;
            if (r.Ok) _c.Router?.CloseModal(ScreenIds.StageSelect);
            return r;
        }

        public void OnClose() => _c.Router?.CloseModal(ScreenIds.StageSelect);
    }
}
