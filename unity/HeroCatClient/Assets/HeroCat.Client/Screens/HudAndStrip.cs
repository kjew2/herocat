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

    /// <summary>HUD bar (specs/07 section 11.1): gold, gems, free pulls, shards.</summary>
    public sealed class HudBar
    {
        public readonly VisualElement Root = Ui.Row();
        readonly Label _gold, _gems, _free, _shards;
        readonly GameViewModel _vm;

        public HudBar(GameViewModel vm)
        {
            _vm = vm;
            Root.style.backgroundColor = Ui.C(Palette.Panel);
            Ui.Pad(Root, 10);
            Root.style.justifyContent = Justify.SpaceBetween;
            _gold = Ui.Text("", Ui.Big, Palette.Gold);
            _gems = Ui.Text("", Ui.Big, Palette.Gems);
            _free = Ui.Text("", Ui.Body);
            _shards = Ui.Text("", Ui.Body, Palette.Muted);
            Root.Add(_gold); Root.Add(_gems); Root.Add(_free); Root.Add(_shards);
        }

        public void Refresh()
        {
            var h = _vm.Hud;
            Ui.Set(_gold, "Gold " + TextFmt.Num(h.Gold));
            Ui.Set(_gems, "Gems " + TextFmt.Int(h.Gems));
            Ui.Set(_free, "Free pulls " + h.FreePullsDaily + "+" + h.FreePullsBanked);
            Ui.Set(_shards, "Shards " + TextFmt.Int(h.Shards));
        }
    }

    /// <summary>
    /// Always-visible combat strip (specs/07 section 11.1): stage, kill progress, WALL badge, party + player + enemy
    /// rectangles, HP bar, boss timer, DPS line, and the Boss button while walled.
    /// </summary>
    public sealed class CombatStrip
    {
        public readonly VisualElement Root = Ui.Panel();
        readonly GameViewModel _vm;
        readonly Label _stage, _bossTag, _kills, _wall, _you, _enemy, _dps;
        readonly Label[] _party = new Label[6];
        readonly Ui.Bar _hp, _timer;
        readonly Button _boss;

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

            var arena = Ui.Row();
            arena.style.flexWrap = Wrap.NoWrap;
            arena.style.minHeight = 250;
            var party = Ui.Row();
            party.style.width = 260;
            for (int i = 0; i < _party.Length; i++) { _party[i] = Ui.Box("--", 72, 0x424242, 20); party.Add(_party[i]); }
            arena.Add(party);
            _you = Ui.Box("YOU", 150, Palette.ClassColor(0), Ui.Body);
            arena.Add(_you);
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            arena.Add(spacer);
            _enemy = Ui.Box("", 150, 0x888888, Ui.Body);
            arena.Add(_enemy);
            Root.Add(arena);

            _hp = new Ui.Bar(36, Palette.Bad);
            Root.Add(_hp.Root);
            _timer = new Ui.Bar(22, Palette.Gold);
            Root.Add(_timer.Root);
            _dps = Ui.Text("", Ui.Body);
            Root.Add(_dps);
            _boss = Ui.Btn("Boss", vm.AttemptBoss);
            Root.Add(_boss);
        }

        public void Refresh()
        {
            var c = _vm.Combat;
            Ui.Set(_stage, "Stage " + c.Stage);
            Ui.Show(_bossTag, c.IsBossStage);
            Ui.Set(_kills, "kills " + c.KillsInStage + "/" + c.KillsPerStage);
            _kills.style.color = Ui.C(c.IsWalled ? Palette.Bad : Palette.Text);
            Ui.Show(_wall, c.IsWalled);

            for (int i = 0; i < _party.Length; i++)
            {
                string g = c.PartyGlyphs[i];
                Ui.Set(_party[i], g ?? "--");
                Ui.Bg(_party[i], g == null ? 0x424242u : 0x90A4AEu);
            }
            Ui.Bg(_you, Palette.ClassColor(c.ClassIndex));

            Color enemyColor = Color.HSVToRGB(c.EnemyHue / 360f, 0.6f, 0.7f);
            Ui.Bg(_enemy, enemyColor);
            float size = c.EnemyIsBoss ? 220 : 150;
            if (_enemy.style.width.value.value != size) { _enemy.style.width = size; _enemy.style.height = size; }
            Ui.Border(_enemy, c.EnemyIsBoss ? 8 : 0, 0xFFA726);
            string enemyText = c.Spawning ? (c.BossPending ? "Boss incoming" : "...")
                             : c.EnemyIsBoss ? "BOSS " + c.Stage : "s" + c.Stage;
            Ui.Set(_enemy, enemyText);

            double hpFrac = c.EnemyMaxHp > 0 && !c.Spawning ? c.EnemyHp / c.EnemyMaxHp : 0;
            _hp.Set(hpFrac, c.Spawning ? "spawning" : TextFmt.Num(c.EnemyHp) + " / " + TextFmt.Num(c.EnemyMaxHp));
            Ui.Show(_timer.Root, c.EnemyIsBoss);
            if (c.EnemyIsBoss) _timer.Set(c.BossTimeMaxS > 0 ? c.BossTimerS / c.BossTimeMaxS : 0, "Boss time " + TextFmt.Seconds(c.BossTimerS));

            Ui.Set(_dps, "DPS " + TextFmt.Num(c.Dps) + "   hit " + TextFmt.Num(c.HitDamage) + "   " + c.HitsToKill + " hits / " +
                         TextFmt.Seconds(c.KillTimeS));

            Ui.Show(_boss, c.ShowBossButton);
            if (c.ShowBossButton)
            {
                Ui.Set(_boss, c.BossClearable ? "Boss: clearable - retry now" : "Boss: need ~" + TextFmt.Ratio(c.NeedDpsRatio) + "x DPS (retry)");
                Ui.Border(_boss, 3, c.BossClearable ? Palette.Ok : Ui.ButtonBorder);
            }
        }
    }
}
