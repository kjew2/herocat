#if UNITY_EDITOR || DEVELOPMENT_BUILD || HEROCAT_CHEATS
using System;
using HeroCat.Client.Core;
using UnityEngine.UIElements;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// Debug panel (specs/07 section 11.8). Shown in the Editor and Development builds, or with HEROCAT_CHEATS
    /// (see Shell.DebugEnabled). The whole file compiles out of Release builds (specs/07 section 13). Everything goes through GameViewModel.Debug* which calls Simulation.Debug.
    /// </summary>
    public sealed class DebugScreen : ScreenBase
    {
        readonly Label _info;

        /// <summary>Frames per second, written by GameRoot.</summary>
        public static float Fps;

        public DebugScreen(GameViewModel vm) : base(vm)
        {
            Section("Gold",
                ("+1x stat cost", () => vm.DebugGrantGoldStatCosts(1)),
                ("+100x stat cost", () => vm.DebugGrantGoldStatCosts(100)),
                ("+1e6", () => vm.DebugGrantGold(1e6)),
                ("+1e12", () => vm.DebugGrantGold(1e12)));
            Section("Gems",
                ("+1,000", () => vm.DebugGrantGems(1000)),
                ("+10,000", () => vm.DebugGrantGems(10000)));
            Section("Heroes",
                ("+1 copy of all", () => vm.DebugGrantAllHeroes(1)),
                ("+10 copies of selected", () => vm.DebugGrantSelectedHero(10)));
            Section("Stage (resets fight)",
                ("1", () => vm.DebugSetStage(1)),
                ("49", () => vm.DebugSetStage(49)),
                ("99", () => vm.DebugSetStage(99)),
                ("149", () => vm.DebugSetStage(149)),
                ("299", () => vm.DebugSetStage(299)),
                ("499", () => vm.DebugSetStage(499)),
                ("-10", () => vm.DebugAddStages(-10)),
                ("+10", () => vm.DebugAddStages(10)),
                ("+50", () => vm.DebugAddStages(50)));
            Section("Time skip: simulate (real ticks)",
                ("Run 10 min", () => vm.DebugRunHours(1.0 / 6)),
                ("Run 1 h", () => vm.DebugRunHours(1)),
                ("Run 8 h", () => vm.DebugRunHours(8)));
            Section("Time skip: wall clock (offline earnings)",
                ("Clock +2 min", () => vm.DebugClockSkip(2.0 / 60)),
                ("Clock +1 h", () => vm.DebugClockSkip(1)),
                ("Clock +8 h", () => vm.DebugClockSkip(8)),
                ("Clock +1 day", () => vm.DebugClockSkip(24)));
            Section("Speed",
                ("x1", () => vm.DebugSetTimeScale(1)),
                ("x5", () => vm.DebugSetTimeScale(5)),
                ("x20", () => vm.DebugSetTimeScale(20)));
            Section("Profile",
                ("Save now", vm.DebugRequestSave),
                ("Validate", vm.DebugValidate),
                ("Reset profile", vm.DebugResetProfile));
            _info = Ui.Text("", Ui.Small, Palette.Muted);
            Root.Add(_info);
        }

        void Section(string title, params (string label, Action action)[] buttons)
        {
            var p = Ui.Panel();
            p.Add(Ui.Text(title, Ui.Small, Palette.Muted));
            var row = Ui.Row();
            foreach (var (label, action) in buttons) row.Add(Ui.Btn(label, action, Ui.Small));
            p.Add(row);
            Root.Add(p);
        }

        public override string Title => "Debug";

        public override void Refresh()
        {
            Ui.Set(_info, "content " + Vm.ContentVersion + "   profile " + Vm.ProfileId + "\nsave: " + Vm.SaveDescription +
                          "\nspeed x" + Vm.TimeScale + "   clock offset " + TextFmt.Duration(Vm.ClockOffsetSeconds) +
                          "   outbox dropped " + Vm.OutboxDropped + "   fps " + Math.Round(Fps));
        }
    }
}
#endif
