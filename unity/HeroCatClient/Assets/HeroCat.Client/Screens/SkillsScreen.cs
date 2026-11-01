using System.Collections.Generic;
using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>SCR_SKILLS (07 section 4.6; issue #116).</summary>
    public sealed class SkillsScreen : CollectionScreenView
    {
        protected override string TitleKey => UiKeys.SkillsTitle;
        protected override CollectionScreenVm CollectionVm => Vm.Skills;
        protected override string EquipAnchor => SkillsVm.AnchorAuto + ".equip";
        protected override void OnEquip() => Vm.Skills.OnEquip();

        protected override void BuildContent()
        {
            var s = Vm.Skills;
            BuildChips();
            Line("SlotsTitle", 136, UiKit.Caption, Palette.Muted, 24, 600).text = Vm.Localizer.Text(UiKeys.SkillsSlots);
            var auto = Btn("Auto", 720, 128, 320, 112, s.OnAutoEquip, 0x37474F, UiKit.Caption);
            ButtonBinding.Bind(s.AutoEquip, auto);
            Anchor(SkillsVm.AnchorAuto, auto.GetComponent<RectTransform>());
            var dot = UiKit.CirclePanel("Dot", auto.transform, UiKit.C(Palette.Bad));
            UiKit.Place(dot.rectTransform, 284, 4, 28, 28);
            s.AutoEquipWouldChange.Bind(new ActiveSink(dot.gameObject));
            for (int i = 0; i < s.Slots.Length; i++)
            {
                int slot = i;
                var b = Btn("Slot" + i, 24 + i * 172, 248, 160, 112, () => Vm.Skills.OnSlot(slot), Palette.PanelRaised, UiKit.Caption);
                var img = b.GetComponent<Image>();
                var label = UiKit.LabelOf(b);
                Anchor(SkillsVm.SlotAnchor(i), b.GetComponent<RectTransform>());
                s.Slots[i].Bind(new ActionSink<SlotLook>(look =>
                {
                    img.color = look.IsEmpty ? UiKit.C(Palette.PanelRaised) : UiKit.Rarity(look.Rarity);
                    label.text = (slot + 1) + (look.IsEmpty ? "" : ": " + Palette.ShapeGlyph(look.Rarity) + look.Level);
                }));
            }
            BuildSortFilterAndGrid(376);
            BuildSelectedPanel();
        }
    }
}
