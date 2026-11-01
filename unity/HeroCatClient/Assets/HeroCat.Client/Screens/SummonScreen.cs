using System.Collections.Generic;
using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>SCR_SUMMON (07 section 4.10; issue #117).</summary>
    public sealed class SummonScreen : FullScreenView
    {
        protected override string TitleKey => UiKeys.SummonTitle;
        RectTransform _rates;
        readonly List<TextMeshProUGUI> _rateLines = new List<TextMeshProUGUI>();
        int _ratesVersion = -1;

        protected override void BuildContent()
        {
            var s = Vm.Summon;
            var tabImgs = new Image[s.Banners.Count];
            for (int i = 0; i < s.Banners.Count; i++)
            {
                var banner = s.Banners[i];
                var b = Btn("Banner" + banner, 24 + i * 340, 8, 328, 112, () => Vm.Summon.SelectBanner(banner), Palette.PanelRaised, UiKit.Caption);
                s.TabNames[i].Bind(UiKit.LabelOf(b).Sink());
                tabImgs[i] = b.GetComponent<Image>();
            }
            s.SelectedBanner.Bind(new ActionSink<Banner>(sel =>
            {
                for (int i = 0; i < tabImgs.Length; i++) tabImgs[i].color = UiKit.C(s.Banners[i] == sel ? Palette.Accent : Palette.PanelRaised, s.Banners[i] == sel ? 0.6f : 1f);
            }));

            var lvl = Line("BannerLevel", 136, UiKit.Body, Palette.Text, 24, 300);
            s.BannerLevel.Bind(new ActionTextSink(t => lvl.text = Vm.Localizer.Format(UiKeys.SummonBannerLevel, t)));
            var bar = UiKit.Bar("BannerExp", Content, Palette.Accent, out var fill);
            UiKit.Place(bar, 340, 148, 420, 32);
            s.BannerFill.Bind(fill.Sink());
            var exp = Line("BannerExpText", 136, UiKit.Caption, Palette.Muted, 776, 280);
            s.BannerExp.Bind(new ActionTextSink(_ => exp.text = s.BannerExp.Text + " / " + s.BannerExpToNext.Text));
            s.BannerExpToNext.Bind(new ActionTextSink(_ => exp.text = s.BannerExp.Text + " / " + s.BannerExpToNext.Text));

            var pity = Line("Pity", 208, UiKit.Body, Palette.Text, 24, 400);
            s.Pity.Bind(new ActionTextSink(_ => pity.text = Vm.Localizer.Format(UiKeys.SummonPity, s.Pity.Text, s.PityHard.Text)));
            s.PityHard.Bind(new ActionTextSink(_ => pity.text = Vm.Localizer.Format(UiKeys.SummonPity, s.Pity.Text, s.PityHard.Text)));
            var soft = Line("SoftPity", 208, UiKit.Caption, Palette.Muted, 440, 600);
            s.PityText.Bind(soft.Sink());
            var pbar = UiKit.Bar("PityBar", Content, Palette.Gold, out var pfill);
            UiKit.Place(pbar, 24, 272, 1032, 32);
            s.PityFill.Bind(pfill.Sink());
            var marker = UiKit.Panel("SoftMarker", pbar, Palette.Text).rectTransform;
            s.PitySoftMarker.Bind(new FillMarkerSink(marker));

            var rates = Btn("Rates", 24, 328, 320, 112, s.ToggleRates, Palette.PanelRaised, UiKit.Caption);
            UiKit.LabelOf(rates).text = Vm.Localizer.Text(UiKeys.SummonRates);
            var p1 = Btn("Pull1", 24, 456, 500, 136, s.OnPull1, 0x1565C0, UiKit.Body);
            var r1 = Line("Pull1Reason", 596, UiKit.Micro, Palette.Muted, 24, 500, 40);
            ButtonBinding.Bind(s.Pull1, p1, r1);
            Anchor(SummonVm.AnchorPull1, p1.GetComponent<RectTransform>());
            var p10 = Btn("Pull10", 556, 456, 500, 136, s.OnPull10, 0x6A1B9A, UiKit.Body);
            var r10 = Line("Pull10Reason", 596, UiKit.Micro, Palette.Muted, 556, 500, 40);
            ButtonBinding.Bind(s.Pull10, p10, r10);
            Anchor(SummonVm.AnchorPull10, p10.GetComponent<RectTransform>());
            var ticket = Btn("Ticket", 24, 648, 500, 112, s.OnTicket, 0x37474F, UiKit.Caption);
            ButtonBinding.Bind(s.Ticket, ticket);
            Anchor(SummonVm.AnchorTicket, ticket.GetComponent<RectTransform>());
            var guarantee = Line("Guarantee", 776, UiKit.Caption, Palette.Muted);
            s.GuaranteeText.Bind(guarantee.Sink());

            _rates = UiKit.Panel("RatesPanel", Content, Palette.PanelRaised).rectTransform;
            UiKit.Place(_rates, 16, 840, 1048, 360);
            s.RatesOpen.Bind(new ActiveSink(_rates.gameObject));
            _rates.gameObject.SetActive(false);
            SetContentHeight(1240);
        }

        public override void Tick(double now)
        {
            var s = Vm.Summon;
            if (!s.RatesOpen.Value || s.RatesVersion == _ratesVersion) return;
            _ratesVersion = s.RatesVersion;
            foreach (var l in _rateLines) Destroy(l.gameObject);
            _rateLines.Clear();
            for (int i = 0; i < s.Rates.Count; i++)
            {
                var line = s.Rates[i];
                var t = UiKit.Text("Rate" + i, _rates, UiKit.Body, Palette.RarityColor(line.Rarity));
                UiKit.Place(t.rectTransform, 24, 16 + i * 64, 1000, 56);
                t.text = Palette.ShapeGlyph(line.Rarity) + " " + Vm.Localizer.Text(UiKeys.RarityName(line.Rarity)) + "  " +
                         TextFmt.PctPlain(line.Rate * 100.0);
                _rateLines.Add(t);
            }
        }
    }
}
