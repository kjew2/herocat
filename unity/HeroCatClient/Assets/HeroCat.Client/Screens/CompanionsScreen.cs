using HeroCat.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>SCR_COMPANIONS (07 section 4.7; issues #181, #291): MP line, four slots, MP budget, Auto-equip, the grid.</summary>
    public sealed class CompanionsScreen : CollectionScreenView
    {
        protected override string TitleKey => UiKeys.CompanionsTitle;
        protected override CollectionScreenVm CollectionVm => Vm.Companions;
        protected override string EquipAnchor => CompanionsVm.AnchorAuto + ".equip";
        protected override void OnEquip() => Vm.Companions.OnEquip();

        protected override void BuildContent()
        {
            var c = Vm.Companions;
            BuildChips();
            c.MpLine.Bind(Line("Mp", 136, UiKit.Body, Palette.Text, 24, 760).Sink());
            var research = Btn("Research", 800, 128, 240, 64, c.Research.OnTap, Palette.PanelRaised, UiKit.Caption);   // "(Research +)", MVP
            UiKit.LabelOf(research).text = Vm.Localizer.Text(UiKeys.CompanionsResearch);
            c.Research.Visible.Bind(new ActiveSink(research.gameObject));
            var budget = Line("Budget", 192, UiKit.Caption, Palette.Muted);
            c.BudgetLine.Bind(budget.Sink());
            c.OverBudget.Bind(new ActionFlagSink(over => budget.color = UiKit.C(over ? Palette.Bad : Palette.Muted)));
            Line("SlotsTitle", 248, UiKit.Caption, Palette.Muted, 24, 600).text = Vm.Localizer.Text(UiKeys.CompanionsSlots);
            var auto = Btn("Auto", 720, 240, 320, 112, c.OnAutoEquip, 0x37474F, UiKit.Caption);
            ButtonBinding.Bind(c.AutoEquip, auto);
            Anchor(CompanionsVm.AnchorAuto, auto.GetComponent<RectTransform>());
            for (int i = 0; i < c.Slots.Length; i++)
            {
                int slot = i;
                var b = Btn("Slot" + i, 24 + i * 256, 360, 240, 200, () => Vm.Companions.OnSlot(slot), Palette.PanelRaised, UiKit.Caption);
                var img = b.GetComponent<Image>();
                var label = UiKit.LabelOf(b);
                var group = b.GetComponent<CanvasGroup>();
                Anchor(CompanionsVm.SlotAnchor(i), b.GetComponent<RectTransform>());
                Image art = null;
                c.Slots[i].Bind(new ActionSink<SlotLook>(look =>
                {
                    img.color = look.IsEmpty ? UiKit.C(Palette.PanelRaised) : UiKit.Rarity(look.Rarity);
                    if (art != null) Destroy(art.gameObject);
                    art = look.IsEmpty ? null : SpriteLibrary.Overlay("Art", b.transform, SpriteLibrary.Get(look.IconId), 8);
                    label.text = (slot + 1) + (look.IsEmpty ? "" : art != null ? "" : ": " + Palette.ShapeGlyph(look.Rarity) + look.Level);
                }));
                var mpBar = UiKit.Bar("Mp", b.transform, Palette.Diamond, out var mpFill);   // the slot's own pool (03 section 2.4)
                mpBar.anchorMin = new Vector2(0, 0); mpBar.anchorMax = new Vector2(1, 0); mpBar.pivot = new Vector2(0.5f, 0);
                mpBar.sizeDelta = new Vector2(-16, 12); mpBar.anchoredPosition = new Vector2(0, 6);
                c.SlotMp[i].Bind(mpFill.Sink());
            }
            BuildSortFilterAndGrid(584);
            BuildSelectedPanel();
        }
    }
}
