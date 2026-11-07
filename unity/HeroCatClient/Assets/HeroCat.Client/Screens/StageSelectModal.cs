using System.Collections.Generic;
using HeroCat.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// SCR_STAGE_SELECT (07 section 4.14; issue #183): highest cleared vs frontier, a chapter picker, the chapter's stage
    /// chips, Return to frontier, Challenge boss and the offline-rewards line. Thin over <see cref="StageSelectVm"/>; the
    /// chips rebuild when <see cref="StageSelectVm.Version"/> moves. The chapter dropdown is a placeholder pair of
    /// previous / next arrows (07 section 15.1). Unity-only: not compiled in cloud sessions (type-checked against stubs only).
    /// </summary>
    public sealed class StageSelectModal : ModalView
    {
        const int PerRow = 10;
        const float ChipW = 80, ChipH = 64, Gap = 8, ChipsTop = 296;

        readonly List<GameObject> _chips = new List<GameObject>();
        RectTransform _grid;
        int _version = -1;

        protected override void BuildCard()
        {
            var v = Vm.StageSelect;
            Line("Title", 24, UiKit.Title, Palette.Text, 72, TextAlignmentOptions.MidlineLeft).text = Vm.Localizer.Text(UiKeys.StageSelectTitle);
            var close = UiKit.Button("Close", Card, v.OnClose, Palette.Panel, UiKit.Title);
            UiKit.Place(close.GetComponent<RectTransform>(), UiConstants.ModalWidth - 128, 16, 112, 88);
            UiKit.LabelOf(close).text = "X";

            v.HighestLine.Bind(Line("Highest", 104, UiKit.Body, Palette.Text, 56, TextAlignmentOptions.MidlineLeft).Sink());
            v.FrontierLine.Bind(Line("Frontier", 160, UiKit.Body, Palette.Text, 56, TextAlignmentOptions.MidlineLeft).Sink());

            var prev = UiKit.Button("PrevChapter", Card, () => v.OnChapter(v.Chapter.Value - 1), Palette.PanelRaised, UiKit.Title);
            UiKit.Place(prev.GetComponent<RectTransform>(), 32, 224, 96, 64);
            UiKit.LabelOf(prev).text = "‹";
            var chapter = UiKit.Text("Chapter", Card, UiKit.Body, Palette.Text, TextAlignmentOptions.Center);
            UiKit.Place(chapter.rectTransform, 136, 224, 624, 64);
            v.ChapterLine.Bind(chapter.Sink());
            var next = UiKit.Button("NextChapter", Card, () => v.OnChapter(v.Chapter.Value + 1), Palette.PanelRaised, UiKit.Title);
            UiKit.Place(next.GetComponent<RectTransform>(), 768, 224, 96, 64);
            UiKit.LabelOf(next).text = "›";

            _grid = UiKit.Rect("Chips", Card);
            UiKit.Place(_grid, 32, ChipsTop, UiConstants.ModalWidth - 64, 5 * (ChipH + Gap));

            v.FarmingLine.Bind(Line("Farming", 664, UiKit.Caption, Palette.Accent, 48).Sink());

            var ret = UiKit.Button("Return", Card, () => v.OnReturn(), 0x37474F, UiKit.Body);
            UiKit.Place(ret.GetComponent<RectTransform>(), 32, 720, 440, 112);
            ButtonBinding.Bind(v.Return, ret);
            Anchor(StageSelectVm.AnchorReturn, ret.GetComponent<RectTransform>());
            var boss = UiKit.Button("Challenge", Card, () => v.OnChallenge(), 0xC62828, UiKit.Body);
            UiKit.Place(boss.GetComponent<RectTransform>(), 488, 720, 440, 112);
            ButtonBinding.Bind(v.Challenge, boss);
            Anchor(StageSelectVm.AnchorChallenge, boss.GetComponent<RectTransform>());

            Line("OfflineNote", 840, UiKit.Micro, Palette.Muted, 48).text = Vm.Localizer.Text(UiKeys.StageSelectOfflineNote);
        }

        public override void OnShown(ScreenEntry e) => _version = -1;

        public override void Tick(double now)
        {
            var v = Vm.StageSelect;
            if (v.Version == _version) return;
            _version = v.Version;
            foreach (var go in _chips) Destroy(go);
            _chips.Clear();
            var stages = v.Stages;
            for (int i = 0; i < stages.Count; i++)
            {
                var chip = stages[i];
                int n = chip.Number;
                uint color = chip.IsSelected ? Palette.Accent : chip.IsFrontier ? 0xC62828 : Palette.PanelRaised;
                var b = UiKit.Button("Stage" + n, _grid, () => v.OnStage(n), color, UiKit.Micro);
                var rt = b.GetComponent<RectTransform>();
                UiKit.Place(rt, i % PerRow * (ChipW + Gap), i / PerRow * (ChipH + Gap), ChipW, ChipH);
                UiKit.LabelOf(b).text = chip.IsBoss ? n + "*" : n.ToString();   // "*" marks a boss stage (colour is never the only signal)
                if (!chip.Cleared && !chip.IsFrontier) UiKit.EnabledOf(b).SetFlag(false);
                Anchor(StageSelectVm.AnchorChip(n), rt);
                _chips.Add(b.gameObject);
            }
        }
    }
}
