using System.Collections.Generic;
using HeroCat.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>SCR_OFFLINE_REWARD (07 section 4.18; issue #118): not dismissible by scrim or back.</summary>
    public sealed class OfflineRewardModal : ModalView
    {
        protected override void BuildCard()
        {
            var o = Vm.OfflineReward;
            Line("Title", 32, UiKit.Title, Palette.Text, 72).text = Vm.Localizer.Text(UiKeys.OfflineTitle);
            o.Away.Bind(Line("Away", 112).Sink());
            o.Rewards.Bind(Line("Rewards", 176, UiKit.Caption, Palette.Muted).Sink());
            var gold = Line("Gold", 248, UiKit.Display, Palette.Gold, 88);
            o.Gold.Bind(gold.Sink());
            var exp = Line("Exp", 344, UiKit.Title, Palette.Accent, 64);
            o.Exp.Bind(exp.Sink());
            var tank = Line("Tank", 424, UiKit.Body, Palette.FishCoin);
            o.TankLine.Bind(tank.Sink());
            o.TankVisible.Bind(new ActiveSink(tank.gameObject));
            o.StageLine.Bind(Line("Stage", 488, UiKit.Caption, Palette.Muted).Sink());
            var claim = Btn("Claim", 280, 600, 400, o.OnClaim, 0x2E7D32);
            ButtonBinding.Bind(o.Claim, claim);
            Anchor(OfflineRewardVm.AnchorClaim, claim.GetComponent<RectTransform>());
        }
    }
}
