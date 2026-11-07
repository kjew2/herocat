using System.Collections.Generic;
using HeroCat.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>SCR_ITEM_DETAIL (07 section 4.19; issue #116).</summary>
    public sealed class ItemDetailModal : ModalView
    {
        Image _icon, _art;
        TextMeshProUGUI _glyph;
        string _artShown;

        /// <summary>Card art follows the item, not its rarity: two Legendaries in a row still swap the splash.</summary>
        public override void Tick(double now)
        {
            var d = Vm.ItemDetail;
            string want = d.ItemId == null ? null : d.ItemId + "|" + d.ArtAddress;
            if (want == _artShown || _icon == null) return;
            _artShown = want;
            if (_art != null) Destroy(_art.gameObject);
            // Splash for an L+ companion, else its portrait, else the icon (00 decision 108, issue #291).
            _art = SpriteLibrary.Overlay("Art", _icon.transform, SpriteLibrary.First(d.ArtAddress, d.FallbackArtAddress), 4);
            _glyph.text = _art != null ? "" : Palette.ShapeGlyph(d.Rarity.Value);
        }

        protected override void BuildCard()
        {
            var d = Vm.ItemDetail;
            var name = Line("Name", 24, UiKit.Title, Palette.Text, 64, TextAlignmentOptions.MidlineLeft);
            d.Name.Bind(name.Sink());
            var kind = Line("Kind", 88, UiKit.Caption, Palette.Muted, 40, TextAlignmentOptions.MidlineLeft);
            d.KindLabel.Bind(kind.Sink());
            var close = UiKit.Button("Close", Card, d.Close, Palette.Panel, UiKit.Title);
            UiKit.Place(close.GetComponent<RectTransform>(), UiConstants.ModalWidth - 136, 16, 112, 112);
            UiKit.LabelOf(close).text = "✕";
            var icon = UiKit.Panel("Icon", Card, Palette.Muted);
            UiKit.Place(icon.rectTransform, 380, 136, 200, 200);
            var glyph = UiKit.Text("Glyph", icon.transform, UiKit.Display, 0x121212, TextAlignmentOptions.Center);
            UiKit.Fill(glyph.rectTransform);
            _icon = icon;
            _glyph = glyph;
            d.Rarity.Bind(new ActionSink<HeroCat.Sim.Content.Rarity>(r =>
            {
                icon.color = UiKit.Rarity(r);
                _artShown = null;   // re-pick the art on the next Tick
                glyph.text = Palette.ShapeGlyph(r);
                kind.text = Vm.Localizer.Text(UiKeys.KindKey(d.Banner)) + " · " + Vm.Localizer.Text(UiKeys.RarityName(r));   // rarity word (07 section 11)
            }));
            var ribbon = UiKit.Panel("New", Card, Palette.Bad);
            UiKit.Place(ribbon.rectTransform, 600, 136, 160, 56);
            var rt = UiKit.Text("Text", ribbon.transform, UiKit.Caption, 0xFFFFFF, TextAlignmentOptions.Center, bold: true);
            UiKit.Fill(rt.rectTransform);
            rt.text = Vm.Localizer.Text(UiKeys.DetailNewRibbon);
            d.NewRibbon.Bind(new ActiveSink(ribbon.gameObject));
            d.LevelLine.Bind(Line("Level", 352).Sink());
            d.StatLine.Bind(Line("Stat", 416).Sink());
            d.NextLine.Bind(Line("Next", 472, UiKit.Caption, Palette.Muted).Sink());
            d.PassiveLine.Bind(Line("Passive", 528, UiKit.Caption, Palette.Muted).Sink());
            d.SourceLine.Bind(Line("Source", 584, UiKit.Caption, Palette.Muted).Sink());
            var compareLine = Line("Compare", 640, UiKit.Caption, Palette.Accent);
            d.CompareLine.Bind(compareLine.Sink());
            d.CompareVisible.Bind(new ActiveSink(compareLine.gameObject));
            var equip = Btn("Equip", 120, 720, 340, () => d.OnEquip(), 0x2E7D32);
            var reason = Line("Reason", 852, UiKit.Micro, Palette.Muted, 36);
            ButtonBinding.Bind(d.Equip, equip, reason);
            Anchor(ItemDetailVm.AnchorEquip, equip.GetComponent<RectTransform>());
            var compare = Btn("Compare", 500, 720, 340, d.ToggleCompare, Palette.Panel);
            UiKit.LabelOf(compare).text = Vm.Localizer.Text(UiKeys.DetailCompareButton);
        }
    }
}
