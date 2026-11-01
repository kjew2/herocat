using System.Collections.Generic;
using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>SCR_HERO (07 section 4.4; issue #115).</summary>
    public sealed class HeroScreen : FullScreenView
    {
        protected override string TitleKey => UiKeys.HeroTitle;

        readonly List<Image> _rowFlash = new List<Image>();
        readonly List<int> _rowSerial = new List<int>();

        protected override void BuildContent()
        {
            var h = Vm.Hero;
            var level = Line("Level", 16, UiKit.Title, Palette.Text, 24, 300, 64);
            h.Level.Bind(new ActionTextSink(t => level.text = Vm.Localizer.Format(UiKeys.HeroLevel, t)));
            var bar = UiKit.Bar("Exp", Content, Palette.Accent, out var fill);
            UiKit.Place(bar, 340, 32, 420, 32);
            h.ExpFill.Bind(fill.Sink());
            var exp = Line("ExpText", 16, UiKit.Caption, Palette.Muted, 776, 280, 64);
            h.Exp.Bind(new ActionTextSink(_ => exp.text = h.Exp.Text + " / " + h.ExpToNext.Text));
            h.ExpToNext.Bind(new ActionTextSink(_ => exp.text = h.Exp.Text + " / " + h.ExpToNext.Text));
            var dps = Line("Dps", 88, UiKit.Body, Palette.Text, 24, 600, 56);
            h.Dps.Bind(new ActionTextSink(t => dps.text = Vm.Localizer.Format(UiKeys.MainDps, t)));

            // Loadout: weapon, 6 skills, 4 companions; tap -> SCR_ITEM_DETAIL, empty -> the collection screen.
            Line("LoadoutTitle", 152, UiKit.Caption, Palette.Muted).text = Vm.Localizer.Text(UiKeys.HeroLoadout);
            for (int i = 0; i < h.Loadout.Length; i++)
            {
                int idx = i;
                var b = Btn("Slot" + i, 24 + i * 94, 200, 88, 88, () => Vm.Hero.OnLoadout(idx, Vm.ItemDetail), Palette.PanelRaised, UiKit.Micro);
                var img = b.GetComponent<Image>();
                var label = UiKit.LabelOf(b);
                h.Loadout[i].Bind(new ActionSink<SlotLook>(s =>
                {
                    img.color = s.IsEmpty ? UiKit.C(Palette.PanelRaised) : UiKit.Rarity(s.Rarity);
                    label.text = s.IsEmpty ? "--" : Palette.ShapeGlyph(s.Rarity) + s.Level;
                }));
            }

            Line("TrainingTitle", 316, UiKit.Title, Palette.Text, 24, 400, 64).text = Vm.Localizer.Text(UiKeys.HeroTraining);
            var stepImgs = new Image[h.StepCount];
            for (int i = 0; i < h.StepCount; i++)
            {
                int idx = i;
                var b = Btn("Step" + i, 480 + i * 144, 304, 136, 112, () => Vm.Hero.SelectStep(idx), Palette.PanelRaised, UiKit.Caption);
                h.StepLabels[i].Bind(UiKit.LabelOf(b).Sink());
                stepImgs[i] = b.GetComponent<Image>();
            }
            h.SelectedStep.Bind(new ActionSink<int>(sel =>
            {
                for (int i = 0; i < stepImgs.Length; i++) stepImgs[i].color = UiKit.C(i == sel ? Palette.Accent : Palette.PanelRaised, i == sel ? 0.6f : 1f);
            }));

            float y = 440;
            foreach (var row in h.Rows)
            {
                var r = row;
                var flash = UiKit.Panel("Row" + r.Stat, Content, Palette.Panel);
                UiKit.Place(flash.rectTransform, 16, y, 1048, 128);
                _rowFlash.Add(flash);
                _rowSerial.Add(r.FlashSerial);
                var name = UiKit.Text("Name", flash.transform, UiKit.Body, Palette.Text);
                UiKit.Place(name.rectTransform, 16, 0, 280, 128);
                r.Name.Bind(name.Sink());
                var lv = UiKit.Text("Level", flash.transform, UiKit.Body, Palette.Muted);
                UiKit.Place(lv.rectTransform, 300, 0, 140, 128);
                r.Level.Bind(new ActionTextSink(t => lv.text = Vm.Localizer.Format(UiKeys.HeroLevel, t)));
                var eff = UiKit.Text("Effect", flash.transform, UiKit.Body, Palette.Text);
                UiKit.Place(eff.rectTransform, 440, 0, 180, 128);
                r.Effect.Bind(eff.Sink());
                var cost = UiKit.Text("Cost", flash.transform, UiKit.Body, Palette.Gold, TextAlignmentOptions.MidlineRight);
                UiKit.Place(cost.rectTransform, 600, 0, 140, 128);
                r.Cost.Bind(cost.Sink());
                r.AtCap.Bind(new ActionFlagSink(cap => cost.gameObject.SetActive(!cap)));
                var buy = UiKit.Button("Buy", flash.transform, () => Vm.Hero.OnTrain(r.Stat), 0x37474F, UiKit.Caption);
                var brt = buy.GetComponent<RectTransform>();
                UiKit.Place(brt, 752, 8, 288, 112);
                var reason = UiKit.Text("Reason", brt, UiKit.Micro, Palette.Muted, TextAlignmentOptions.Bottom);
                UiKit.Fill(reason.rectTransform, 4, 70, 4, 2);
                ButtonBinding.Bind(r.Buy, buy, reason);
                Anchor(r.Anchor, brt);
                y += 136;
            }
            SetContentHeight(y + 32);
        }

        public override void Tick(double now)
        {
            var rows = Vm.Hero.Rows;
            for (int i = 0; i < rows.Count && i < _rowFlash.Count; i++)
                if (rows[i].FlashSerial != _rowSerial[i])
                {
                    _rowSerial[i] = rows[i].FlashSerial;
                    Ui.Tweens.Pulse(_rowFlash[i].rectTransform, now, 0.03f, 0.25);   // StatTrained row flash
                }
        }
    }
}
