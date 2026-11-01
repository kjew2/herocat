using System.Collections.Generic;
using HeroCat.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// SCR_ERROR, blocking variant (07 section 4.27; issue #118): above everything, its own scrim, title and body keys
    /// <c>err.&lt;code_lower&gt;.*</c>, the code and support id in small text, and 01's per-code actions.
    /// </summary>
    public sealed class ErrorModal : ScreenView
    {
        RectTransform _actions;
        int _version = -1;

        protected override void Build()
        {
            UiKit.Fill(Root);
            var scrim = UiKit.Panel("Scrim", Root, Palette.Scrim, 0.85f, raycast: true);
            UiKit.Fill(scrim.rectTransform);
            var card = UiKit.Panel("Card", Root, Palette.PanelRaised, 1f, raycast: true).rectTransform;
            card.anchorMin = card.anchorMax = card.pivot = new Vector2(0.5f, 0.5f);
            card.sizeDelta = new Vector2(UiConstants.ModalWidth, 760);
            ErrorViews.Build(card, Vm.Error, Vm.Localizer, out _actions);
        }

        public override void Tick(double now)
        {
            if (Vm.Error.ActionsVersion == _version) return;
            _version = Vm.Error.ActionsVersion;
            ErrorViews.Actions(_actions, Vm.Error, Vm.Localizer);
        }
    }
}
