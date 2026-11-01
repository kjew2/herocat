using System.Collections.Generic;
using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>The shared collection screen grammar (07 sections 4.5, 4.6): chips, summary, sort/filter, grid, selection.</summary>
    public abstract class CollectionScreenView : FullScreenView
    {
        protected abstract CollectionScreenVm CollectionVm { get; }
        RectTransform _grid;
        int _gridVersion = -1;
        readonly List<GameObject> _tiles = new List<GameObject>();
        protected float GridTop = 360;

        protected void BuildChips()
        {
            var c = CollectionVm;
            var w = Btn("ChipWeapons", 24, 8, 220, 112, () => c.OnChip(Banner.WEAPON), c.Banner == Banner.WEAPON ? Palette.Accent : Palette.PanelRaised, UiKit.Caption);
            UiKit.LabelOf(w).text = Vm.Localizer.Text(UiKeys.CollChipWeapons);
            var s = Btn("ChipSkills", 260, 8, 220, 112, () => c.OnChip(Banner.SKILL), c.Banner == Banner.SKILL ? Palette.Accent : Palette.PanelRaised, UiKit.Caption);
            UiKit.LabelOf(s).text = Vm.Localizer.Text(UiKeys.CollChipSkills);
            var cp = Btn("ChipCompanions", 496, 8, 260, 112, () => c.OnChip(Banner.COMPANION), Palette.PanelRaised, UiKit.Caption);
            UiKit.LabelOf(cp).text = Vm.Localizer.Text(UiKeys.CollChipCompanions);
            cp.GetComponent<CanvasGroup>().alpha = Palette.DisabledAlpha;   // MVP: shows its lock toast
        }

        protected void BuildSortFilterAndGrid(float y)
        {
            var c = CollectionVm;
            var sort = Btn("Sort", 24, y, 320, 112, c.CycleSort, Palette.PanelRaised, UiKit.Caption);
            c.SortLabel.Bind(UiKit.LabelOf(sort).Sink());
            var filter = Btn("Filter", 360, y, 320, 112, c.CycleFilter, Palette.PanelRaised, UiKit.Caption);
            c.FilterLabel.Bind(UiKit.LabelOf(filter).Sink());
            var news = Line("NewCount", y, UiKit.Caption, Palette.Accent, 700, 340, 112);
            c.NewCount.Bind(new ActionTextSink(t => news.text = c.NewCount.Value > 0 ? Vm.Localizer.Format(UiKeys.CollNew, t) : ""));
            GridTop = y + 128;
            _grid = UiKit.Rect("Grid", Content);
            UiKit.Place(_grid, 24, GridTop, 1032, 1200);
        }

        /// <summary>The selected panel; placed after the grid each rebuild.</summary>
        protected RectTransform Selected;

        protected void BuildSelectedPanel()
        {
            var c = CollectionVm;
            Selected = UiKit.Panel("Selected", Content, Palette.PanelRaised).rectTransform;
            var name = UiKit.Text("Name", Selected, UiKit.Body, Palette.Text);
            UiKit.Place(name.rectTransform, 24, 8, 640, 56);
            c.SelectedName.Bind(name.Sink());
            var lvl = UiKit.Text("Level", Selected, UiKit.Caption, Palette.Muted);
            UiKit.Place(lvl.rectTransform, 24, 64, 640, 48);
            c.SelectedLevel.Bind(lvl.Sink());
            var stats = UiKit.Text("Stats", Selected, UiKit.Caption, Palette.Text);
            UiKit.Place(stats.rectTransform, 24, 112, 640, 48);
            c.SelectedStats.Bind(stats.Sink());
            var equip = UiKit.Button("Equip", Selected, OnEquip, 0x2E7D32, UiKit.Body);
            var ert = equip.GetComponent<RectTransform>();
            UiKit.Place(ert, 680, 16, 220, 112);
            var reason = UiKit.Text("Reason", Selected, UiKit.Micro, Palette.Muted, TextAlignmentOptions.Center);
            UiKit.Place(reason.rectTransform, 680, 128, 220, 40);
            ButtonBinding.Bind(c.Equip, equip, reason);
            Anchor(EquipAnchor, ert);
            var info = UiKit.Button("Info", Selected, () => c.OnInfo(Vm.ItemDetail), Palette.Panel, UiKit.Body);
            UiKit.Place(info.GetComponent<RectTransform>(), 912, 16, 112, 112);
            UiKit.LabelOf(info).text = "i";
            c.HasSelection.Bind(new ActiveSink(Selected.gameObject));
            Selected.gameObject.SetActive(false);
        }

        protected abstract void OnEquip();
        protected abstract string EquipAnchor { get; }

        public override void Tick(double now)
        {
            var c = CollectionVm;
            if (c.GridVersion == _gridVersion) return;
            _gridVersion = c.GridVersion;
            foreach (var t in _tiles) Destroy(t);
            _tiles.Clear();
            const int perRow = 6;
            const float size = 160, gap = 12;
            for (int i = 0; i < c.Tiles.Count; i++)
            {
                var tile = c.Tiles[i];
                string id = tile.Id;
                var b = UiKit.Button("Tile" + i, _grid, () => { c.OnTile(id); _gridVersion = -1; }, Palette.PanelRaised, UiKit.Caption);
                var rt = b.GetComponent<RectTransform>();
                UiKit.Place(rt, i % perRow * (size + gap), i / perRow * (size + gap), size, size);
                b.GetComponent<Image>().color = UiKit.Rarity(tile.Rarity);
                b.GetComponent<CanvasGroup>().alpha = tile.Owned ? 1f : Palette.UnownedAlpha;
                var label = UiKit.LabelOf(b);
                label.color = UiKit.C(Palette.DarkTextOn(tile.Rarity) ? 0x121212u : 0xFFFFFFu);
                // Placeholder icon (07 section 15.1): rarity glyph + pool index; "?" when unowned.
                label.text = tile.Owned ? Palette.ShapeGlyph(tile.Rarity) + " " + tile.PoolCode + "\nLv" + tile.Level : Palette.ShapeGlyph(tile.Rarity) + " ?";
                if (tile.IsNew) UiKit.Place(UiKit.CirclePanel("New", rt, UiKit.C(Palette.Bad)).rectTransform, size - 32, 4, 28, 28);
                if (tile.IsEquipped) { var o = b.gameObject.AddComponent<Outline>(); o.effectColor = UiKit.C(Palette.Accent); o.effectDistance = new Vector2(5, 5); }
                if (c.SelectedId == id) b.transform.localScale = new Vector3(1.06f, 1.06f, 1);
                _tiles.Add(b.gameObject);
            }
            float gridH = (c.Tiles.Count + perRow - 1) / perRow * (size + gap);
            _grid.sizeDelta = new Vector2(1032, gridH);
            if (Selected != null) UiKit.Place(Selected, 16, GridTop + gridH + 16, 1048, 176);
            SetContentHeight(GridTop + gridH + 220);
        }

        public override void OnShown(ScreenEntry e) => _gridVersion = -1;
    }
}
