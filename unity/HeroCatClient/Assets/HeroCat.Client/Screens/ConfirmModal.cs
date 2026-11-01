using System.Collections.Generic;
using HeroCat.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>SCR_CONFIRM (07 section 4.26; issue #118). Scrim and back equal Cancel.</summary>
    public sealed class ConfirmModal : ModalView
    {
        TMP_InputField _typed;

        protected override void BuildCard()
        {
            var c = Vm.Confirm;
            c.Title.Bind(Line("Title", 32, UiKit.Title, Palette.Text, 72).Sink());
            c.Body.Bind(Line("Body", 120, UiKit.Body, Palette.Text, 200).Sink());
            var cost = Line("Cost", 336, UiKit.Body, Palette.Gold);
            c.CostLine.Bind(cost.Sink());
            c.CostVisible.Bind(new ActiveSink(cost.gameObject));

            var field = UiKit.Panel("Typed", Card, Palette.Panel, 1f, raycast: true);
            UiKit.Place(field.rectTransform, 120, 420, 720, 112);
            _typed = field.gameObject.AddComponent<TMP_InputField>();
            var text = UiKit.Text("Text", field.transform, UiKit.Body);
            UiKit.Fill(text.rectTransform, 16, 8, 16, 8);
            _typed.textViewport = field.rectTransform;
            _typed.textComponent = text;
            _typed.onValueChanged.AddListener(c.OnTyped);
            c.TypedVisible.Bind(new ActiveSink(field.gameObject));

            var cancel = Btn("Cancel", 80, 600, 380, c.OnCancel, Palette.Panel);
            ButtonBinding.Bind(c.Cancel, cancel);
            var ok = Btn("Confirm", 500, 600, 380, c.OnConfirm, 0x2E7D32);
            ButtonBinding.Bind(c.Confirm, ok);
            c.Destructive.Bind(new ColorFlagSink(ok.GetComponent<Image>(), UiKit.C(Palette.Bad), UiKit.C(0x2E7D32)));
        }

        public override void OnShown(ScreenEntry e)
        {
            if (_typed != null) _typed.text = "";
        }
    }
}
