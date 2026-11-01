using HeroCat.Client.Core;
using UnityEngine.UIElements;

namespace HeroCat.Client.Screens
{
    /// <summary>Gear tab (specs/07 section 11.4): 6 slot tiles, selected-slot detail, Enhance.</summary>
    public sealed class GearScreen : ScreenBase
    {
        readonly Label _header, _detail;
        readonly Button[] _tiles;
        readonly Button _enhance, _weakest;

        public GearScreen(GameViewModel vm) : base(vm)
        {
            _header = Ui.Heading("");
            Root.Add(_header);
            var grid = Ui.Row();
            _tiles = new Button[vm.Gear.Slots.Length];
            for (int i = 0; i < _tiles.Length; i++)
            {
                int slot = i;
                _tiles[i] = Ui.Btn("", () => vm.SelectGearSlot(slot), Ui.Small);
                _tiles[i].style.width = 300;
                _tiles[i].style.height = 170;
                grid.Add(_tiles[i]);
            }
            Root.Add(grid);
            var p = Ui.Panel();
            _detail = Ui.Text("");
            p.Add(_detail);
            _enhance = Ui.Btn("", vm.EnhanceSelected);
            p.Add(_enhance);
            _weakest = Ui.Btn("Enhance weakest slot", vm.EnhanceWeakest, Ui.Small);
            p.Add(_weakest);
            Root.Add(p);
            Root.Add(Ui.Text("Gear drops from kills: auto-equipped if its base % beats the current piece, otherwise sold.", Ui.Small, Palette.Muted));
        }

        public override string Title => "Gear";

        public override void Refresh()
        {
            var g = Vm.Gear;
            Ui.Set(_header, "Gear " + TextFmt.Mult(g.GearMult) + "   (" + g.Slots.Length + " slots)");
            for (int i = 0; i < _tiles.Length; i++)
            {
                var s = g.Slots[i];
                string text = s.IsEmpty ? s.Name + "\nempty"
                    : s.Name + "\n[" + s.Rarity + "] T" + s.Tier + "\n+" + s.Enhance + (s.Maxed ? " MAX" : "");
                Ui.Set(_tiles[i], text);
                Ui.Bg(_tiles[i], s.IsEmpty ? Ui.ButtonBg : Palette.RarityColor(s.Rarity));
                _tiles[i].style.color = Ui.C(s.IsEmpty || !Palette.DarkTextOn(s.Rarity) ? 0xFFFFFFu : 0x000000u);
                Ui.Border(_tiles[i], i == g.SelectedSlot ? 6 : 2, i == g.SelectedSlot ? 0xFFFFFFu : Ui.ButtonBorder);
            }
            var sel = g.Slots[g.SelectedSlot];
            Ui.Set(_detail, sel.IsEmpty ? "Selected: " + sel.Name + " (empty)"
                : "Selected: " + sel.Name + "  [" + sel.Rarity + "] T" + sel.Tier + " +" + sel.Enhance + "\n" +
                  "base " + TextFmt.PctSigned(sel.BasePct) + "   effective " + TextFmt.PctSigned(sel.EffPct) + "   slot " + TextFmt.Mult(sel.SlotMult) +
                  (sel.Tier == 0 ? "\nT0: enhancing adds nothing yet" : ""));
            string label;
            if (g.SelectedEmpty) label = "Empty slot";
            else if (g.EnhanceMaxed) label = "Max level";
            else if (!g.EnhanceAffordable) label = "Need " + TextFmt.Num(g.EnhanceCost) + " gold";
            else label = "Enhance +" + sel.Enhance + " -> +" + (sel.Enhance + 1) + "   " + TextFmt.Chance(g.EnhanceChance) +
                         "   cost " + TextFmt.Num(g.EnhanceCost);
            Ui.Set(_enhance, label);
            Ui.Enable(_enhance, !g.SelectedEmpty && !g.EnhanceMaxed && g.EnhanceAffordable);
        }
    }
}
