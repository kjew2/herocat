using System.Collections.Generic;
using HeroCat.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// SCR_RESEARCH (07 section 4.9; issue #181): Fish Coin header, one row per node (name, level / max, current -> next,
    /// cost, [Buy]), the Locked state and the Tank capacity coach mark. Every rule is in ResearchVm. Unity-only: not
    /// compiled in cloud sessions.
    /// </summary>
    public sealed class ResearchScreen : FullScreenView
    {
        protected override string TitleKey => UiKeys.ResearchTitle;

        readonly List<Image> _rowFlash = new List<Image>();
        readonly List<int> _rowSerial = new List<int>();

        protected override void BuildContent()
        {
            var r = Vm.Research;
            var coins = Line("FishCoins", 16, UiKit.Title, Palette.Text, 24, 1032, 64);
            coins.alignment = TextAlignmentOptions.MidlineRight;
            r.FishCoin.Bind(new ActionTextSink(t => coins.text = Vm.Localizer.Format(UiKeys.ResearchFishCoins, t)));
            var locked = Line("Locked", 88, UiKit.Body, Palette.Muted);
            r.LockedLine.Bind(locked.Sink());
            var group = Content.gameObject.AddComponent<CanvasGroup>();
            r.Locked.Bind(new ActionFlagSink(on =>
            {
                locked.gameObject.SetActive(on);
                group.alpha = on ? 0.6f : 1f;   // greyed while locked
            }));
            float y = 152, coachY = 152;
            foreach (var row in r.Rows)
            {
                var x = row;
                var flash = UiKit.Panel("Row" + x.NodeId, Content, Palette.Panel);
                UiKit.Place(flash.rectTransform, 16, y, 1048, 128);
                _rowFlash.Add(flash);
                _rowSerial.Add(x.FlashSerial);
                if (x.NodeId == ResearchVm.CoachNode) coachY = y + 136;
                var name = UiKit.Text("Name", flash.transform, UiKit.Body, Palette.Text);
                UiKit.Place(name.rectTransform, 16, 0, 300, 128);
                x.Name.Bind(name.Sink());
                var lv = UiKit.Text("Level", flash.transform, UiKit.Caption, Palette.Muted);
                UiKit.Place(lv.rectTransform, 320, 0, 140, 128);
                x.Level.Bind(new ActionTextSink(_ => lv.text = Vm.Localizer.Format(UiKeys.ResearchLevel, x.Level.Text, x.MaxLevel.Text)));
                x.MaxLevel.Bind(new ActionTextSink(_ => lv.text = Vm.Localizer.Format(UiKeys.ResearchLevel, x.Level.Text, x.MaxLevel.Text)));
                var eff = UiKit.Text("Effect", flash.transform, UiKit.Body, Palette.Text);
                UiKit.Place(eff.rectTransform, 460, 0, 300, 128);
                string cur = "", next = "";
                void Draw() => eff.text = next.Length == 0 ? cur : cur + " -> " + next;
                x.CurrentText.Bind(new ActionTextSink(t => { cur = t; Draw(); }));
                x.NextText.Bind(new ActionTextSink(t => { next = t ?? ""; Draw(); }));
                var cost = UiKit.Text("Cost", flash.transform, UiKit.Body, Palette.FishCoin, TextAlignmentOptions.MidlineRight);
                UiKit.Place(cost.rectTransform, 640, 0, 140, 128);
                x.Cost.Bind(cost.Sink());
                x.AtMax.Bind(new ActionFlagSink(max => cost.gameObject.SetActive(!max)));
                var buy = UiKit.Button("Buy", flash.transform, () => { Vm.Research.OnCoachMarkDismissed(); Vm.Research.OnBuy(x.NodeId); }, 0x37474F, UiKit.Caption);
                var brt = buy.GetComponent<RectTransform>();
                UiKit.Place(brt, 792, 8, 240, 112);
                var reason = UiKit.Text("Reason", brt, UiKit.Micro, Palette.Muted, TextAlignmentOptions.Bottom);
                UiKit.Fill(reason.rectTransform, 4, 70, 4, 2);
                ButtonBinding.Bind(x.Buy, buy, reason);
                Anchor(x.Anchor, brt);
                y += 136;
            }
            // First-time coach mark on Tank capacity: a card under the row; any tap dismisses it.
            var coach = UiKit.Panel("Coach", Content, Palette.PanelRaised, 1f, raycast: true);
            UiKit.Place(coach.rectTransform, 120, coachY, 840, 120);
            coach.gameObject.AddComponent<Button>().onClick.AddListener(Vm.Research.OnCoachMarkDismissed);
            var coachText = UiKit.Text("Text", coach.rectTransform, UiKit.Caption, Palette.Text, TextAlignmentOptions.Center);
            UiKit.Fill(coachText.rectTransform, 16, 8, 16, 8);
            r.CoachText.Bind(coachText.Sink());
            r.CoachMark.Bind(new ActiveSink(coach.gameObject));
            SetContentHeight(y + 32);
        }

        public override void Tick(double now)
        {
            var rows = Vm.Research.Rows;
            for (int i = 0; i < rows.Count && i < _rowFlash.Count; i++)
                if (rows[i].FlashSerial != _rowSerial[i])
                {
                    _rowSerial[i] = rows[i].FlashSerial;
                    Ui.Tweens.Pulse(_rowFlash[i].rectTransform, now, 0.03f, 0.25);   // ResearchLeveled row flash
                }
        }
    }
}
