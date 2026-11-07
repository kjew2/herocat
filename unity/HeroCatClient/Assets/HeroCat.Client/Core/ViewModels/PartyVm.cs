using System;
using System.Collections.Generic;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Power;
using HeroCat.Sim.State;

namespace HeroCat.Client.Core
{
    /// <summary>One Training row (07 section 4.4): level, effect, the selected step's cost and count, the [+] button.</summary>
    public sealed class TrainingRowVm
    {
        public TrainingRowVm(VmContext c, StatDef def)
        {
            Stat = def.Id;
            NameKey = def.NameKey;
            Name = c.Label();
            Name.Set(def.NameKey);
            Effect = c.Label();
            Buy = c.Button();
        }

        public Stat Stat { get; }
        public string NameKey { get; }

        /// <summary>The anchor id of this row's [+] (reject toast and shake).</summary>
        public string Anchor => "party.train." + Stat;

        public readonly TextKeyLabel Name;
        public readonly IntLabel Level = new IntLabel();

        /// <summary>03's per-stat effect: x4.20 (ATK), 12.5% (crit rate), +45% (crit damage), +16% (speed, gold, EXP).</summary>
        public readonly TextKeyLabel Effect;

        public readonly NumberLabel Cost = new NumberLabel();
        public readonly IntLabel Count = new IntLabel();
        public readonly Flag CanAfford = new Flag();
        public readonly Flag AtCap = new Flag();

        /// <summary>[+]: label "+" / "Buy 37 levels for 3.71M" on Max / "MAX" at cap.</summary>
        public readonly ButtonState Buy;

        /// <summary>Levels the current step buys (0 at cap).</summary>
        public int CountNow { get; internal set; }

        /// <summary>Flashes on StatTrained (07 section 4.4 events).</summary>
        public int FlashSerial { get; internal set; }
    }

    /// <summary>One party slot card on SCR_PARTY (07 section 4.4, 00 decision 109).</summary>
    public sealed class PartySlotVm
    {
        public PartySlotVm(VmContext c, int slot)
        {
            Slot = slot;
            Awaken = c.Button();
        }

        /// <summary>0-based slot index; players see Slot + 1.</summary>
        public int Slot { get; }

        public string AwakenAnchor => "party.awaken." + Slot;

        public readonly Bindable<SlotLook> Companion = new Bindable<SlotLook>();
        public readonly Bindable<SlotLook> Weapon = new Bindable<SlotLook>();
        public readonly Bindable<SlotLook> Skill = new Bindable<SlotLook>();
        public readonly IntLabel AwakenRank = new IntLabel();
        public readonly BarFill Mp = new BarFill();
        public readonly NumberLabel SlotDps = new NumberLabel();

        /// <summary>Awaken this slot (MVP): visible when the tier has ranks, enabled when Awaken(slot) would be accepted.</summary>
        public readonly ButtonState Awaken;
    }

    /// <summary>
    /// SCR_PARTY (07 section 4.4; issue #115, 00 decision 109): Party Level and EXP bar, party DPS, the five slot cards
    /// (companion, weapon, skill, MP, Awakening), the bulk step selector (TRAIN_BULK_STEPS plus Max, persisted in
    /// ClientPrefs) and one party-wide Training row per trainable stat. HP and DEF are not trainable in MVP (00 decision
    /// 10), so they get no row.
    /// </summary>
    public sealed class PartyVm
    {
        readonly VmContext _c;
        readonly List<TrainingRowVm> _rows = new List<TrainingRowVm>();
        readonly int[] _steps;

        public PartyVm(VmContext c)
        {
            _c = c;
            foreach (var def in c.Db.Stats)
                if (def.Trainable && c.Db.TrainingCurves.ContainsKey(def.Id)) _rows.Add(new TrainingRowVm(c, def));
            var bulk = c.K.TrainBulkSteps;
            _steps = new int[bulk.Count];
            for (int i = 0; i < bulk.Count; i++) _steps[i] = bulk[i];
            StepLabels = new TextKeyLabel[_steps.Length + 1];
            for (int i = 0; i < StepLabels.Length; i++)
            {
                StepLabels[i] = c.Label();
                if (i < _steps.Length) StepLabels[i].Set(UiKeys.PartyStepN, _steps[i]);
                else StepLabels[i].Set(UiKeys.PartyStepMax);
            }
            Slots = new PartySlotVm[c.K.PartySlots];
            Research = new ResearchEntryVm(c, toastWhenLocked: false);
            for (int i = 0; i < Slots.Length; i++) Slots[i] = new PartySlotVm(c, i);
            int step = c.Prefs.TrainStep;
            SelectedStep.Set(step >= 0 && step <= _steps.Length ? step : 0);
        }

        public IReadOnlyList<TrainingRowVm> Rows => _rows;

        public readonly IntLabel Level = new IntLabel();
        public readonly NumberLabel Exp = new NumberLabel();
        public readonly NumberLabel ExpToNext = new NumberLabel();
        public readonly BarFill ExpFill = new BarFill();
        public readonly NumberLabel Dps = new NumberLabel();

        /// <summary>The five slot cards, slot 1 first.</summary>
        public readonly PartySlotVm[] Slots;

        /// <summary>[Research &gt;] (MVP): opens SCR_RESEARCH, which shows its Locked state before the gate.</summary>
        public readonly ResearchEntryVm Research;

        /// <summary>Step labels "x1", "x10", "x100", "Max"; the last is Max.</summary>
        public readonly TextKeyLabel[] StepLabels;

        public readonly Bindable<int> SelectedStep = new Bindable<int>();

        public int StepCount => StepLabels.Length;
        public bool IsMaxStep => SelectedStep.Value >= _steps.Length;

        public TrainingRowVm Row(Stat s)
        {
            foreach (var r in _rows) if (r.Stat == s) return r;
            return null;
        }

        public void SelectStep(int index)
        {
            if (index < 0 || index >= StepLabels.Length) return;
            SelectedStep.Set(index);
            _c.Prefs.TrainStep = index;
        }

        static SlotLook Look<TDef>(string id, IReadOnlyDictionary<string, TDef> defs, CollectionState owned, ContentDb db,
                                   Func<TDef, string> icon, Func<TDef, Rarity> rarity, float cooldown01 = 0)
        {
            if (id == null || !defs.TryGetValue(id, out var d)) return default;
            owned.Items.TryGetValue(id, out var item);
            return new SlotLook(id, icon(d), rarity(d), item != null ? db.DupeLevel(item.Copies) : 0, cooldown01);
        }

        public void Refresh(Simulation sim, in Snapshot s)
        {
            Level.Set(s.PartyLevel);
            Exp.Set(s.PartyExp);
            ExpToNext.Set(s.PartyExpToNext);
            ExpFill.Set(s.PartyExp, s.PartyExpToNext);
            Dps.Set(s.PartyDps);
            Research.Refresh(sim);

            var st = sim.State;
            var db = _c.Db;
            var q = sim.Query;
            bool hasRanks = db.Awakening.Count > 0;
            for (int i = 0; i < Slots.Length; i++)
            {
                var vm = Slots[i];
                var x = i < st.Party.Slots.Length ? st.Party.Slots[i] : null;
                vm.Companion.Set(Look(x?.Companion, db.Companions, st.Companions, db, d => d.Icon, d => d.Rarity));
                vm.Weapon.Set(Look(x?.Weapon, db.Weapons, st.Weapons, db, d => d.Icon, d => d.Rarity));
                vm.Skill.Set(Look(x?.Skill, db.Skills, st.Skills, db, d => d.Icon, d => d.Rarity));
                var snap = s.Slot(i);
                vm.AwakenRank.Set(snap.AwakenRank);
                vm.Mp.Set(snap.Mp, s.MpMax);
                vm.SlotDps.Set(snap.SlotDps);
                vm.Awaken.Visible.Set(hasRanks && q.NextAwakenStageGate(i).HasValue);
                bool can = q.CanAwaken(i);
                vm.Awaken.Enabled.Set(can);
                if (q.NextAwakenStageGate(i) is int gate && st.Progress.HighestCleared < gate) vm.Awaken.Reason.Set(UiKeys.PartyAwakenGate, gate);
                else if (!can && hasRanks) vm.Awaken.Reason.Set(UiKeys.NeedMore, LabelArg.Num(q.NextAwakenCost(i) - st.Wallet.RockCandy));
                else vm.Awaken.Reason.Set(null);
            }

            bool max = IsMaxStep;
            int step = max ? 0 : _steps[SelectedStep.Value];
            foreach (var r in _rows)
            {
                var def = db.Stat(r.Stat);
                int level = q.TrainLevel(r.Stat);
                r.Level.Set(level);
                SetEffect(r, q);
                int room = def.Cap.HasValue ? Math.Max(0, def.Cap.Value - level) : int.MaxValue;
                bool atCap = room == 0;
                r.AtCap.Set(atCap);
                int count;
                if (atCap) count = 0;
                else if (max) count = Math.Max(1, Math.Min(room, q.MaxAffordableLevels(r.Stat)));
                else count = Math.Min(step, room);
                r.CountNow = count;
                r.Count.Set(count);
                double cost = count > 0 ? q.TrainCost(r.Stat, count) : 0;
                r.Cost.Set(cost);
                bool afford = count > 0 && s.Gold >= cost;
                r.CanAfford.Set(afford);
                r.Buy.Visible.Set(true);
                r.Buy.Enabled.Set(afford);
                if (atCap)
                {
                    r.Buy.Label.Set(UiKeys.PartyMax);
                    r.Buy.Reason.Set(UiKeys.PartyMax);
                }
                else
                {
                    if (max) r.Buy.Label.Set(UiKeys.PartyBuyLevels, count, LabelArg.Num(cost));
                    else r.Buy.Label.Set(UiKeys.PartyPlus);
                    if (afford) r.Buy.Reason.Set(null);
                    else r.Buy.Reason.Set(UiKeys.NeedMore, LabelArg.Num(cost - s.Gold));
                }
            }
        }

        static void SetEffect(TrainingRowVm r, SimQuery q)
        {
            switch (r.Stat)
            {
                case Stat.ATK: r.Effect.Set(UiKeys.Raw, LabelArg.Num(q.Power.Training, NumberStyle.Mult)); break;
                case Stat.CRIT_RATE: r.Effect.Set(UiKeys.Raw, LabelArg.Num(q.CritRate * 100.0, NumberStyle.PctPlain)); break;
                case Stat.CRIT_DMG: r.Effect.Set(UiKeys.Raw, LabelArg.Num((q.CritDmg - 1.0) * 100.0, NumberStyle.PctPoints)); break;
                case Stat.ATK_SPD: r.Effect.Set(UiKeys.Raw, LabelArg.Num((q.SpeedFactor - 1.0) * 100.0, NumberStyle.PctPoints)); break;
                case Stat.GOLD_GAIN: r.Effect.Set(UiKeys.Raw, LabelArg.Num((q.GoldMult - 1.0) * 100.0, NumberStyle.PctPoints)); break;
                case Stat.EXP_GAIN: r.Effect.Set(UiKeys.Raw, LabelArg.Num((q.ExpMult - 1.0) * 100.0, NumberStyle.PctPoints)); break;
                default: r.Effect.Set(UiKeys.Raw, ""); break;
            }
        }

        /// <summary>[+]: <c>TrainStat(stat, count)</c>, all or nothing, with the count shown for the selected step.</summary>
        public void OnTrain(Stat stat)
        {
            var r = Row(stat);
            if (r == null) return;
            int n = r.CountNow > 0 ? r.CountNow : 1;
            _c.Commands?.Send(new TrainStat(stat, n), r.Anchor);
        }

        /// <summary>
        /// A slot card's companion, weapon or skill: the matching collection screen in slot mode for that slot (07 section 3).
        /// </summary>
        public void OnSlotItem(int slot, Banner kind)
        {
            if (slot < 0 || slot >= Slots.Length) return;
            _c.TargetSlot = slot;
            if (kind == Banner.WEAPON) _c.Router?.Replace(ScreenIds.Weapons);
            else if (kind == Banner.SKILL) _c.Router?.Replace(ScreenIds.Skills);
            else _c.Router?.Replace(ScreenIds.Companions);
        }

        /// <summary>Awaken slot N: <c>Awaken(slot)</c>; a reject shakes the slot's button with its toast.</summary>
        public void OnAwaken(int slot)
        {
            if (slot < 0 || slot >= Slots.Length) return;
            _c.Commands?.Send(new Awaken(slot), Slots[slot].AwakenAnchor);
        }

        /// <summary>"Auto-equip all": the three AutoEquipBest* commands, companions first so weapons and skills follow them.</summary>
        public void OnAutoEquipAll()
        {
            _c.Commands?.Send(new AutoEquipBestCompanions());
            _c.Commands?.Send(new AutoEquipBestWeapons());
            _c.Commands?.Send(new AutoEquipBestSkills());
        }

        public void OnEvent(Event e)
        {
            if (e is StatTrained t)
            {
                var r = Row(t.Stat);
                if (r != null) r.FlashSerial++;
                _c.Cues?.Feel(Haptic.None, Sounds.Upgrade);
            }
            else if (e is Awakened) _c.Cues?.Feel(Haptic.Success, Sounds.Upgrade);
        }
    }
}
