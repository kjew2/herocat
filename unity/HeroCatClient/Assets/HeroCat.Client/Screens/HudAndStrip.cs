using HeroCat.Client.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace HeroCat.Client.Screens
{
    /// <summary>Content-panel screen: built once, refreshed with change-only setters while visible.</summary>
    public abstract class ScreenBase
    {
        public readonly VisualElement Root = Ui.Col();
        protected readonly GameViewModel Vm;

        protected ScreenBase(GameViewModel vm) { Vm = vm; }

        public abstract string Title { get; }
        public abstract void Refresh();
    }

    /// <summary>HUD bar (07 section 4.3): gold, diamonds, fish coins, hero level.</summary>
    public sealed class HudBar
    {
        public readonly VisualElement Root = Ui.Row();
        readonly Label _gold, _diamonds, _fish, _level;
        readonly GameViewModel _vm;

        public HudBar(GameViewModel vm)
        {
            _vm = vm;
            Root.style.backgroundColor = Ui.C(Palette.Panel);
            Ui.Pad(Root, 10);
            Root.style.justifyContent = Justify.SpaceBetween;
            _gold = Ui.Text("", Ui.Big, Palette.Gold);
            _diamonds = Ui.Text("", Ui.Big, Palette.Diamond);
            _fish = Ui.Text("", Ui.Body, Palette.FishCoin);
            _level = Ui.Text("", Ui.Body);
            Root.Add(_gold); Root.Add(_diamonds); Root.Add(_fish); Root.Add(_level);
        }

        public void Refresh()
        {
            var h = _vm.Hud;
            // TODO(#187): localization keys (00 contract 10); TODO(#112): 07 section 7 formatters.
            Ui.Set(_gold, "Gold " + TextFmt.Num(h.Gold));
            Ui.Set(_diamonds, "Diamonds " + TextFmt.Int(h.Diamonds));
            Ui.Set(_fish, "Fish Coins " + TextFmt.Num(h.FishCoin));
            Ui.Set(_level, "Lv " + h.HeroLevel);
        }
    }

    /// <summary>Always-visible combat strip: stage, chapter, kill progress, BOSS / WALL badges, enemy HP bar. TODO(#113).</summary>
    public sealed class CombatStrip
    {
        public readonly VisualElement Root = Ui.Panel();
        readonly GameViewModel _vm;
        readonly Label _stage, _bossTag, _kills, _wall;
        readonly Ui.Bar _hp;

        public CombatStrip(GameViewModel vm)
        {
            _vm = vm;
            var line = Ui.Row();
            _stage = Ui.Heading("");
            _bossTag = Ui.Text(" BOSS ", Ui.Body, 0x000000);
            _bossTag.style.backgroundColor = Ui.C(0xFFA726);
            _bossTag.style.marginLeft = 12;
            _kills = Ui.Text("", Ui.Body);
            _kills.style.marginLeft = 16;
            _wall = Ui.Text(" WALL ", Ui.Body, 0xFFFFFF);
            _wall.style.backgroundColor = Ui.C(Palette.Bad);
            _wall.style.marginLeft = 16;
            line.Add(_stage); line.Add(_bossTag); line.Add(_kills); line.Add(_wall);
            Root.Add(line);
            _hp = new Ui.Bar(36, Palette.Bad);
            Root.Add(_hp.Root);
        }

        public void Refresh()
        {
            var c = _vm.Combat;
            Ui.Set(_stage, "Stage " + c.Stage + "  (chapter " + c.Chapter + ")");
            Ui.Show(_bossTag, c.IsBossStage);
            Ui.Set(_kills, "kills " + c.KillsInStage + "/" + c.KillsPerStage);
            Ui.Show(_wall, c.IsWalled);
            double hpFrac = c.EnemyMaxHp > 0 ? c.EnemyHp / c.EnemyMaxHp : 0;
            _hp.Set(hpFrac, c.EnemyMaxHp > 0 ? TextFmt.Num(c.EnemyHp) + " / " + TextFmt.Num(c.EnemyMaxHp) : "no fight yet");
        }
    }
}
