using HeroCat.Client.Core;
using UnityEngine.UIElements;

namespace HeroCat.Client.Screens
{
    /// <summary>Combat tab (specs/07 section 11.2): wall card, event feed, idle chest, lifetime line.</summary>
    public sealed class CombatScreen : ScreenBase
    {
        readonly VisualElement _wallCard;
        readonly Label _wallText, _feed, _lifetime;
        readonly Button _chest;
        int _feedVersion = -1;

        public CombatScreen(GameViewModel vm) : base(vm)
        {
            _wallCard = Ui.Panel();
            Ui.Border(_wallCard, 3, Palette.Bad);
            _wallText = Ui.Text("");
            _wallCard.Add(_wallText);
            _wallCard.Add(Ui.Btn("Retry boss", vm.AttemptBoss));
            Root.Add(_wallCard);

            var feedPanel = Ui.Panel();
            feedPanel.Add(Ui.Text("Recent", Ui.Body, Palette.Muted));
            _feed = Ui.Text("", Ui.Small);
            feedPanel.Add(_feed);
            Root.Add(feedPanel);

            _chest = Ui.Btn("Idle chest", vm.ClaimIdleChest);
            Root.Add(_chest);
            _lifetime = Ui.Text("", Ui.Small, Palette.Muted);
            Root.Add(_lifetime);
        }

        public override string Title => "Combat";

        public override void Refresh()
        {
            var c = Vm.Combat;
            Ui.Show(_wallCard, c.IsWalled);
            if (c.IsWalled)
                Ui.Set(_wallText, "BOSS FAILED (attempt " + c.BossFailsOnStage + ")\n" +
                                  "Your DPS " + TextFmt.Num(c.Dps) + "   Need " + TextFmt.Num(c.NeedDps) + "\n" +
                                  "Need ~" + TextFmt.Ratio(c.NeedDpsRatio) + "x DPS" + (c.BossClearable ? "  (clearable now!)" : "") + "\n" +
                                  "Farming stage " + c.Stage + " mobs meanwhile.");
            if (_feedVersion != Vm.Feed.Version) { _feedVersion = Vm.Feed.Version; Ui.Set(_feed, Vm.Feed.Text()); }
            Ui.Set(_chest, c.ChestReady ? "Claim idle chest (+" + c.ChestGems + " gems)"
                                        : "Idle chest ready in " + TextFmt.Duration(c.ChestReadyInSec));
            Ui.Enable(_chest, c.ChestReady);
            Ui.Set(_lifetime, "Lifetime: kills " + c.LifetimeKills + "  bosses " + c.LifetimeBossKills + "  fails " + c.LifetimeBossFails +
                              "  drops " + c.LifetimeDrops + (c.FirstWallStage > 0 ? "  first wall: stage " + c.FirstWallStage : ""));
        }
    }

    /// <summary>Stats tab (specs/07 section 11.3): x1 / x10 / Max ATK level buys.</summary>
    public sealed class StatsScreen : ScreenBase
    {
        readonly Label _header, _gold;
        readonly Button[] _buttons = new Button[3];
        readonly Label[] _info = new Label[3];

        public StatsScreen(GameViewModel vm) : base(vm)
        {
            var p = Ui.Panel();
            _header = Ui.Heading("");
            _gold = Ui.Text("", Ui.Body, Palette.Gold);
            p.Add(_header); p.Add(_gold);
            Root.Add(p);
            System.Action[] actions = { () => vm.BuyStat(1), () => vm.BuyStat(10), vm.BuyStatMax };
            for (int i = 0; i < 3; i++)
            {
                var row = Ui.Row();
                _buttons[i] = Ui.Btn("", actions[i]);
                _buttons[i].style.width = 220;
                _info[i] = Ui.Text("");
                _info[i].style.flexShrink = 1;
                row.Add(_buttons[i]); row.Add(_info[i]);
                Root.Add(row);
            }
            Root.Add(Ui.Text("Each level adds a flat % of base ATK; cost grows geometrically (content curve stat_cost).", Ui.Small, Palette.Muted));
        }

        public override string Title => "Stats";

        public override void Refresh()
        {
            var s = Vm.Stats;
            Ui.Set(_header, "ATK level " + s.Level + "    ATK " + TextFmt.Mult(s.StatMult));
            Ui.Set(_gold, "Gold " + TextFmt.Num(Vm.Hud.Gold));
            Fill(0, s.One, "x1");
            Fill(1, s.Ten, "x10");
            Fill(2, s.Max, "Max (" + s.Max.Count + ")");
        }

        void Fill(int i, StatRow r, string label)
        {
            Ui.Set(_buttons[i], label);
            Ui.Enable(_buttons[i], r.Affordable);
            Ui.Set(_info[i], r.Count <= 0 ? "cost " + TextFmt.Num(r.Cost) + " for the next level"
                : "cost " + TextFmt.Num(r.Cost) + "   -> " + TextFmt.Mult(r.ResultMult) + "   " + TextFmt.PctSigned(r.DeltaPct) + " DPS");
        }
    }

    /// <summary>Class tab (specs/07 section 11.7): automatic advances at content stages, read-only.</summary>
    public sealed class ClassScreen : ScreenBase
    {
        readonly Label _header, _adv, _next, _milestones;
        readonly Ui.Bar _bar;

        public ClassScreen(GameViewModel vm) : base(vm)
        {
            var p = Ui.Panel();
            _header = Ui.Heading("");
            _adv = Ui.Text("");
            _next = Ui.Text("");
            _bar = new Ui.Bar(30, Palette.Ok);
            p.Add(_header); p.Add(_adv); p.Add(_next); p.Add(_bar.Root);
            Root.Add(p);
            _milestones = Ui.Text("");
            Root.Add(_milestones);
            Root.Add(Ui.Text("Class advances are automatic on reaching the stage. Each multiplies ATK.", Ui.Small, Palette.Muted));
        }

        public override string Title => "Class";

        public override void Refresh()
        {
            var c = Vm.Class;
            Ui.Set(_header, "Class: " + c.ClassName + "    ATK " + TextFmt.Mult(c.ClassMult));
            Ui.Set(_adv, "Advances " + c.Advances + " / " + c.MaxAdvances);
            Ui.Set(_next, c.NextStage > 0 ? "Next: stage " + c.NextStage : "All advances reached");
            _bar.Set(c.Progress01, c.NextStage > 0 ? Vm.Combat.Stage + " / " + c.NextStage : "max");
            var sb = new System.Text.StringBuilder();
            foreach (var m in c.Milestones)
                sb.Append(m.Reached ? "[x] " : "[ ] ").Append("Stage ").Append(m.Stage).Append("   ").Append(m.Name)
                  .Append("   ").Append(TextFmt.Mult(m.Mult)).Append('\n');
            Ui.Set(_milestones, sb.ToString().TrimEnd('\n'));
        }
    }
}
