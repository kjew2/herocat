using System;
using System.Collections.Generic;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Power;
using HeroCat.Sim.State;
using HeroCat.Sim.Summons;

namespace HeroCat.Client.Core
{
    /// <summary>One collection grid tile (07 sections 4.5 and 4.6): unowned tiles draw at 30% opacity with "?".</summary>
    public sealed class TileVm
    {
        public string Id;
        public Banner Banner;
        public Rarity Rarity;
        public string NameKey, IconId;
        public bool Owned, IsNew, IsEquipped;
        public int Level, Copies, CopiesToNext, Slot = -1;

        /// <summary>The copies needed for the next level in total (<c>DUPE_LVL_CUM</c>), for "4/6".</summary>
        public int CopiesForNext;

        public bool AtMaxLevel;

        /// <summary>Weapons: equipped ATK % now / at the next level, and the passive %. Skills: dps % now / next.</summary>
        public double ValueNow, ValueNext, PassiveNow, PassiveNext;

        /// <summary>Skills only.</summary>
        public CastType CastType;
        public int CooldownTicks;

        public long FirstAcquired;

        /// <summary>Placeholder icon text (07 section 15.1): <c>W07</c>, <c>S12</c>.</summary>
        public string PoolCode;
    }

    /// <summary>
    /// The shared grammar of the collection screens (07 sections 4.5-4.7): grid, sort and filter (persisted in
    /// ClientPrefs), the selected panel and NEW badges (a ClientPrefs update, never a sim command; issues #7, #47). The
    /// tile list is rebuilt only when an event or a pref change marks it dirty; <see cref="GridVersion"/> tells the view.
    /// </summary>
    public abstract class CollectionScreenVm
    {
        protected readonly VmContext C;
        protected readonly List<TileVm> All = new List<TileVm>();
        readonly List<TileVm> _visible = new List<TileVm>();
        bool _dirty = true;
        int _prefsVersion = -1;

        protected CollectionScreenVm(VmContext c, Banner banner)
        {
            C = c;
            Banner = banner;
            SelectedName = c.Label();
            SelectedLevel = c.Label();
            SelectedStats = c.Label();
            SortLabel = c.Label();
            FilterLabel = c.Label();
            Equip = c.Button();
            Equip.Label.Set(UiKeys.CollEquip);
        }

        public Banner Banner { get; }

        public IReadOnlyList<TileVm> Tiles => _visible;
        public int GridVersion { get; private set; }

        public readonly IntLabel OwnedCount = new IntLabel();
        public readonly IntLabel PoolSize = new IntLabel();
        public readonly IntLabel NewCount = new IntLabel();
        public readonly TextKeyLabel SortLabel, FilterLabel;

        public string SelectedId { get; private set; }
        public TileVm Selected { get; private set; }
        public readonly Flag HasSelection = new Flag();
        public readonly TextKeyLabel SelectedName, SelectedLevel, SelectedStats;
        public readonly ButtonState Equip;

        public CollectionSort Sort => C.Prefs.Sort;
        public CollectionFilter Filter => C.Prefs.Filter;

        /// <summary>Marks the grid for a rebuild on the next Refresh (an ItemAcquired, an equip, a pref).</summary>
        public void Invalidate() => _dirty = true;

        public void Refresh(Simulation sim)
        {
            if (_prefsVersion != C.Prefs.Version) { _prefsVersion = C.Prefs.Version; _dirty = true; }
            if (_dirty)
            {
                _dirty = false;
                Rebuild(sim);
            }
            RefreshExtra(sim);
            RefreshSelected();
        }

        void Rebuild(Simulation sim)
        {
            var db = C.Db;
            var view = SummonQuery.Collection(db, sim.State, Banner, id => C.Prefs.IsNew(Banner, id));
            var col = CollectionRules.For(sim.State, Banner);
            All.Clear();
            int index = 0;
            foreach (var it in view.Items)
            {
                index++;
                col.Items.TryGetValue(it.Id, out var owned);
                var t = new TileVm
                {
                    Id = it.Id, Banner = Banner, Rarity = it.Rarity, NameKey = it.NameKey, IconId = it.Icon, Owned = it.Owned, IsNew = it.IsNew,
                    IsEquipped = it.Equipped, Slot = it.Slot ?? -1, Level = it.Level, Copies = it.Copies, CopiesToNext = it.CopiesToNextLevel,
                    AtMaxLevel = it.Level >= db.DupeLevels.Count, FirstAcquired = owned?.FirstAcquiredServerTime ?? 0,
                    PoolCode = (Banner == Banner.WEAPON ? "W" : Banner == Banner.SKILL ? "S" : "C") + index.ToString("00"),
                };
                t.CopiesForNext = t.AtMaxLevel ? 0 : db.DupeLevels[Math.Max(0, it.Level)];
                double multNow = owned != null ? PowerFormula.ItemLevelMult(db, owned) : 0.0;
                double multNext = t.AtMaxLevel ? multNow : db.LevelMult(it.EffectiveLevel + 1 < 1 ? 1 : it.EffectiveLevel + 1);
                FillValues(t, multNow, multNext);
                All.Add(t);
            }
            OwnedCount.Set(view.OwnedCount);
            PoolSize.Set(view.PoolCount);
            NewCount.Set(view.NewCount);
            SortLabel.Set(UiKeys.SortKey(C.Prefs.Sort));
            FilterLabel.Set(UiKeys.FilterKey(C.Prefs.Filter));
            ApplySortFilter();
            if (SelectedId != null) Selected = Find(SelectedId);
            GridVersion++;
        }

        protected abstract void FillValues(TileVm t, double multNow, double multNext);

        protected virtual void RefreshExtra(Simulation sim) { }

        protected abstract void RefreshSelectedStats(TileVm t);

        void ApplySortFilter()
        {
            _visible.Clear();
            var f = C.Prefs.Filter;
            foreach (var t in All)
            {
                if (f == CollectionFilter.Owned && !t.Owned) continue;
                if (f >= CollectionFilter.Common && t.Rarity != (Rarity)(f - CollectionFilter.Common)) continue;
                _visible.Add(t);
            }
            var sort = C.Prefs.Sort;
            _visible.Sort((a, b) =>
            {
                int r;
                switch (sort)
                {
                    case CollectionSort.Level: r = b.Level.CompareTo(a.Level); break;
                    case CollectionSort.Newest: r = b.FirstAcquired.CompareTo(a.FirstAcquired); break;
                    case CollectionSort.OwnedFirst: r = b.Owned.CompareTo(a.Owned); break;
                    default: r = 0; break;
                }
                if (r == 0) r = b.Rarity.CompareTo(a.Rarity);
                if (r == 0) r = string.CompareOrdinal(a.Id, b.Id);
                return r;
            });
        }

        protected TileVm Find(string id)
        {
            foreach (var t in All) if (t.Id == id) return t;
            return null;
        }

        void RefreshSelected()
        {
            var t = Selected;
            HasSelection.Set(t != null);
            if (t == null)
            {
                Equip.Visible.Set(false);
                return;
            }
            SelectedName.Set(t.NameKey);
            if (!t.Owned) SelectedLevel.Set(UiKeys.CollNotOwned);
            else if (t.AtMaxLevel) SelectedLevel.Set(UiKeys.CollLevelMax, t.Level);
            else SelectedLevel.Set(UiKeys.CollLevelCopies, t.Level, t.Copies, t.CopiesForNext);
            RefreshSelectedStats(t);
            Equip.Visible.Set(true);
            bool can = t.Owned && !t.IsEquipped;
            Equip.Enabled.Set(can);
            Equip.Reason.Set(can ? null : t.Owned ? UiKeys.CollEquipped : UiKeys.CollNotOwned);
        }

        /// <summary>Tile tap: select it and clear its NEW badge (07 section 4.5 states).</summary>
        public virtual void OnTile(string id)
        {
            SelectedId = id;
            Selected = Find(id);
            if (Selected != null && Selected.IsNew)
            {
                C.Prefs.ClearNew(Banner, id);   // a ClientPrefs update, not a sim command
                Selected.IsNew = false;
            }
            RefreshSelected();
        }

        public void ClearSelection()
        {
            SelectedId = null;
            Selected = null;
            RefreshSelected();
        }

        /// <summary>Leaving the screen clears every NEW badge shown on it.</summary>
        public void OnLeave()
        {
            foreach (var t in All)
                if (t.IsNew) C.Prefs.ClearNew(Banner, t.Id);
        }

        public void CycleSort() => C.Prefs.Sort = (CollectionSort)(((int)C.Prefs.Sort + 1) % 4);

        public void CycleFilter() => C.Prefs.Filter = (CollectionFilter)(((int)C.Prefs.Filter + 1) % 7);

        /// <summary>The header chips: switch to a sibling collection screen at the same stack depth.</summary>
        public void OnChip(Banner b)
        {
            if (b == Banner.WEAPON) C.Router?.Replace(ScreenIds.Weapons);
            else if (b == Banner.SKILL) C.Router?.Replace(ScreenIds.Skills);
            else C.LockedToast("COMPANIONS");
        }

        public void OnInfo(ItemDetailVm detail)
        {
            if (SelectedId != null) detail?.Open(Banner, SelectedId);
        }

        public virtual void OnEvent(Event e)
        {
            switch (e)
            {
                case ItemAcquired a when a.Banner == Banner:
                case ItemEquipped q when q.Banner == Banner:
                    Invalidate();
                    break;
            }
        }
    }

    /// <summary>SCR_WEAPONS (07 section 4.5; issue #116): equipped summary, collection passive total, grid, Equip.</summary>
    public sealed class WeaponsVm : CollectionScreenVm
    {
        public const string AnchorEquip = "weapons.equip";

        public WeaponsVm(VmContext c) : base(c, Banner.WEAPON)
        {
            EquippedName = c.Label();
        }

        public readonly TextKeyLabel EquippedName;
        public readonly IntLabel EquippedLevel = new IntLabel();
        public readonly NumberLabel EquippedAtkPct = new NumberLabel(NumberStyle.PctPoints);
        public readonly Bindable<Rarity> EquippedRarity = new Bindable<Rarity>();

        /// <summary>The 00 decision 5 passive total over owned weapons (04 WEAPON_PASSIVE_PCT).</summary>
        public readonly NumberLabel PassiveTotalPct = new NumberLabel(NumberStyle.PctPoints);

        protected override void FillValues(TileVm t, double multNow, double multNext)
        {
            if (!C.Db.Weapons.TryGetValue(t.Id, out var w)) return;
            t.ValueNow = w.AtkPct * multNow;
            t.ValueNext = w.AtkPct * multNext;
            t.PassiveNow = w.PassivePct * multNow;
            t.PassiveNext = w.PassivePct * multNext;
        }

        protected override void RefreshExtra(Simulation sim)
        {
            var p = sim.Query.Power;
            PassiveTotalPct.Set(p.WeaponPassivePct);
            EquippedAtkPct.Set(p.EquippedWeaponPct);
            string id = sim.State.Loadout.Weapon;
            var t = id != null ? Find(id) : null;
            if (t != null)
            {
                EquippedName.Set(t.NameKey);
                EquippedLevel.Set(t.Level);
                EquippedRarity.Set(t.Rarity);
            }
            else EquippedName.Set(UiKeys.CollNothingEquipped);
        }

        protected override void RefreshSelectedStats(TileVm t) =>
            SelectedStats.Set(UiKeys.WeaponStats, LabelArg.Num(t.ValueNow, NumberStyle.PctPoints), LabelArg.Num(t.PassiveNow, NumberStyle.PctPoints));

        /// <summary><c>EquipWeapon(id)</c>; disabled with a reason when equipped or unowned.</summary>
        public void OnEquip()
        {
            if (Selected == null) return;
            C.Commands?.Send(new EquipWeapon(Selected.Id), AnchorEquip);
        }
    }

    /// <summary>
    /// SCR_SKILLS (07 section 4.6; issue #116): six auto-cast slots, Auto-equip, the 40-tile grid with a cast-type glyph.
    /// A slot tap with a selection sends <c>EquipSkill(slot, id)</c>; without one, <c>UnequipSkill(slot)</c>.
    /// </summary>
    public sealed class SkillsVm : CollectionScreenVm
    {
        public const string AnchorAuto = "skills.auto";

        public SkillsVm(VmContext c) : base(c, Banner.SKILL)
        {
            Slots = new Bindable<SlotLook>[c.K.SkillSlots];
            for (int i = 0; i < Slots.Length; i++) Slots[i] = new Bindable<SlotLook>();
            AutoEquip = c.Button();
            AutoEquip.Label.Set(UiKeys.SkillsAutoEquip);
        }

        public readonly Bindable<SlotLook>[] Slots;
        public readonly ButtonState AutoEquip;

        /// <summary>Red dot on Auto-equip when it would change the loadout (04 ordering rule).</summary>
        public readonly Flag AutoEquipWouldChange = new Flag();

        public static string SlotAnchor(int slot) => "skills.slot." + slot;

        protected override void FillValues(TileVm t, double multNow, double multNext)
        {
            if (!C.Db.Skills.TryGetValue(t.Id, out var s)) return;
            t.ValueNow = s.DpsPct * multNow;
            t.ValueNext = s.DpsPct * multNext;
            t.CastType = s.CastType;
            t.CooldownTicks = s.CooldownTicks;
        }

        protected override void RefreshExtra(Simulation sim)
        {
            var st = sim.State;
            var slots = st.Loadout.Skills;
            for (int i = 0; i < Slots.Length; i++)
            {
                string id = slots != null && i < slots.Length ? slots[i] : null;
                if (id != null && C.Db.Skills.TryGetValue(id, out var d))
                {
                    st.Skills.Items.TryGetValue(id, out var owned);
                    Slots[i].Set(new SlotLook(id, d.Icon, d.Rarity, owned != null ? C.Db.DupeLevel(owned.Copies) : 0, 0));
                }
                else Slots[i].Set(default);
            }
            bool better = CollectionRules.BetterSkillAvailable(C.Db, st);
            AutoEquipWouldChange.Set(better);
            AutoEquip.Visible.Set(true);
            AutoEquip.Enabled.Set(true);
        }

        protected override void RefreshSelectedStats(TileVm t) =>
            SelectedStats.Set(UiKeys.SkillStats, LabelArg.Num(t.ValueNow, NumberStyle.PctPoints),
                              LabelArg.Num(t.CooldownTicks / (double)C.K.TickHz, NumberStyle.Seconds), UiKeys.CastTypeKey(t.CastType));

        public void OnSlot(int slot)
        {
            if (slot < 0 || slot >= Slots.Length) return;
            if (Selected != null && Selected.Owned)
            {
                C.Commands?.Send(new EquipSkill(slot, Selected.Id), SlotAnchor(slot));
                return;
            }
            if (!Slots[slot].Value.IsEmpty) C.Commands?.Send(new UnequipSkill(slot), SlotAnchor(slot));
        }

        /// <summary>The selected panel's "Equip to slot" with an explicit slot from the picker.</summary>
        public void OnEquipTo(int slot)
        {
            if (Selected == null) return;
            C.Commands?.Send(new EquipSkill(slot, Selected.Id), SlotAnchor(slot));
        }

        public void OnAutoEquip() => C.Commands?.Send(new AutoEquipBestSkills(), AnchorAuto);

        /// <summary>The first empty slot, else slot 0: the default target of the selected panel's Equip.</summary>
        public int DefaultSlot()
        {
            for (int i = 0; i < Slots.Length; i++) if (Slots[i].Value.IsEmpty) return i;
            return 0;
        }

        public void OnEquip()
        {
            if (Selected == null) return;
            OnEquipTo(DefaultSlot());
        }
    }

    /// <summary>
    /// SCR_ITEM_DETAIL (07 section 4.19; issue #116): any collectible's card: level and copies to next (DUPE_LVL_CUM),
    /// kind-specific stats with the next-level preview, source banner and base rate, Equip with a slot picker for skills,
    /// weapon Compare, the NEW ribbon and the "Not owned" state.
    /// </summary>
    public sealed class ItemDetailVm
    {
        public const string AnchorEquip = "detail.equip";

        readonly VmContext _c;
        Simulation _sim;

        public ItemDetailVm(VmContext c)
        {
            _c = c;
            Name = c.Label();
            KindLabel = c.Label();
            LevelLine = c.Label();
            StatLine = c.Label();
            NextLine = c.Label();
            PassiveLine = c.Label();
            SourceLine = c.Label();
            CompareLine = c.Label();
            Equip = c.Button();
            Equip.Label.Set(UiKeys.CollEquip);
        }

        public Banner Banner { get; private set; }
        public string ItemId { get; private set; }
        public TileVm Item { get; private set; }

        public readonly TextKeyLabel Name, KindLabel, LevelLine, StatLine, NextLine, PassiveLine, SourceLine, CompareLine;
        public readonly Bindable<Rarity> Rarity = new Bindable<Rarity>();
        public readonly Flag NewRibbon = new Flag();
        public readonly Flag Owned = new Flag();
        public readonly Flag CompareVisible = new Flag();
        public readonly ButtonState Equip;

        /// <summary>Skills: the slot picker row (07 section 4.19).</summary>
        public readonly Flag SlotPickerVisible = new Flag();

        public bool Compare { get; private set; }

        /// <summary>Set by Open and Compare; the GameViewModel refreshes the open modal while it is set.</summary>
        public bool Dirty { get; internal set; }

        /// <summary>Opens the modal for <paramref name="id"/> (pushes SCR_ITEM_DETAIL; the router queues or shifts it).</summary>
        public void Open(Banner banner, string id)
        {
            Banner = banner;
            ItemId = id;
            Compare = false;
            Item = null;
            Dirty = true;
            _c.Router?.ShowModal(ScreenIds.ItemDetail, id);
        }

        public void Close() => _c.Router?.CloseModal(ScreenIds.ItemDetail);

        public void ToggleCompare()
        {
            Compare = !Compare && Banner == Banner.WEAPON;
            Dirty = true;
        }

        public void Refresh(Simulation sim)
        {
            _sim = sim;
            Dirty = false;
            if (ItemId == null) return;
            var db = _c.Db;
            var view = SummonQuery.Collection(db, sim.State, Banner, i => _c.Prefs.IsNew(Banner, i));
            CollectibleVm it = null;
            int index = 0;
            foreach (var x in view.Items)
            {
                index++;
                if (x.Id == ItemId) { it = x; break; }
            }
            if (it == null) return;
            var col = CollectionRules.For(sim.State, Banner);
            col.Items.TryGetValue(ItemId, out var owned);
            int maxLevel = db.DupeLevels.Count;
            double multNow = owned != null ? PowerFormula.ItemLevelMult(db, owned) : 0.0;
            double multNext = db.LevelMult(Math.Min(maxLevel, it.EffectiveLevel + 1) < 1 ? 1 : it.EffectiveLevel + 1);
            Item = new TileVm
            {
                Id = it.Id, Banner = Banner, Rarity = it.Rarity, NameKey = it.NameKey, IconId = it.Icon, Owned = it.Owned, IsNew = it.IsNew,
                IsEquipped = it.Equipped, Level = it.Level, Copies = it.Copies, AtMaxLevel = it.Level >= maxLevel,
            };
            Item.CopiesForNext = Item.AtMaxLevel ? 0 : db.DupeLevels[Math.Max(0, it.Level)];

            Name.Set(it.NameKey);
            Rarity.Set(it.Rarity);
            KindLabel.Set(UiKeys.KindKey(Banner));
            NewRibbon.Set(it.IsNew);
            Owned.Set(it.Owned);
            if (!it.Owned) LevelLine.Set(UiKeys.CollNotOwned);
            else if (Item.AtMaxLevel) LevelLine.Set(UiKeys.CollLevelMax, it.Level);
            else LevelLine.Set(UiKeys.DetailLevel, it.Level, it.Copies, Item.CopiesForNext);

            if (Banner == Banner.WEAPON && db.Weapons.TryGetValue(ItemId, out var w))
            {
                StatLine.Set(UiKeys.DetailWeaponAtk, LabelArg.Num(w.AtkPct * multNow, NumberStyle.PctPoints));
                NextLine.Set(Item.AtMaxLevel ? UiKeys.DetailAtMax : UiKeys.DetailNext, LabelArg.Num(w.AtkPct * multNext, NumberStyle.PctPoints));
                PassiveLine.Set(UiKeys.DetailPassive, LabelArg.Num(w.PassivePct * multNow, NumberStyle.PctPoints));
                var p = sim.Query.Power;
                CompareVisible.Set(Compare);
                CompareLine.Set(UiKeys.DetailCompare, LabelArg.Num(p.EquippedWeaponPct, NumberStyle.PctPoints));
            }
            else if (Banner == Banner.SKILL && db.Skills.TryGetValue(ItemId, out var s))
            {
                StatLine.Set(UiKeys.SkillStats, LabelArg.Num(s.DpsPct * multNow, NumberStyle.PctPoints),
                             LabelArg.Num(s.CooldownTicks / (double)_c.K.TickHz, NumberStyle.Seconds), UiKeys.CastTypeKey(s.CastType));
                NextLine.Set(Item.AtMaxLevel ? UiKeys.DetailAtMax : UiKeys.DetailNext, LabelArg.Num(s.DpsPct * multNext, NumberStyle.PctPoints));
                PassiveLine.Set(null);
                CompareVisible.Set(false);
            }
            var rates = SummonQuery.RateTable(db, Banner, 1);
            double rate = rates != null && rates.Rates.TryGetValue(it.Rarity, out var r) ? r : 0;
            SourceLine.Set(UiKeys.DetailSource, db.Banners.TryGetValue(Banner, out var bd) ? bd.NameKey : "", TextFmt.RarityCode(it.Rarity),
                           LabelArg.Num(rate * 100.0, NumberStyle.PctPlain));

            Equip.Visible.Set(it.Owned);
            bool can = it.Owned && !it.Equipped;
            Equip.Enabled.Set(can);
            Equip.Reason.Set(can ? null : it.Owned ? UiKeys.CollEquipped : UiKeys.CollNotOwned);
            SlotPickerVisible.Set(it.Owned && Banner == Banner.SKILL);
        }

        /// <summary>Weapons: EquipWeapon. Skills: EquipSkill into <paramref name="slot"/> (the picker; default the first empty).</summary>
        public void OnEquip(int slot = -1)
        {
            if (ItemId == null || Item == null || !Item.Owned) return;
            if (Banner == Banner.WEAPON) _c.Commands?.Send(new EquipWeapon(ItemId), AnchorEquip);
            else if (Banner == Banner.SKILL)
            {
                if (slot < 0) slot = FirstEmptySkillSlot();
                _c.Commands?.Send(new EquipSkill(slot, ItemId), AnchorEquip);
            }
        }

        int FirstEmptySkillSlot()
        {
            var slots = _sim?.State.Loadout.Skills;
            if (slots == null) return 0;
            for (int i = 0; i < slots.Length; i++) if (slots[i] == null) return i;
            return 0;
        }
    }
}
