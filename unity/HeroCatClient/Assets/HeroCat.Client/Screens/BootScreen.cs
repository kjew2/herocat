using System.Collections.Generic;
using HeroCat.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// SCR_BOOT (07 section 4.1; issue #118): logo placeholder, weighted progress, the step key, the version caption, and
    /// the blocking SCR_ERROR when boot fails (Retry / Update / Contact support). Back is ignored. Built before any
    /// GameViewModel exists, so it binds ClientSession's BootVm and BootError directly.
    /// </summary>
    public sealed class BootScreen : MonoBehaviour
    {
        RectTransform _actions;
        ErrorVm _error;
        ILocalizer _loc;
        int _version = -1;

        public static BootScreen Create(Transform parent, BootVm boot, ErrorVm error, ILocalizer loc)
        {
            var go = new GameObject("SCR_BOOT", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var s = go.AddComponent<BootScreen>();
            s.Build(boot, error, loc);
            return s;
        }

        void Build(BootVm boot, ErrorVm error, ILocalizer loc)
        {
            _error = error;
            _loc = loc;
            var root = UiRoot.MakeCanvas(gameObject, 90);
            UiKit.Fill(UiKit.Panel("Bg", root, Palette.Background, 1f, raycast: true).rectTransform);
            var logo = UiKit.Panel("Logo", root, Palette.Accent).rectTransform;
            logo.anchorMin = logo.anchorMax = logo.pivot = new Vector2(0.5f, 0.65f);
            logo.sizeDelta = new Vector2(320, 320);
            var title = UiKit.Text("Title", root, UiKit.Display, Palette.Text, TextAlignmentOptions.Center, bold: true);
            var tr = title.rectTransform;
            tr.anchorMin = tr.anchorMax = tr.pivot = new Vector2(0.5f, 0.5f);
            tr.sizeDelta = new Vector2(900, 96);
            title.text = loc.Text(UiKeys.BootTitle);
            var bar = UiKit.Bar("Progress", root, Palette.Accent, out var fill);
            bar.anchorMin = bar.anchorMax = bar.pivot = new Vector2(0.5f, 0.4f);
            bar.sizeDelta = new Vector2(800, 32);
            boot.Progress.Bind(fill.Sink());
            var step = UiKit.Text("Step", root, UiKit.Body, Palette.Muted, TextAlignmentOptions.Center);
            var sr = step.rectTransform;
            sr.anchorMin = sr.anchorMax = sr.pivot = new Vector2(0.5f, 0.36f);
            sr.sizeDelta = new Vector2(900, 56);
            boot.StepKey.Bind(step.Sink());
            var caption = UiKit.Text("Caption", root, UiKit.Micro, Palette.Muted, TextAlignmentOptions.Center);
            var cr = caption.rectTransform;
            cr.anchorMin = cr.anchorMax = cr.pivot = new Vector2(0.5f, 0.06f);
            cr.sizeDelta = new Vector2(1000, 40);
            boot.Caption.Bind(caption.Sink());

            if (error == null) return;
            var scrim = UiKit.Panel("ErrorScrim", root, Palette.Scrim, 0.85f, raycast: true);
            UiKit.Fill(scrim.rectTransform);
            var card = UiKit.Panel("Card", scrim.transform, Palette.PanelRaised, 1f, raycast: true).rectTransform;
            card.anchorMin = card.anchorMax = card.pivot = new Vector2(0.5f, 0.5f);
            card.sizeDelta = new Vector2(UiConstants.ModalWidth, 760);
            ErrorViews.Build(card, error, loc, out _actions);
            error.Visible.Bind(new ActiveSink(scrim.gameObject));
        }

        void Update()
        {
            if (_error == null || _error.ActionsVersion == _version) return;
            _version = _error.ActionsVersion;
            ErrorViews.Actions(_actions, _error, _loc);
        }
    }
}
