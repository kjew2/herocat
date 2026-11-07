using System;
using System.Collections.Generic;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;

namespace HeroCat.Client.Core
{
    /// <summary>One SCR_RESEARCH row (07 section 4.9): level / max, current -> next value, Fish Coin cost, [Buy].</summary>
    public sealed class ResearchRowVm
    {
        public ResearchRowVm(VmContext c, ResearchDef def)
        {
            NodeId = def.Id;
            Effect = def.Effect;
            LayoutRow = def.LayoutRow;
            LayoutCol = def.LayoutCol;
            IconId = def.Icon;
            Name = c.Label();
            Name.Set(def.NameKey ?? def.Id);
            Desc = c.Label();
            Desc.Set(def.DescKey ?? "");
            CurrentText = c.Label();
            NextText = c.Label();
            Buy = c.Button();
        }

        public string NodeId { get; }
        public ResearchEffect Effect { get; }
        public int LayoutRow { get; }
        public int LayoutCol { get; }
        public string IconId { get; }

        /// <summary>The anchor id of this row's [Buy] (reject toast and shake).</summary>
        public string Anchor => "research.buy." + NodeId;

        public readonly TextKeyLabel Name, Desc;
        public readonly IntLabel Level = new IntLabel();
        public readonly IntLabel MaxLevel = new IntLabel();

        /// <summary>The effect at the bought level ("20", "5.6s", "+15%", "24h").</summary>
        public readonly TextKeyLabel CurrentText;

        /// <summary>The effect one level up; empty at max.</summary>
        public readonly TextKeyLabel NextText;

        /// <summary>Fish Coins for the next level, rounded up for display (the charge is unrounded); 0 at max.</summary>
        public readonly NumberLabel Cost = new NumberLabel();

        public readonly Flag CanAfford = new Flag();
        public readonly Flag AtMax = new Flag();

        /// <summary>[Buy]: enabled when Research(nodeId) would be accepted; a disabled one names the reason.</summary>
        public readonly ButtonState Buy;

        /// <summary>Bumped on ResearchLeveled for this node (row flash).</summary>
        public int FlashSerial { get; internal set; }
    }

    /// <summary>
    /// A Research entry button (07 sections 4.3 and 4.4; issue #181): the Fish Board's [Research], SCR_PARTY's
    /// [Research &gt;] and SCR_COMPANIONS' "(Research +)". Hidden in a tier without Research rows (M0). The Fish Board
    /// button shows the lock glyph before the RESEARCH feature_unlocks gate and toasts its condition; the others open
    /// SCR_RESEARCH, which shows its Locked state. The red dot (Badges.ResearchAffordable) is issue #180's.
    /// </summary>
    public sealed class ResearchEntryVm
    {
        readonly VmContext _c;
        readonly bool _toastWhenLocked;

        public ResearchEntryVm(VmContext c, bool toastWhenLocked)
        {
            _c = c;
            _toastWhenLocked = toastWhenLocked;
        }

        public readonly Flag Visible = new Flag();
        public readonly Flag Locked = new Flag();

        public void Refresh(Simulation sim)
        {
            Visible.Set(ResearchVm.InTier(_c.Db));
            Locked.Set(!_c.Db.IsUnlocked(ResearchVm.Feature, sim.State));
        }

        public void OnTap() => ResearchVm.Open(_c, _toastWhenLocked && Locked.Value);
    }

    /// <summary>
    /// SCR_RESEARCH (07 section 4.9, 03 section 6; issue #181): the Fish Coin header, one row per node in layout order
    /// (layout_row, layout_col), the Locked state before the RESEARCH feature_unlocks gate (every Buy disabled, the
    /// condition shown), and the first-time coach mark on Tank capacity. Every number comes from the sim's ResearchRules,
    /// so a row and the sim's accept/reject always agree.
    /// </summary>
    public sealed class ResearchVm
    {
        public const string Feature = AwakeningRules.ResearchFeature;

        /// <summary>The ClientPrefs.SeenCoachMarks id of the Tank capacity coach mark.</summary>
        public const string CoachMarkId = "research.tank_cap";

        /// <summary>The node the coach mark points at.</summary>
        public const string CoachNode = "res_tank_cap";

        readonly VmContext _c;
        readonly List<ResearchRowVm> _rows = new List<ResearchRowVm>();

        public ResearchVm(VmContext c)
        {
            _c = c;
            var defs = new List<ResearchDef>(c.Db.Research.Values);
            defs.Sort((a, b) =>
            {
                int r = a.LayoutRow.CompareTo(b.LayoutRow);
                if (r == 0) r = a.LayoutCol.CompareTo(b.LayoutCol);
                return r != 0 ? r : string.CompareOrdinal(a.Id, b.Id);
            });
            foreach (var d in defs) _rows.Add(new ResearchRowVm(c, d));
            LockedLine = c.Label();
            CoachText = c.Label();
            CoachText.Set(UiKeys.ResearchCoachTank);
        }

        /// <summary>The tier has Research rows (MVP); M0 content has none.</summary>
        public static bool InTier(ContentDb db) => db.Research != null && db.Research.Count > 0;

        /// <summary>
        /// Opens SCR_RESEARCH; with <paramref name="toast"/> (a locked Fish Board button) or in a tier without Research,
        /// shows the lock toast naming the condition instead.
        /// </summary>
        public static void Open(VmContext c, bool toast)
        {
            if (toast || !InTier(c.Db)) c.LockedToast(Feature);
            else c.Router?.Push(ScreenIds.Research);
        }

        public IReadOnlyList<ResearchRowVm> Rows => _rows;

        /// <summary>Wallet.FishCoin, floored for display (00 section 6.4).</summary>
        public readonly NumberLabel FishCoin = new NumberLabel();

        /// <summary>Before the RESEARCH gate: the screen is greyed and <see cref="LockedLine"/> names the condition.</summary>
        public readonly Flag Locked = new Flag();
        public readonly TextKeyLabel LockedLine;

        /// <summary>The first-time coach mark on Tank capacity (07 section 4.9), shown once the screen is open and unlocked.</summary>
        public readonly Flag CoachMark = new Flag();
        public readonly TextKeyLabel CoachText;

        public ResearchRowVm Row(string nodeId)
        {
            foreach (var r in _rows) if (r.NodeId == nodeId) return r;
            return null;
        }

        string LockedKey()
        {
            if (_c.Db.FeatureUnlocks != null && _c.Db.FeatureUnlocks.TryGetValue(Feature, out var f) && !string.IsNullOrEmpty(f.LockedTextKey))
                return f.LockedTextKey;
            return UiKeys.ComingSoon;
        }

        public void Refresh(Simulation sim)
        {
            var db = _c.Db;
            var st = sim.State;
            double fc = st.Wallet.FishCoin;
            FishCoin.Set(Math.Floor(fc));
            bool open = db.IsUnlocked(Feature, st);
            Locked.Set(!open);
            LockedLine.Set(open ? null : LockedKey());
            CoachMark.Set(open && _rows.Count > 0 && !_c.Prefs.SeenCoachMark(CoachMarkId));
            int rank = AwakeningRules.HighestRank(db, st);
            foreach (var r in _rows)
            {
                var node = db.Research[r.NodeId];
                int level = ResearchRules.Level(st, node);
                bool atMax = level >= node.MaxLevel;
                double cost = atMax ? 0.0 : node.Cost(level);
                bool afford = !atMax && fc >= cost;
                bool can = sim.Query.CanResearch(r.NodeId);
                r.Level.Set(level);
                r.MaxLevel.Set(node.MaxLevel);
                SetValue(r.CurrentText, node.Effect, ResearchRules.EffectAt(db, node, level));
                if (atMax) r.NextText.Set(UiKeys.Raw, "");
                else SetValue(r.NextText, node.Effect, ResearchRules.EffectAt(db, node, level + 1));
                r.Cost.Set(Math.Ceiling(cost));
                r.CanAfford.Set(afford);
                r.AtMax.Set(atMax);
                r.Buy.Visible.Set(true);
                r.Buy.Enabled.Set(can);
                r.Buy.Label.Set(atMax ? UiKeys.ResearchMax : UiKeys.ResearchBuy);
                if (!open) r.Buy.Reason.Set(LockedKey());
                else if (rank < node.UnlockAwakenRank) r.Buy.Reason.Set(UiKeys.ResearchNodeLocked, node.UnlockAwakenRank);
                else if (atMax) r.Buy.Reason.Set(UiKeys.ResearchMaxLevel);
                else if (!afford) r.Buy.Reason.Set(UiKeys.NeedMore, LabelArg.Num(Math.Ceiling(cost - fc)));
                else r.Buy.Reason.Set(null);
            }
        }

        /// <summary>One effect value in its unit: a percentage bonus, a count, seconds, MP per second or hours.</summary>
        static void SetValue(TextKeyLabel l, ResearchEffect e, double v)
        {
            switch (e)
            {
                case ResearchEffect.RESEARCH_PCT:
                case ResearchEffect.GOLD_MULT: l.Set(UiKeys.Raw, LabelArg.Num(v * 100.0, NumberStyle.PctPoints)); break;
                case ResearchEffect.MP_REGEN: l.Set(UiKeys.ResearchPerSec, LabelArg.Num(v, NumberStyle.Ratio)); break;
                case ResearchEffect.TANK_REFILL: l.Set(UiKeys.Raw, LabelArg.Num(v, NumberStyle.Seconds)); break;
                case ResearchEffect.OFFLINE_CAP: l.Set(UiKeys.ResearchHours, LabelArg.Int((long)Math.Round(v))); break;
                case ResearchEffect.MP_POOL: l.Set(UiKeys.Raw, LabelArg.Num(v)); break;
                default: l.Set(UiKeys.Raw, LabelArg.Int((long)Math.Round(v))); break;   // TANK_CAP, FISH_CAP
            }
        }

        /// <summary>[Buy]: <c>Research(nodeId)</c>, one level; a reject shakes the row's button with its toast.</summary>
        public void OnBuy(string nodeId)
        {
            var r = Row(nodeId);
            if (r == null) return;
            if (nodeId == CoachNode) OnCoachMarkDismissed();
            _c.Commands?.Send(new HeroCat.Sim.Commands.Research(nodeId), r.Anchor);
        }

        /// <summary>A tap anywhere dismisses the coach mark for good (ClientPrefs.SeenCoachMarks).</summary>
        public void OnCoachMarkDismissed()
        {
            _c.Prefs.MarkCoachMark(CoachMarkId);
            CoachMark.Set(false);
        }

        public void OnEvent(Event e)
        {
            if (e is ResearchLeveled l)
            {
                var r = Row(l.NodeId);
                if (r != null) r.FlashSerial++;
                _c.Cues?.Feel(Haptic.Success, Sounds.Upgrade);
            }
        }
    }
}
