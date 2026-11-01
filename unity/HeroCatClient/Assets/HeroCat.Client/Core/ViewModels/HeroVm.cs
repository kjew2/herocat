using System;
using System.Collections.Generic;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Power;

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
        public string Anchor => "hero.train." + Stat;

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

    /// <summary>
    /// SCR_HERO (07 section 4.4; issue #115): level and EXP bar, DPS, the equipped loadout row, the bulk step selector
    /// (TRAIN_BULK_STEPS plus Max, persisted in ClientPrefs) and one Training row per trainable stat. HP and DEF are not
    /// trainable in MVP (00 decision 10), so they get no row. Awakening and the Research button are MVP rows: Awakening needs
    /// the Awaken command (not in the M0 sim), so <see cref="AwakenVisible"/> stays false until it lands.
    /// </summary>
    public sealed class HeroVm
    {
        readonly VmContext _c;
        readonly List<TrainingRowVm> _rows = new List<TrainingRowVm>();
        readonly int[] _steps;

        public HeroVm(VmContext c)
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
                if (i < _steps.Length) StepLabels[i].Set(UiKeys.HeroStepN, _steps[i]);
                else StepLabels[i].Set(UiKeys.HeroStepMax);
            }
            Loadout = new Bindable<SlotLook>[1 + c.K.SkillSlots + c.K.CompanionSlots];
            for (int i = 0; i < Loadout.Length; i++) Loadout[i] = new Bindable<SlotLook>();
            int step = c.Prefs.TrainStep;
            SelectedStep.Set(step >= 0 && step <= _steps.Length ? step : 0);
        }

        public IReadOnlyList<TrainingRowVm> Rows => _rows;

        public readonly IntLabel Level = new IntLabel();
        public readonly NumberLabel Exp = new NumberLabel();
        public readonly NumberLabel ExpToNext = new NumberLabel();
        public readonly BarFill ExpFill = new BarFill();
        public readonly NumberLabel Dps = new NumberLabel();

        /// <summary>Weapon (index 0), then the skill slots, then the companion slots.</summary>
        public readonly Bindable<SlotLook>[] Loadout;

        /// <summary>Step labels "x1", "x10", "x100", "Max"; the last is Max.</summary>
        public readonly TextKeyLabel[] StepLabels;

        public readonly Bindable<int> SelectedStep = new Bindable<int>();
        public readonly Flag AwakenVisible = new Flag();

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

        public void Refresh(Simulation sim, in Snapshot s)
        {
            Level.Set(s.HeroLevel);
            Exp.Set(s.HeroExp);
            ExpToNext.Set(s.HeroExpToNext);
            ExpFill.Set(s.HeroExp, s.HeroExpToNext);
            Dps.Set(s.HeroDps);
            AwakenVisible.Set(false);   // TODO(#36): the MVP Awaken command and SCR_CONFIRM flow

            var st = sim.State;
            var db = _c.Db;
            string w = st.Loadout.Weapon;
            if (w != null && db.Weapons.TryGetValue(w, out var wd))
            {
                st.Weapons.Items.TryGetValue(w, out var owned);
                Loadout[0].Set(new SlotLook(w, wd.Icon, wd.Rarity, owned != null ? db.DupeLevel(owned.Copies) : 0, 0));
            }
            else Loadout[0].Set(default);
            var skills = st.Loadout.Skills;
            for (int i = 0; i < _c.K.SkillSlots; i++)
            {
                string id = skills != null && i < skills.Length ? skills[i] : null;
                if (id != null && db.Skills.TryGetValue(id, out var sd))
                {
                    st.Skills.Items.TryGetValue(id, out var owned);
                    Loadout[1 + i].Set(new SlotLook(id, sd.Icon, sd.Rarity, owned != null ? db.DupeLevel(owned.Copies) : 0, 0));
                }
                else Loadout[1 + i].Set(default);
            }
            for (int i = 1 + _c.K.SkillSlots; i < Loadout.Length; i++) Loadout[i].Set(default);

            var q = sim.Query;
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
                    r.Buy.Label.Set(UiKeys.HeroMax);
                    r.Buy.Reason.Set(UiKeys.HeroMax);
                }
                else
                {
                    if (max) r.Buy.Label.Set(UiKeys.HeroBuyLevels, count, LabelArg.Num(cost));
                    else r.Buy.Label.Set(UiKeys.HeroPlus);
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

        /// <summary>Loadout slot tap: SCR_ITEM_DETAIL for an item, the matching collection screen for an empty slot.</summary>
        public void OnLoadout(int index, ItemDetailVm detail)
        {
            if (index < 0 || index >= Loadout.Length) return;
            var v = Loadout[index].Value;
            Banner b = index == 0 ? Banner.WEAPON : index <= _c.K.SkillSlots ? Banner.SKILL : Banner.COMPANION;
            if (v.IsEmpty)
            {
                if (b == Banner.WEAPON) _c.Router?.Replace(ScreenIds.Weapons);
                else if (b == Banner.SKILL) _c.Router?.Replace(ScreenIds.Skills);
                else _c.LockedToast("COMPANIONS");
                return;
            }
            detail?.Open(b, v.ItemId);
        }

        public void OnEvent(Event e)
        {
            if (e is StatTrained t)
            {
                var r = Row(t.Stat);
                if (r != null) r.FlashSerial++;
                _c.Cues?.Feel(Haptic.None, Sounds.Upgrade);
            }
        }
    }
}
