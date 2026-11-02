#if UNITY_EDITOR || DEVELOPMENT_BUILD || HEROCAT_CHEATS
using HeroCat.Client.Core;
using TMPro;
using UnityEngine;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// Dev-build cheats (07 sections 4.20 and 17.3 rule 7): time skip, clock offset, time scale, save, reset profile, FPS,
    /// and diamond / companion-ticket grants for testing summons and art (issue #291).
    /// Compiled only under UNITY_EDITOR, DEVELOPMENT_BUILD or HEROCAT_CHEATS, so a Release build has no cheat UI. Labels are
    /// developer text, not player copy (the SCR_SETTINGS Debug section is MVP). Unity-only: not compiled in cloud sessions.
    /// </summary>
    public sealed class DebugPanel : MonoBehaviour
    {
        GameViewModel _vm;
        RectTransform _panel;
        TextMeshProUGUI _info;
        float _acc;
        int _frames;
        float _fps;

        public static DebugPanel Create(RectTransform parent, GameViewModel vm)
        {
            var rt = UiKit.Rect("Debug", parent);
            var c = rt.gameObject.AddComponent<Canvas>();
            c.overrideSorting = true;
            c.sortingOrder = 95;
            rt.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            UiKit.Fill(rt);
            var p = rt.gameObject.AddComponent<DebugPanel>();
            p.Build(vm);
            return p;
        }

        void Build(GameViewModel vm)
        {
            _vm = vm;
            var toggle = UiKit.Button("Toggle", transform, () => _panel.gameObject.SetActive(!_panel.gameObject.activeSelf), 0x424242, UiKit.Micro);
            UiKit.Place(toggle.GetComponent<RectTransform>(), 960, 132, 112, 112);
            UiKit.LabelOf(toggle).text = "dbg";
            _panel = UiKit.Panel("Panel", transform, Palette.PanelRaised, 0.97f, raycast: true).rectTransform;
            UiKit.Place(_panel, 40, 260, 1000, 940);
            string[] labels = { "Run 10 min", "Run 1 h", "Run 8 h", "Clock +2 min", "Clock +1 h", "Clock +8 h", "Clock +1 day", "x1", "x5", "x20", "Save now", "Reset profile",
                                "+10K diamonds", "+100K diamonds", "+50 comp tickets" };
            UnityEngine.Events.UnityAction[] actions =
            {
                () => vm.DebugRunHours(1.0 / 6), () => vm.DebugRunHours(1), () => vm.DebugRunHours(8),
                () => vm.DebugClockSkip(2.0 / 60), () => vm.DebugClockSkip(1), () => vm.DebugClockSkip(8), () => vm.DebugClockSkip(24),
                () => vm.DebugSetTimeScale(1), () => vm.DebugSetTimeScale(5), () => vm.DebugSetTimeScale(20),
                vm.DebugRequestSave, vm.DebugResetProfile,
                () => vm.DebugGrant(HeroCat.Sim.Content.Currency.DIAMOND, 10_000), () => vm.DebugGrant(HeroCat.Sim.Content.Currency.DIAMOND, 100_000),
                () => vm.DebugGrant(HeroCat.Sim.Content.Currency.TICKET_SUMMON_COMPANION, 50),
            };
            for (int i = 0; i < labels.Length; i++)
            {
                var b = UiKit.Button(labels[i], _panel, actions[i], 0x37474F, UiKit.Caption);
                UiKit.Place(b.GetComponent<RectTransform>(), 24 + i % 3 * 324, 24 + i / 3 * 136, 304, 120);
                UiKit.LabelOf(b).text = labels[i];
            }
            _info = UiKit.Text("Info", _panel, UiKit.Micro, Palette.Muted);
            UiKit.Place(_info.rectTransform, 24, 720, 952, 200);
            _panel.gameObject.SetActive(false);
        }

        public void Tick(float dt)
        {
            _acc += dt;
            _frames++;
            if (_acc >= 0.5f)
            {
                _fps = _frames / _acc;
                _acc = 0;
                _frames = 0;
            }
            if (!_panel.gameObject.activeSelf) return;
            _info.text = "fps " + _fps.ToString("0") + "   scale x" + _vm.TimeScale + "   clock +" + _vm.ClockOffsetSeconds + " s\n" +
                         "profile " + _vm.ProfileId + "\ncontent " + _vm.ContentVersion + "\n" + _vm.SaveDescription;
        }
    }
}
#endif
