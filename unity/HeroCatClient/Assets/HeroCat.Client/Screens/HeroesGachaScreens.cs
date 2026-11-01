using HeroCat.Client.Core;
using UnityEngine.UIElements;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// Heroes tab (specs/07 section 11.5): party of 6 (tap a slot to place the selected hero there, or to unequip it
    /// when nothing is selected), 54-hero inventory, auto-equip.
    /// </summary>
    public sealed class HeroesScreen : ScreenBase
    {
        readonly Label _header, _detail;
        readonly Button _auto;
        readonly Button[] _party;
        readonly VisualElement _grid;
        Button[] _inv = new Button[0];
        int _version = -1;

        public HeroesScreen(GameViewModel vm) : base(vm)
        {
            var top = Ui.Row();
            _header = Ui.Heading("");
            _auto = Ui.Btn("Auto-equip", vm.AutoEquip);
            top.Add(_header); top.Add(_auto);
            Root.Add(top);

            Root.Add(Ui.Text("Party (tap: place selected hero / unequip)", Ui.Small, Palette.Muted));
            var partyRow = Ui.Row();
            _party = new Button[6];
            for (int i = 0; i < _party.Length; i++)
            {
                int slot = i;
                _party[i] = Ui.Btn("", () => vm.TapPartySlot(slot), Ui.Small);
                _party[i].style.width = 150;
                _party[i].style.height = 120;
                partyRow.Add(_party[i]);
            }
            Root.Add(partyRow);

            var p = Ui.Panel();
            _detail = Ui.Text("");
            p.Add(_detail);
            var actions = Ui.Row();
            actions.Add(Ui.Btn("Equip to empty slot", vm.EquipSelectedToEmptySlot, Ui.Small));
            actions.Add(Ui.Btn("Unequip", vm.UnequipSelected, Ui.Small));
            p.Add(actions);
            Root.Add(p);

            Root.Add(Ui.Text("Inventory", Ui.Body, Palette.Muted));
            _grid = Ui.Row();
            Root.Add(_grid);
        }

        public override string Title => "Heroes";

        public override void Refresh()
        {
            var h = Vm.Heroes;
            Ui.Set(_header, "Heroes " + TextFmt.Mult(h.HeroMult) + "  ");
            Ui.Set(_auto, h.AutoEquipOn ? "Auto-equip (on)" : "Auto-equip");
            if (_version == h.Version) return;
            _version = h.Version;

            if (_inv.Length != h.All.Length)
            {
                _grid.Clear();
                _inv = new Button[h.All.Length];
                for (int i = 0; i < _inv.Length; i++)
                {
                    string id = h.All[i].Id;
                    _inv[i] = Ui.Btn("", () => Vm.SelectHero(id), 20);
                    _inv[i].style.width = 112;
                    _inv[i].style.height = 100;
                    Ui.Pad(_inv[i], 4);
                    _grid.Add(_inv[i]);
                }
            }
            for (int i = 0; i < _inv.Length; i++)
            {
                var r = h.All[i];
                var b = _inv[i];
                Ui.Set(b, r.Owned ? r.Glyph + "\nLv" + r.Level : "?\n" + r.Glyph);
                Ui.Bg(b, Palette.RarityColor(r.Tier));
                b.style.color = Ui.C(Palette.DarkTextOn(r.Tier) ? 0x000000u : 0xFFFFFFu);
                b.style.opacity = r.Owned ? 1f : 0.3f;
                bool sel = r.Id == h.SelectedHero;
                Ui.Border(b, sel ? 6 : r.EquippedSlot >= 0 ? 4 : 1, sel ? 0xFFFFFFu : r.EquippedSlot >= 0 ? Palette.Ok : Ui.ButtonBorder);
            }
            for (int s = 0; s < _party.Length; s++)
            {
                var r = s < h.Equipped.Length ? h.Equipped[s] : null;
                Ui.Set(_party[s], r == null ? "( -- )\nempty" : r.Glyph + "\nLv" + r.Level + "  " + TextFmt.PctPoints(r.Pct));
                Ui.Bg(_party[s], r == null ? Ui.ButtonBg : Palette.RarityColor(r.Tier));
                _party[s].style.color = Ui.C(r == null || !Palette.DarkTextOn(r.Tier) ? 0xFFFFFFu : 0x000000u);
            }

            HeroRow selRow = null;
            foreach (var r in h.All) if (r.Id == h.SelectedHero) { selRow = r; break; }
            if (selRow == null) Ui.Set(_detail, "Tap an owned hero to select it.");
            else
            {
                string copies = selRow.NextLevelCopies < 0 ? "Lv" + selRow.Level + " MAX (" + selRow.Copies + " copies)"
                              : "Lv" + selRow.Level + " (" + selRow.Copies + "/" + selRow.NextLevelCopies + " copies)";
                Ui.Set(_detail, "Selected: " + selRow.Glyph + "  " + selRow.Name + "  " + TextFmt.Tier(selRow.Tier) + "\n" +
                                (selRow.Owned ? copies + "   " + TextFmt.PctPoints(selRow.Pct) + " DPS" : "not owned") +
                                (selRow.EquippedSlot >= 0 ? "   equipped slot " + (selRow.EquippedSlot + 1) : ""));
            }
        }
    }

    /// <summary>Gacha tab (specs/07 section 11.6): pity, pull x1 / x10, last results. No flip animation in the POC.</summary>
    public sealed class GachaScreen : ScreenBase
    {
        readonly Label _banner, _pity, _totals, _summary, _rates;
        readonly Ui.Bar _pityBar;
        readonly Button _pull1, _pull10;
        readonly VisualElement _cards;
        int _resultsVersion = -1;

        public GachaScreen(GameViewModel vm) : base(vm)
        {
            var p = Ui.Panel();
            _banner = Ui.Heading("");
            _pity = Ui.Text("");
            _pityBar = new Ui.Bar(26, Palette.RarityColor(HeroCat.Sim.Content.Rarity.L));
            _totals = Ui.Text("", Ui.Small, Palette.Muted);
            p.Add(_banner); p.Add(_pity); p.Add(_pityBar.Root); p.Add(_totals);
            Root.Add(p);
            _pull1 = Ui.Btn("", vm.Pull, Ui.Big);
            _pull10 = Ui.Btn("", vm.Pull10, Ui.Big);
            Root.Add(_pull1);
            Root.Add(_pull10);
            _summary = Ui.Text("");
            Root.Add(_summary);
            _cards = Ui.Row();
            Root.Add(_cards);
            _rates = Ui.Text(vm.Gacha.RatesText, Ui.Small, Palette.Muted);
            Root.Add(_rates);
        }

        public override string Title => "Gacha";

        public override void Refresh()
        {
            var g = Vm.Gacha;
            Ui.Set(_banner, g.BannerName);
            Ui.Set(_pity, "Pity " + g.PityCounter + " / " + g.PityHard + "    soft pity from pull " + (g.PitySoftStart + 1) +
                          "    next L+ chance " + TextFmt.PctSigned(g.NextPullLplus).TrimStart('+'));
            _pityBar.Set(g.PityHard > 0 ? g.PityCounter / (double)g.PityHard : 0, g.PityCounter + "/" + g.PityHard);
            Ui.Set(_totals, "Total pulls " + g.TotalPulls + "    free today " + g.FreePullsDaily + "/" + g.FreePullsPerDay +
                            "    banked " + g.FreePullsBanked + "    gems " + g.Gems);
            Ui.Set(_pull1, g.Pull1Label);
            Ui.Enable(_pull1, g.CanPull);
            Ui.Set(_pull10, g.Pull10Label);
            Ui.Enable(_pull10, g.CanPull10);

            if (_resultsVersion == g.ResultsVersion) return;
            _resultsVersion = g.ResultsVersion;
            Ui.Set(_summary, g.LastResults.Length == 0 ? "No pulls yet." : "Last: " + g.LastSummary);
            _cards.Clear();
            foreach (var card in g.LastResults)
            {
                string caption = card.Glyph + "\n" + card.Name + "\n" +
                                 (card.IsNew ? "NEW" : "Lv " + card.LevelBefore + "->" + card.LevelAfter) +
                                 (card.Shards > 0 ? "\n+" + card.Shards + " shards" : "");
                var box = Ui.Box(caption, 190, Palette.RarityColor(card.Tier), 22);
                box.style.color = Ui.C(Palette.DarkTextOn(card.Tier) ? 0x000000u : 0xFFFFFFu);
                _cards.Add(box);
            }
        }
    }
}
