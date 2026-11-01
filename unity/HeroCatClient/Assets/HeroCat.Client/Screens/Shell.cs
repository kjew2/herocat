using System.Collections.Generic;
using HeroCat.Client.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// The whole UI tree (specs/07 section 5): HUD bar, combat strip, tab bar, content panel (one screen per tab),
    /// toast line and a modal layer for the offline-earnings / class-advance popups. Built from code into the
    /// UIDocument root; screens refresh at most 10 times a second, or right after a tap.
    /// </summary>
    public sealed class Shell
    {
        const float RefreshPeriodS = 0.1f;

        /// <summary>True when the Debug tab and the GameViewModel.Debug* cheats are compiled in. The same condition
        /// guards DebugScreen.cs and the debug region of GameViewModel; keep them in sync.</summary>
        public static bool DebugEnabled =>
#if UNITY_EDITOR || DEVELOPMENT_BUILD || HEROCAT_CHEATS
            true;
#else
            false;
#endif

        readonly GameViewModel _vm;
        readonly VisualElement _root;
        readonly HudBar _hud;
        readonly CombatStrip _strip;
        readonly List<ScreenBase> _screens = new List<ScreenBase>();
        readonly List<Button> _tabs = new List<Button>();
        readonly Label _toast;
        readonly VisualElement _modal;
        readonly Label _modalText;
        int _active;
        float _lastRefresh = -1f;
        int _forceFrames = 2;

        public Shell(VisualElement host, GameViewModel vm)
        {
            _vm = vm;
            _root = new VisualElement();
            _root.style.position = Position.Absolute;
            _root.style.left = 0; _root.style.right = 0; _root.style.top = 0; _root.style.bottom = 0;
            _root.style.backgroundColor = Ui.C(Palette.Background);
            _root.style.alignItems = Align.Center;
            host.Add(_root);

            // Portrait column, centred, capped at the 1080 reference width so a landscape editor window still works.
            var column = Ui.Col();
            column.style.flexGrow = 1;
            column.style.width = Length.Percent(100);
            column.style.maxWidth = 1080;
            _root.Add(column);

            _hud = new HudBar(vm);
            column.Add(_hud.Root);
            _strip = new CombatStrip(vm);
            column.Add(_strip.Root);

            var tabBar = Ui.Row();
            tabBar.style.flexWrap = Wrap.NoWrap;
            column.Add(tabBar);

            var content = new ScrollView(ScrollViewMode.Vertical);
            content.style.flexGrow = 1;
            content.style.flexShrink = 1;
            Ui.Pad(content, 8);
            column.Add(content);

            _screens.Add(new CombatScreen(vm));
            _screens.Add(new StatsScreen(vm));
            _screens.Add(new GearScreen(vm));
            _screens.Add(new HeroesScreen(vm));
            _screens.Add(new GachaScreen(vm));
            _screens.Add(new ClassScreen(vm));
#if UNITY_EDITOR || DEVELOPMENT_BUILD || HEROCAT_CHEATS
            _screens.Add(new DebugScreen(vm));
#endif
            for (int i = 0; i < _screens.Count; i++)
            {
                int index = i;
                var tab = Ui.Btn(_screens[i].Title, () => Select(index), Ui.Small);
                tab.style.flexGrow = 1;
                tab.style.flexBasis = 0;
                Ui.Pad(tab, 8, 2);
                Ui.Margin(tab, 2);
                tabBar.Add(tab);
                _tabs.Add(tab);
                content.Add(_screens[i].Root);
            }

            _toast = Ui.Text("", Ui.Body, 0x000000);
            _toast.style.position = Position.Absolute;
            _toast.style.left = 24; _toast.style.right = 24; _toast.style.bottom = 24;
            _toast.style.backgroundColor = Ui.C(0xE0E0E0, 0.95f);
            _toast.style.unityTextAlign = TextAnchor.MiddleCenter;
            Ui.Pad(_toast, 14);
            Ui.Radius(_toast, 10);
            _toast.pickingMode = PickingMode.Ignore;
            _root.Add(_toast);

            _modal = new VisualElement();
            _modal.style.position = Position.Absolute;
            _modal.style.left = 0; _modal.style.right = 0; _modal.style.top = 0; _modal.style.bottom = 0;
            _modal.style.backgroundColor = Ui.C(0x000000, 0.6f);
            _modal.style.justifyContent = Justify.Center;
            _modal.style.alignItems = Align.Center;
            var card = Ui.Panel();
            card.style.width = 900;
            card.style.maxWidth = Length.Percent(95);
            Ui.Border(card, 4, Palette.Gold);
            Ui.Pad(card, 30);
            _modalText = Ui.Text("", Ui.Big);
            _modalText.style.unityTextAlign = TextAnchor.MiddleCenter;
            card.Add(_modalText);
            card.Add(Ui.Btn("OK", () => { _vm.DismissPopup(); Force(); }, Ui.Big));
            _modal.Add(card);
            _root.Add(_modal);

            // Any tap anywhere forces a refresh on the next frames so commands show their result immediately.
            _root.RegisterCallback<PointerUpEvent>(_ => Force(), TrickleDown.TrickleDown);

            Select(0);
        }

        public void Dispose() => _root.RemoveFromHierarchy();

        void Force() => _forceFrames = 2;

        public void Select(int index)
        {
            _active = index;
            for (int i = 0; i < _screens.Count; i++)
            {
                Ui.Show(_screens[i].Root, i == index);
                Ui.Bg(_tabs[i], i == index ? 0x546E7Au : Ui.ButtonBg);
            }
            Force();
        }

        /// <summary>Called every frame by GameRoot after the sim runner; throttles to 10 Hz unless a tap happened.</summary>
        public void Refresh(float now)
        {
            bool force = _forceFrames > 0;
            if (force) _forceFrames--;
            if (!force && now - _lastRefresh < RefreshPeriodS) return;
            _lastRefresh = now;

            _hud.Refresh();
            _strip.Refresh();
            _screens[_active].Refresh();

            string toast = _vm.Toasts.Current(now);
            Ui.Show(_toast, toast != null);
            if (toast != null) Ui.Set(_toast, toast);

            string popup = _vm.CurrentPopup;
            Ui.Show(_modal, popup != null);
            if (popup != null) Ui.Set(_modalText, popup);
        }
    }
}
