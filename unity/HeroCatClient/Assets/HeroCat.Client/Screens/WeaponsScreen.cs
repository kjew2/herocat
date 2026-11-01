using System.Collections.Generic;
using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>SCR_WEAPONS (07 section 4.5; issue #116).</summary>
    public sealed class WeaponsScreen : CollectionScreenView
    {
        protected override string TitleKey => UiKeys.WeaponsTitle;
        protected override CollectionScreenVm CollectionVm => Vm.Weapons;
        protected override string EquipAnchor => WeaponsVm.AnchorEquip;
        protected override void OnEquip() => Vm.Weapons.OnEquip();

        protected override void BuildContent()
        {
            var w = Vm.Weapons;
            BuildChips();
            var eq = Line("Equipped", 136, UiKit.Body);
            w.EquippedName.Bind(new ActionTextSink(_ => eq.text = w.EquippedName.Text + "  " + Vm.Localizer.Format(UiKeys.HeroLevel, w.EquippedLevel.Text) + "  " + w.EquippedAtkPct.Text));
            w.EquippedLevel.Bind(new ActionTextSink(_ => eq.text = w.EquippedName.Text + "  " + Vm.Localizer.Format(UiKeys.HeroLevel, w.EquippedLevel.Text) + "  " + w.EquippedAtkPct.Text));
            w.EquippedAtkPct.Bind(new ActionTextSink(_ => eq.text = w.EquippedName.Text + "  " + Vm.Localizer.Format(UiKeys.HeroLevel, w.EquippedLevel.Text) + "  " + w.EquippedAtkPct.Text));
            var passive = Line("Passive", 192, UiKit.Body, Palette.Gold);
            void WritePassive() => passive.text = Vm.Localizer.Format(UiKeys.CollPassive, w.PassiveTotalPct.Text, w.OwnedCount.Text, w.PoolSize.Text);
            w.PassiveTotalPct.Bind(new ActionTextSink(_ => WritePassive()));
            w.OwnedCount.Bind(new ActionTextSink(_ => WritePassive()));
            w.PoolSize.Bind(new ActionTextSink(_ => WritePassive()));
            BuildSortFilterAndGrid(256);
            BuildSelectedPanel();
        }
    }
}
