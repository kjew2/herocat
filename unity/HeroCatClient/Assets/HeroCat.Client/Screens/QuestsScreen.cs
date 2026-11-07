using System.Collections.Generic;
using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// SCR_QUESTS (07 section 4.15; issue #183): tabs with badges, the progress bar with bonus chips, one row per quest
    /// and Claim all. Thin over <see cref="QuestsVm"/>; rows rebuild when <see cref="QuestsVm.RowsVersion"/> moves.
    /// Unity-only: not compiled in cloud sessions (type-checked against stubs only).
    /// </summary>
    public sealed class QuestsScreen : FullScreenView
    {
        protected override string TitleKey => UiKeys.QuestsTitle;

        const float TabsY = 16, ProgressY = 136, RowsTop = 296, RowH = 128;
        static readonly QuestKind[] Kinds = { QuestKind.DAILY, QuestKind.WEEKLY, QuestKind.ACHIEVEMENT };

        readonly List<GameObject> _built = new List<GameObject>();
        RectTransform _rows, _chips, _progress;
        CanvasGroup _rowsGroup;
        RectTransform _claimAll;
        int _version = -1;

        protected override void BuildContent()
        {
            var q = Vm.Quests;
            var tabImgs = new Image[Kinds.Length];
            for (int i = 0; i < Kinds.Length; i++)
            {
                var kind = Kinds[i];
                var b = Btn("Tab" + kind, 24 + i * 344, TabsY, 336, 104, () => q.OnTab(kind), Palette.PanelRaised, UiKit.Body);
                var label = UiKit.LabelOf(b);
                string name = Vm.Localizer.Text(UiKeys.QuestsTabKey(kind));
                label.text = name;
                tabImgs[i] = b.GetComponent<Image>();
                var dot = UiKit.CirclePanel("Dot", b.transform, UiKit.C(Palette.Bad));
                UiKit.Place(dot.rectTransform, 296, 8, 32, 32);
                q.TabBadges[i].Bind(new ActiveSink(dot.gameObject), new ActionTextSink(n => label.text = string.IsNullOrEmpty(n) ? name : name + " " + n));
            }
            q.Tab.Bind(new ActionSink<QuestKind>(t =>
            {
                for (int i = 0; i < tabImgs.Length; i++) tabImgs[i].color = UiKit.C(Kinds[i] == t ? Palette.Accent : Palette.PanelRaised, Kinds[i] == t ? 0.6f : 1f);
            }));

            _progress = UiKit.Rect("Progress", Content);
            UiKit.Place(_progress, 24, ProgressY, 1032, 144);
            var pl = UiKit.Text("Label", _progress, UiKit.Body, Palette.Text);
            UiKit.Place(pl.rectTransform, 0, 0, 520, 56);
            q.ProgressLabel.Bind(pl.Sink());
            var bar = UiKit.Bar("Bar", _progress, Palette.Accent, out var fill);
            UiKit.Place(bar, 0, 72, 520, 32);
            q.ProgressFill.Bind(fill.Sink());
            _chips = UiKit.Rect("Chips", _progress);
            UiKit.Place(_chips, 540, 0, 492, 112);
            q.ProgressVisible.Bind(new ActiveSink(_progress.gameObject));

            _rows = UiKit.Rect("Rows", Content);
            UiKit.Place(_rows, 16, RowsTop, 1048, 2000);
            _rowsGroup = _rows.gameObject.AddComponent<CanvasGroup>();

            var all = Btn("ClaimAll", 600, RowsTop, 456, 112, () => q.OnClaimAll(), 0x2E7D32);
            _claimAll = all.GetComponent<RectTransform>();
            ButtonBinding.Bind(q.ClaimAll, all);
            Anchor(QuestsVm.AnchorClaimAll, all.GetComponent<RectTransform>());

            // Locked (07 section 3): greyed content, the unlock condition, no interaction. TODO(gap: shared Locked
            // presentation, #180): a local stand-in until the shared lock card lands.
            var lockText = Line("Locked", ProgressY, UiKit.Title, Palette.Muted, 24, 1032, 96);
            lockText.alignment = TextAlignmentOptions.Center;
            q.LockedText.Bind(new ActionTextSink(t => lockText.text = string.IsNullOrEmpty(t) ? "" : t));
            q.Locked.Bind(new ActionFlagSink(on =>
            {
                lockText.gameObject.SetActive(on);
                _rowsGroup.alpha = on ? Palette.DisabledAlpha : 1f;
                _rowsGroup.blocksRaycasts = !on;   // no interaction while locked
            }));
        }

        public override void OnShown(ScreenEntry e) => _version = -1;

        public override void Tick(double now)
        {
            var q = Vm.Quests;
            if (q.RowsVersion == _version) return;
            _version = q.RowsVersion;
            foreach (var go in _built) Destroy(go);
            _built.Clear();

            for (int i = 0; i < q.Bonus.Count; i++)
            {
                var chip = q.Bonus[i];
                var b = UiKit.Button("Bonus" + chip.Id, _chips, () => q.OnClaim(chip.Id), Palette.PanelRaised, UiKit.Caption);
                UiKit.Place(b.GetComponent<RectTransform>(), i * 248, 0, 236, 104);
                var label = UiKit.LabelOf(b);
                string text = Vm.Localizer.Format(UiKeys.QuestsBonus, chip.Threshold.ToString(), TextFmt.IntText(chip.RewardAmount));
                var img = b.GetComponent<Image>();
                chip.State.Bind(new ActionSink<QuestRowState>(s =>
                {
                    label.text = s == QuestRowState.Claimed ? text + "\n" + Vm.Localizer.Text(UiKeys.QuestsClaimed)
                               : s == QuestRowState.Completed ? text + " !" : text;
                    img.color = UiKit.C(s == QuestRowState.Completed ? Palette.Ok : Palette.PanelRaised);
                }));
                Anchor(QuestsVm.AnchorClaim(chip.Id), b.GetComponent<RectTransform>());
                _built.Add(b.gameObject);
            }

            float y = 0;
            foreach (var row in q.Rows)
            {
                var r = row;
                var panel = UiKit.Panel("Row" + r.Id, _rows, Palette.Panel);
                UiKit.Place(panel.rectTransform, 0, y, 1048, RowH - 8);
                _built.Add(panel.gameObject);
                var title = UiKit.Text("Title", panel.transform, UiKit.Body, Palette.Text);
                UiKit.Place(title.rectTransform, 16, 0, 520, 64);
                title.text = Vm.Localizer.Format(r.TitleKey, TextFmt.IntText(r.Target));
                var sub = UiKit.Text("Sub", panel.transform, UiKit.Caption, Palette.Muted);
                UiKit.Place(sub.rectTransform, 16, 64, 520, 48);
                if (r.LadderRungs > 0) r.Rungs.Bind(sub.Sink());
                var expiry = UiKit.Text("Expiry", panel.transform, UiKit.Micro, Palette.Gold);   // amber caption
                UiKit.Place(expiry.rectTransform, 300, 64, 240, 48);
                expiry.text = Vm.Localizer.Text(UiKeys.QuestsExpiresSoon);
                r.ExpiresSoon.Bind(new ActiveSink(expiry.gameObject));
                var prog = UiKit.Text("Progress", panel.transform, UiKit.Caption, Palette.Text, TextAlignmentOptions.MidlineRight);
                UiKit.Place(prog.rectTransform, 540, 0, 200, 120);
                r.Progress.Bind(new ActionTextSink(t => prog.text = Vm.Localizer.Format(UiKeys.QuestsProgress, t, TextFmt.IntText(r.Target))));
                var reward = UiKit.Text("Reward", panel.transform, UiKit.Caption, Palette.Diamond, TextAlignmentOptions.MidlineRight);
                UiKit.Place(reward.rectTransform, 740, 0, 100, 120);
                reward.text = r.CountsOnly ? "" : "+" + TextFmt.IntText(r.RewardAmount);
                UnityEngine.Events.UnityAction tap = null;   // swapped by the row state: Claim, Go or nothing
                var action = UiKit.Button("Action", panel.transform, () => tap?.Invoke(), 0x37474F, UiKit.Caption);
                var art = action.GetComponent<RectTransform>();
                UiKit.Place(art, 852, 8, 184, 104);
                Anchor(QuestsVm.AnchorClaim(r.Id), art);
                var alabel = UiKit.LabelOf(action);
                var aimg = action.GetComponent<Image>();
                r.State.Bind(new ActionSink<QuestRowState>(s =>
                {
                    action.interactable = true;
                    if (s == QuestRowState.Completed && !r.CountsOnly)
                    {
                        alabel.text = Vm.Localizer.Text(UiKeys.QuestsClaim);
                        aimg.color = UiKit.C(0x2E7D32);
                        tap = () => q.OnClaim(r.Id);
                    }
                    else if (s == QuestRowState.InProgress && r.GoScreen != null)
                    {
                        alabel.text = Vm.Localizer.Text(UiKeys.QuestsGo);
                        aimg.color = UiKit.C(Palette.PanelRaised);
                        tap = () => q.OnGo(r);
                    }
                    else
                    {
                        alabel.text = s == QuestRowState.InProgress ? "" : Vm.Localizer.Text(r.CountsOnly ? UiKeys.QuestsDone : UiKeys.QuestsClaimed);
                        aimg.color = UiKit.C(Palette.Panel);
                        tap = null;
                        action.interactable = false;
                    }
                }));
                y += RowH;
            }
            UiKit.Place(_claimAll, 600, RowsTop + y + 16, 456, 112);
            SetContentHeight(RowsTop + y + 160);
        }
    }
}
