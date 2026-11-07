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

        /// <summary>Skills and companions: the cooldown. Skills: the cast type.</summary>
        public CastType CastType;
        public int CooldownTicks;

        /// <summary>Companions only (07 section 4.7).</summary>
        public AbilityType AbilityType;
        public int MpCost;

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

        /// <summary>The five party slots' item of this kind (07 sections 4.5-4.7, 00 decision 109), slot 1 first.</summary>
        public Bindable<SlotLook>[] PartySlots { get; private set; }

        /// <summary>The slot the screen equips into: slot mode's target (07 section 3), else <see cref="DefaultSlot"/>.</summary>
        public int TargetSlot => C.TargetSlot is int t && t >= 0 && t < C.K.PartySlots ? t : DefaultSlot();

        /// <summary>Slot mode is on: pushed from a SCR_PARTY slot card.</summary>
        public bool InSlotMode => C.TargetSlot.HasValue;

        /// <summary>Clears slot mode (the banner's [x], or back to SCR_PARTY after an accepted equip).</summary>
        public void ExitSlotMode() => C.TargetSlot = null;

        Simulation _simForSlots;

        string FieldOf(PartySlot x) => x == null ? null : Banner == Banner.WEAPON ? x.Weapon : Banner == Banner.SKILL ? x.Skill : x.Companion;

        /// <summary>Fills <see cref="PartySlots"/> from the state (called by each screen's refresh).</summary>
        protected void FillPartySlots(Simulation sim)
        {
            _simForSlots = sim;
            int n = C.K.PartySlots;
            if (PartySlots == null || PartySlots.Length != n)
            {
                PartySlots = new Bindable<SlotLook>[n];
                for (int i = 0; i < n; i++) PartySlots[i] = new Bindable<SlotLook>();
            }
            var st = sim.State;
            var col = CollectionRules.For(st, Banner);
            for (int i = 0; i < n; i++)
            {
                string id = i < st.Party.Slots.Length ? FieldOf(st.Party.Slots[i]) : null;
                if (id != null && CollectionRules.IsKnown(C.Db, Banner, id))
                {
                    col.Items.TryGetValue(id, out var owned);
                    PartySlots[i].Set(new SlotLook(id, CollectionRules.IconOf(C.Db, Banner, id), CollectionRules.RarityOf(C.Db, Banner, id),
                                                   owned != null ? C.Db.DupeLevel(owned.Copies) : 0, 0));
                }
                else PartySlots[i].Set(default);
            }
        }

        /// <summary>The first slot with a companion but none of this kind, else the first empty one, else slot 0.</summary>
        public int DefaultSlot()
        {
            var st = _simForSlots?.State;
            if (st == null) return 0;
            var slots = st.Party.Slots;
            for (int i = 0; i < slots.Length; i++) if (slots[i].Companion != null && FieldOf(slots[i]) == null) return i;
            for (int i = 0; i < slots.Length; i++) if (FieldOf(slots[i]) == null) return i;
            return 0;
        }

        /// <summary>The slot that holds <paramref name="id"/>, or -1 (an item is in at most one slot).</summary>
        public int SlotOf(string id)
        {
            var st = _simForSlots?.State;
            if (st == null || id == null) return -1;
            for (int i = 0; i < st.Party.Slots.Length; i++) if (FieldOf(st.Party.Slots[i]) == id) return i;
            return -1;
        }

        protected abstract Command EquipCommand(int slot, string id);
        protected abstract Command UnequipCommand(int slot);

        public static string SlotAnchorFor(Banner b, int slot) => b.ToString().ToLowerInvariant() + ".slot." + slot;

        /// <summary>
        /// Equip <paramref name="id"/> into <paramref name="slot"/>. An item already in another slot moves ("Move here",
        /// 07 section 4.5): <c>Unequip(from)</c> then <c>Equip(slot, id)</c>, two sim commands. In slot mode an accepted equip
        /// returns to SCR_PARTY.
        /// </summary>
        public void EquipTo(int slot, string id)
        {
            if (id == null || slot < 0 || slot >= C.K.PartySlots) return;
            int from = SlotOf(id);
            if (from == slot) return;
            if (from >= 0) C.Commands?.Send(UnequipCommand(from), SlotAnchorFor(Banner, from));
            var r = C.Commands?.Send(EquipCommand(slot, id), SlotAnchorFor(Banner, slot));
            if (InSlotMode && r.HasValue && r.Value.Ok)
            {
                ExitSlotMode();
                C.Router?.Replace(ScreenIds.Party);
            }
        }

        /// <summary>A party slot tap: with a selection, equip it there; without one, empty the slot.</summary>
        public void OnSlot(int slot)
        {
            if (slot < 0 || slot >= C.K.PartySlots) return;
            if (Selected != null && Selected.Owned) { EquipTo(slot, Selected.Id); return; }
            if (PartySlots != null && !PartySlots[slot].Value.IsEmpty) C.Commands?.Send(UnequipCommand(slot), SlotAnchorFor(Banner, slot));
        }

        /// <summary>The selected panel's Equip: into the target slot (slot mode) or the default slot.</summary>
        public void OnEquip()
        {
            if (Selected == null) return;
            EquipTo(TargetSlot, Selected.Id);
        }

        /// <summary>The slot picker's explicit choice.</summary>
        public void OnEquipTo(int slot)
        {
            if (Selected == null) return;
            EquipTo(slot, Selected.Id);
        }

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
            else if (b == Banner.COMPANION && C.Db.Companions.Count > 0) C.Router?.Replace(ScreenIds.Companions);
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
                case ItemLimitBroken lb when CollectionRules.IsKnown(C.Db, Banner, lb.ItemId):   // values after LevelMult
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

        protected override Command EquipCommand(int slot, string id) => new EquipWeapon(slot, id);
        protected override Command UnequipCommand(int slot) => new UnequipWeapon(slot);

        protected override void RefreshExtra(Simulation sim)
        {
            FillPartySlots(sim);
            var p = sim.Query.Power;
            int slot = TargetSlot;
            PassiveTotalPct.Set(p.WeaponPassivePct);
            EquippedAtkPct.Set(p.EquippedWeaponPct(slot));
            string id = sim.State.Party.Slots[slot].Weapon;
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

        public void OnAutoEquip() => C.Commands?.Send(new AutoEquipBestWeapons(), AnchorEquip);
    }

    /// <summary>
    /// SCR_SKILLS (07 section 4.6; issue #116, 00 decision 109): one skill per party slot (five chips showing each slot's
    /// companion), Auto-equip, the 40-tile grid with a cast-type glyph. A slot tap with a selection sends
    /// <c>EquipSkill(slot, id)</c>; without one, <c>UnequipSkill(slot)</c>.
    /// </summary>
    public sealed class SkillsVm : CollectionScreenVm
    {
        public const string AnchorAuto = "skills.auto";

        public SkillsVm(VmContext c) : base(c, Banner.SKILL)
        {
            AutoEquip = c.Button();
            AutoEquip.Label.Set(UiKeys.SkillsAutoEquip);
        }

        /// <summary>The five per-slot skills (the base class's <see cref="CollectionScreenVm.PartySlots"/>).</summary>
        public Bindable<SlotLook>[] Slots => PartySlots;
        public readonly ButtonState AutoEquip;

        /// <summary>Red dot on Auto-equip when it would change the loadout (04 ordering rule).</summary>
        public readonly Flag AutoEquipWouldChange = new Flag();

        public static string SlotAnchor(int slot) => SlotAnchorFor(Banner.SKILL, slot);

        protected override Command EquipCommand(int slot, string id) => new EquipSkill(slot, id);
        protected override Command UnequipCommand(int slot) => new UnequipSkill(slot);

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
            FillPartySlots(sim);
            AutoEquipWouldChange.Set(CollectionRules.BetterSkillAvailable(C.Db, sim.State));
            AutoEquip.Visible.Set(true);
            AutoEquip.Enabled.Set(true);
        }

        protected override void RefreshSelectedStats(TileVm t) =>
            SelectedStats.Set(UiKeys.SkillStats, LabelArg.Num(t.ValueNow, NumberStyle.PctPoints),
                              LabelArg.Num(t.CooldownTicks / (double)C.K.TickHz, NumberStyle.Seconds), UiKeys.CastTypeKey(t.CastType));

        public void OnAutoEquip() => C.Commands?.Send(new AutoEquipBestSkills(), AnchorAuto);
    }

    /// <summary>
    /// SCR_COMPANIONS (07 section 4.7; issues #181, #291, 00 decision 109): five always-open party slots, each with its own
    /// MP pool, the MP line, Auto-equip and the 50-tile grid. A slot tap with a selection sends <c>EquipCompanion(slot, id)</c>;
    /// without one, <c>UnequipCompanion(slot)</c>. The slot keeps its weapon, skill and Awakening.
    /// </summary>
    public sealed class CompanionsVm : CollectionScreenVm
    {
        public const string AnchorAuto = "companions.auto";

        public CompanionsVm(VmContext c) : base(c, Banner.COMPANION)
        {
            SlotMp = new BarFill[c.K.PartySlots];
            SlotMpSufficiencyPct = new NumberLabel[c.K.PartySlots];
            for (int i = 0; i < SlotMp.Length; i++)
            {
                SlotMp[i] = new BarFill();
                SlotMpSufficiencyPct[i] = new NumberLabel(NumberStyle.PctPlain);
            }
            MpLine = c.Label();
            BudgetLine = c.Label();
            AutoEquip = c.Button();
            AutoEquip.Label.Set(UiKeys.SkillsAutoEquip);
            Research = new ResearchEntryVm(c, toastWhenLocked: false);
        }

        public Bindable<SlotLook>[] Slots => PartySlots;

        /// <summary>The MP line's "(Research +)" (MVP, 07 section 4.7): opens SCR_RESEARCH.</summary>
        public readonly ResearchEntryVm Research;

        /// <summary>Each slot's own MP pool (03 section 2.4).</summary>
        public readonly BarFill[] SlotMp;

        /// <summary>Each occupied slot's mpSufficiency as a percentage (100 = full ability uptime).</summary>
        public readonly NumberLabel[] SlotMpSufficiencyPct;

        /// <summary>"MP 40 / 100 · regen 0.5/s" for the target slot's pool.</summary>
        public readonly TextKeyLabel MpLine;

        /// <summary>The target slot's companion cost per second; <see cref="OverBudget"/> when any occupied pool runs short.</summary>
        public readonly TextKeyLabel BudgetLine;
        public readonly Flag OverBudget = new Flag();
        public readonly ButtonState AutoEquip;

        public static string SlotAnchor(int slot) => SlotAnchorFor(Banner.COMPANION, slot);

        protected override Command EquipCommand(int slot, string id) => new EquipCompanion(slot, id);
        protected override Command UnequipCommand(int slot) => new UnequipCompanion(slot);

        protected override void FillValues(TileVm t, double multNow, double multNext)
        {
            if (!C.Db.Companions.TryGetValue(t.Id, out var d)) return;
            t.ValueNow = d.DpsPct * multNow;
            t.ValueNext = d.DpsPct * multNext;
            t.AbilityType = d.AbilityType;
            t.MpCost = d.MpCost;
            t.CooldownTicks = d.CooldownTicks;
        }

        protected override void RefreshExtra(Simulation sim)
        {
            FillPartySlots(sim);
            Research.Refresh(sim);
            var db = C.Db;
            var st = sim.State;
            var p = sim.Query.Power;
            double max = ResearchRules.MpPoolMax(db, st);
            bool short_ = false;
            for (int i = 0; i < SlotMp.Length; i++)
            {
                SlotMp[i].Set(i < st.Combat.Mp.Length ? st.Combat.Mp[i] : 0, max);
                SlotMpSufficiencyPct[i].Set(p.MpSufficiency(i) * 100.0);
                if (p.Occupied(i) && p.MpSufficiency(i) < 1.0) short_ = true;
            }
            int slot = TargetSlot;
            double demand = 0.0;
            string id = st.Party.Slots[slot].Companion;
            if (id != null && db.Companions.TryGetValue(id, out var d) && d.CooldownTicks > 0) demand = d.MpCost * (double)C.K.TickHz / d.CooldownTicks;
            double regen = ResearchRules.MpRegenPerSecond(db, st);
            MpLine.Set(UiKeys.CompanionsMp, LabelArg.Num(slot < st.Combat.Mp.Length ? st.Combat.Mp[slot] : 0, NumberStyle.Short),
                       LabelArg.Num(max, NumberStyle.Short), LabelArg.Num(regen, NumberStyle.Ratio));
            BudgetLine.Set(UiKeys.CompanionsBudget, LabelArg.Num(demand, NumberStyle.Ratio));
            OverBudget.Set(short_);
            AutoEquip.Visible.Set(true);
            AutoEquip.Enabled.Set(true);
        }

        protected override void RefreshSelectedStats(TileVm t) =>
            SelectedStats.Set(UiKeys.CompanionsStats, UiKeys.AbilityKey(t.AbilityType), LabelArg.Num(t.ValueNow, NumberStyle.PctPoints),
                              LabelArg.Num(t.CooldownTicks / (double)C.K.TickHz, NumberStyle.Seconds));

        public void OnAutoEquip() => C.Commands?.Send(new AutoEquipBestCompanions(), AnchorAuto);
    }

    /// <summary>
    /// SCR_ITEM_DETAIL (07 section 4.19; issue #116): any collectible's card: level and copies to next (DUPE_LVL_CUM),
    /// kind-specific stats with the next-level preview, source banner and base rate, Equip with a slot picker for skills,
    /// weapon Compare, the NEW ribbon and the "Not owned" state; Limit break (04 sections 5.2 and 11; issue #255).
    /// </summary>
    public sealed class ItemDetailVm
    {
        public const string AnchorEquip = "detail.equip";
        public const string AnchorLimitBreak = "detail.limit_break";

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
            LimitBreakLine = c.Label();
            LimitBreak = c.Button();
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

        /// <summary>"Limit breaks 2 / 5" under the level block; empty while unowned (04 section 5.2).</summary>
        public readonly TextKeyLabel LimitBreakLine;

        /// <summary>
        /// "Limit break (N Rock Candy)" (04 section 11): visible when owned; disabled at LIMIT_BREAK_RANKS
        /// (<c>NextLimitBreakCost == 0</c>) or when Rock Candy is short; sends <c>LimitBreak(id)</c>.
        /// </summary>
        public readonly ButtonState LimitBreak;

        /// <summary>Skills: the slot picker row (07 section 4.19).</summary>
        public readonly Flag SlotPickerVisible = new Flag();

        public bool Compare { get; private set; }

        /// <summary>The card art (07 section 4.19): splash for an L+ companion (00 decision 108), else the portrait, else the icon.</summary>
        public string ArtAddress { get; private set; }

        /// <summary>Tried when <see cref="ArtAddress"/> has no file (a missing splash falls back to the portrait).</summary>
        public string FallbackArtAddress { get; private set; }

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
                CompareLine.Set(UiKeys.DetailCompare, LabelArg.Num(p.EquippedWeaponPct(PickSlot(-1)), NumberStyle.PctPoints));
            }
            else if (Banner == Banner.SKILL && db.Skills.TryGetValue(ItemId, out var s))
            {
                StatLine.Set(UiKeys.SkillStats, LabelArg.Num(s.DpsPct * multNow, NumberStyle.PctPoints),
                             LabelArg.Num(s.CooldownTicks / (double)_c.K.TickHz, NumberStyle.Seconds), UiKeys.CastTypeKey(s.CastType));
                NextLine.Set(Item.AtMaxLevel ? UiKeys.DetailAtMax : UiKeys.DetailNext, LabelArg.Num(s.DpsPct * multNext, NumberStyle.PctPoints));
                PassiveLine.Set(null);
                CompareVisible.Set(false);
            }
            else if (Banner == Banner.COMPANION && db.Companions.TryGetValue(ItemId, out var cd))
            {
                StatLine.Set(UiKeys.CompanionsStats, UiKeys.AbilityKey(cd.AbilityType), LabelArg.Num(cd.DpsPct * multNow, NumberStyle.PctPoints),
                             LabelArg.Num(cd.CooldownTicks / (double)_c.K.TickHz, NumberStyle.Seconds));
                NextLine.Set(Item.AtMaxLevel ? UiKeys.DetailAtMax : UiKeys.DetailNext, LabelArg.Num(cd.DpsPct * multNext, NumberStyle.PctPoints));
                PassiveLine.Set(cd.DescKey);
                CompareVisible.Set(false);
            }
            ArtAddress = ArtAddresses.SplashFor(Banner, ItemId, it.Rarity)
                         ?? (Banner == Banner.COMPANION ? ArtAddresses.PortraitFor(ItemId) : it.Icon);
            FallbackArtAddress = Banner == Banner.COMPANION ? ArtAddresses.PortraitFor(ItemId) : null;
            var rates = SummonQuery.RateTable(db, Banner, 1);
            double rate = rates != null && rates.Rates.TryGetValue(it.Rarity, out var r) ? r : 0;
            SourceLine.Set(UiKeys.DetailSource, db.Banners.TryGetValue(Banner, out var bd) ? bd.NameKey : "", TextFmt.RarityCode(it.Rarity),
                           LabelArg.Num(rate * 100.0, NumberStyle.PctPlain));

            Equip.Visible.Set(it.Owned);
            bool can = it.Owned && !it.Equipped;
            Equip.Enabled.Set(can);
            Equip.Reason.Set(can ? null : it.Owned ? UiKeys.CollEquipped : UiKeys.CollNotOwned);
            SlotPickerVisible.Set(it.Owned);   // every kind equips into a chosen party slot (00 decision 109)

            // Limit break (04 sections 5.2 and 11), companions included.
            long cost = it.NextLimitBreakCost;
            if (it.Owned) LimitBreakLine.Set(UiKeys.DetailLimitBreaks, it.LimitBreaks, _c.K.LimitBreakRanks);
            else LimitBreakLine.Set(null);
            bool capped = it.Owned && cost <= 0;
            bool canBreak = it.Owned && !capped && sim.State.Wallet.RockCandy >= cost;
            LimitBreak.Visible.Set(it.Owned);
            if (capped) LimitBreak.Label.Set(UiKeys.RejectLimitBreakCapped);
            else LimitBreak.Label.Set(UiKeys.DetailLimitBreak, cost);
            LimitBreak.Enabled.Set(canBreak);
            LimitBreak.Reason.Set(canBreak ? null : !it.Owned ? UiKeys.CollNotOwned
                                  : capped ? UiKeys.RejectLimitBreakCapped : UiKeys.RejectNotEnoughRockCandy);
        }

        /// <summary><c>LimitBreak(id)</c> (04 section 5.2); the sim rejects (with feedback on the anchor) when it cannot.</summary>
        public void OnLimitBreak()
        {
            if (ItemId == null || Item == null || !Item.Owned) return;
            _c.Commands?.Send(new HeroCat.Sim.Commands.LimitBreak(ItemId), AnchorLimitBreak);
        }

        /// <summary>
        /// Equip into party slot <paramref name="slot"/> (the picker; default: slot mode's target, else the first slot with a
        /// companion but none of this kind, else the first empty one). An item in another slot moves there (Unequip, Equip).
        /// </summary>
        public void OnEquip(int slot = -1)
        {
            if (ItemId == null || Item == null || !Item.Owned || _sim == null) return;
            slot = PickSlot(slot);
            var slots = _sim.State.Party.Slots;
            for (int i = 0; i < slots.Length; i++)
                if (i != slot && Field(slots[i]) == ItemId) _c.Commands?.Send(Unequip(i), AnchorEquip);
            if (Banner == Banner.WEAPON) _c.Commands?.Send(new EquipWeapon(slot, ItemId), AnchorEquip);
            else if (Banner == Banner.SKILL) _c.Commands?.Send(new EquipSkill(slot, ItemId), AnchorEquip);
            else if (Banner == Banner.COMPANION) _c.Commands?.Send(new EquipCompanion(slot, ItemId), AnchorEquip);
        }

        string Field(PartySlot x) => Banner == Banner.WEAPON ? x.Weapon : Banner == Banner.SKILL ? x.Skill : x.Companion;

        Command Unequip(int slot) =>
            Banner == Banner.WEAPON ? new UnequipWeapon(slot) : Banner == Banner.SKILL ? (Command)new UnequipSkill(slot) : new UnequipCompanion(slot);

        int PickSlot(int slot)
        {
            int n = _c.K.PartySlots;
            if (slot >= 0 && slot < n) return slot;
            if (_c.TargetSlot is int t && t >= 0 && t < n) return t;
            var slots = _sim?.State.Party.Slots;
            if (slots == null) return 0;
            for (int i = 0; i < slots.Length; i++) if (slots[i].Companion != null && Field(slots[i]) == null) return i;
            for (int i = 0; i < slots.Length; i++) if (Field(slots[i]) == null) return i;
            return 0;
        }
    }
}
