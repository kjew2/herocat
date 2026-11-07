using System;
using System.Collections.Generic;
using System.Text;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Client.Core
{
    /// <summary>A quest's three states as the screens see them (00 decision 17; screens never read HeroCat.Sim.State).</summary>
    public enum QuestRowState { InProgress, Completed, Claimed }

    /// <summary>
    /// One SCR_QUESTS row (07 section 4.15): a daily or weekly mission, or an achievement ladder's lowest unclaimed rung.
    /// The identity fields are fixed for the row's life; the change-only fields follow the sim.
    /// </summary>
    public sealed class QuestRowVm
    {
        internal QuestRowVm(QuestDef def, ILocalizer loc, string goScreen, int ladderRungs)
        {
            Def = def;
            GoScreen = goScreen;
            LadderRungs = ladderRungs;
            Rungs = new TextKeyLabel(loc);
        }

        internal QuestDef Def { get; }
        public string Id => Def.Id;
        public string TitleKey => Def.NameKey;
        /// <summary>07's <c>TitleArgs</c>: the target, for a name key that shows it.</summary>
        public long Target => Def.Target;
        public Currency RewardIcon => Def.RewardCurrency;
        public long RewardAmount => Def.RewardAmount;

        /// <summary>A counting row that pays nothing (d_login, issue #20): it shows "done", never a Claim button.</summary>
        public bool CountsOnly => Def.RewardAmount == 0;

        /// <summary>The deep link of an IN_PROGRESS row (<c>[Go &gt;]</c>); null when no screen advances it (log in).</summary>
        public string GoScreen { get; }

        /// <summary>Achievement rows: the ladder's rung count ("3 / 11 rungs"); 0 on a mission.</summary>
        public int LadderRungs { get; }

        /// <summary>The counter, which keeps counting past <see cref="Target"/> (06 AC 8); the bar and label clamp it.</summary>
        public readonly IntLabel Progress = new IntLabel();
        public readonly BarFill Fill = new BarFill();
        public readonly Bindable<QuestRowState> State = new Bindable<QuestRowState>();

        /// <summary>The amber "expires soon" caption: COMPLETED and within UI_QUEST_EXPIRY_WARN_SEC of its kind's rollover.</summary>
        public readonly Flag ExpiresSoon = new Flag();

        /// <summary>"3 / 11 rungs" on an achievement row (06 section 5.4); empty on a mission.</summary>
        public readonly TextKeyLabel Rungs;
    }

    /// <summary>A bonus chip of the daily or weekly progress bar (d_bonus_5, d_bonus_8; w_bonus_all_*): tap claims it.</summary>
    public sealed class QuestBonusVm
    {
        internal QuestBonusVm(QuestDef def) { Def = def; }

        internal QuestDef Def { get; }
        public string Id => Def.Id;
        /// <summary>The missions it needs (5, 8; 7 for the weekly chips).</summary>
        public long Threshold => Def.Target;
        public Currency RewardIcon => Def.RewardCurrency;
        public long RewardAmount => Def.RewardAmount;
        public readonly Bindable<QuestRowState> State = new Bindable<QuestRowState>();
    }

    /// <summary>
    /// SCR_QUESTS (07 section 4.15, 06 section 5, 00 decision 17; issue #183). Three tabs with a COMPLETED-count badge
    /// each; on Daily and Weekly a progress bar (missions done / missions on the tab) with the bonus rows as chips, and one
    /// row per mission; on Achievements one row per ladder (its lowest unclaimed rung, <c>Simulation.AchievementRows()</c>).
    /// Claim sends <c>ClaimQuest(id)</c>, Claim all <c>ClaimAllQuests(tab)</c>, whose <c>QuestsBulkClaimed</c> toasts the
    /// summed rewards. A rollover rebuilds every row and, while the screen is open, toasts "Missions reset". Completed daily
    /// (weekly) rows show the amber expiry caption in the last <see cref="UiConstants.QuestExpiryWarnSec"/> before the day
    /// (week) rollover, read from the sim's virtual server time, because the rollover forfeits them. Before the QUESTS
    /// feature_unlocks condition the screen is Locked (greyed, condition text, no interaction).
    /// </summary>
    public sealed class QuestsVm
    {
        public const string AnchorClaimAll = "quests.claim_all";
        public static string AnchorClaim(string id) => "quests.claim." + id;

        const string Feature = "QUESTS";
        static readonly QuestKind[] Kinds = { QuestKind.DAILY, QuestKind.WEEKLY, QuestKind.ACHIEVEMENT };

        readonly VmContext _c;
        readonly List<QuestRowVm> _rows = new List<QuestRowVm>();
        readonly List<QuestBonusVm> _bonus = new List<QuestBonusVm>();
        readonly List<string> _rowIds = new List<string>();
        readonly List<string> _scratchIds = new List<string>();
        bool _rebuild = true;

        public QuestsVm(VmContext c)
        {
            _c = c;
            for (int i = 0; i < TabBadges.Length; i++) TabBadges[i] = new BadgeDot();
            LockedText = c.Label();
            ProgressLabel = c.Label();
            ClaimAll = c.Button();
        }

        /// <summary>The visible tab (07: DAILY, WEEKLY, ACHIEVEMENT).</summary>
        public readonly Bindable<QuestKind> Tab = new Bindable<QuestKind>();

        /// <summary>Per <see cref="QuestKind"/> ordinal: count of COMPLETED quests on that tab (bonus rows and rungs included).</summary>
        public readonly BadgeDot[] TabBadges = new BadgeDot[3];

        public readonly Flag Locked = new Flag();
        /// <summary>The unlock condition from content (<c>feature.quests.locked</c>: "Unlocks at stage 10").</summary>
        public readonly TextKeyLabel LockedText;

        /// <summary>Daily and Weekly only: the progress bar.</summary>
        public readonly Flag ProgressVisible = new Flag();
        /// <summary>"Daily progress 8 / 11" (07's <c>DailyCompletedCount / 11</c>; Weekly: <c>WeeklyCompletedCount / 7</c>).</summary>
        public readonly TextKeyLabel ProgressLabel;
        public readonly IntLabel CompletedCount = new IntLabel();
        public readonly IntLabel MissionCount = new IntLabel();
        public readonly BarFill ProgressFill = new BarFill();

        /// <summary>"Claim all (4)": visible and enabled while the tab has a COMPLETED quest.</summary>
        public readonly ButtonState ClaimAll;

        /// <summary>The tab's rows; rebuilt (new instances) when <see cref="RowsVersion"/> changes, otherwise updated in place.</summary>
        public IReadOnlyList<QuestRowVm> Rows => _rows;
        /// <summary>The tab's bonus chips (empty on Achievements); rebuilt with <see cref="Rows"/>.</summary>
        public IReadOnlyList<QuestBonusVm> Bonus => _bonus;
        public int RowsVersion { get; private set; }

        /// <summary>The screen was shown, or the tab or day changed: rebuild the rows on the next refresh.</summary>
        public void Invalidate() => _rebuild = true;

        // ------------------------------------------------------------------------------------------ refresh

        public void Refresh(Simulation sim)
        {
            var st = sim.State;
            bool locked = !_c.Db.IsUnlocked(Feature, st);
            Locked.Set(locked);
            string lockKey = null;
            if (_c.Db.FeatureUnlocks != null && _c.Db.FeatureUnlocks.TryGetValue(Feature, out var f)) lockKey = f.LockedTextKey;
            LockedText.Set(locked && !string.IsNullOrEmpty(lockKey) ? lockKey : UiKeys.Raw);

            for (int i = 0; i < Kinds.Length; i++) TabBadges[i].Set(ClaimableCount(sim, Kinds[i]));

            var tab = Tab.Value;
            if (_rebuild || RowIdsChanged(sim, tab)) Rebuild(sim, tab);

            long now = sim.VirtualNowServerSeconds;
            bool soon = ExpiresSoon(now, _c.K.DaySeconds, tab, UiConstants.QuestExpiryWarnSec);
            foreach (var r in _rows)
            {
                var state = sim.QuestStateOf(r.Def);
                long p = sim.QuestProgressOf(r.Def);
                r.State.Set(ToRow(state));
                r.Progress.Set(Math.Min(p, r.Target));
                r.Fill.Set(Math.Min(p, r.Target), r.Target);
                r.ExpiresSoon.Set(soon && state == QuestStatus.COMPLETED);
                if (r.LadderRungs > 0) r.Rungs.Set(UiKeys.QuestsRungs, sim.ClaimedRungs(r.Def.Ladder), r.LadderRungs);
            }
            foreach (var b in _bonus) b.State.Set(ToRow(sim.QuestStateOf(b.Def)));

            bool missions = tab != QuestKind.ACHIEVEMENT;
            ProgressVisible.Set(missions);
            if (missions)
            {
                int done = 0, total = 0;
                foreach (var def in _c.Db.QuestsOf(tab))
                {
                    if (IsBonus(def)) continue;
                    total++;
                    if (sim.QuestStateOf(def) != QuestStatus.IN_PROGRESS) done++;
                }
                CompletedCount.Set(done);
                MissionCount.Set(total);
                ProgressFill.Set(done, total);
                ProgressLabel.Set(tab == QuestKind.DAILY ? UiKeys.QuestsDailyProgress : UiKeys.QuestsWeeklyProgress, done, total);
            }

            int claimable = TabBadges[(int)tab].Count;
            ClaimAll.Visible.Set(claimable > 0);
            ClaimAll.Enabled.Set(claimable > 0 && !locked);
            ClaimAll.Label.Set(UiKeys.QuestsClaimAll, claimable);
            ClaimAll.Reason.Set(null);
        }

        void Rebuild(Simulation sim, QuestKind tab)
        {
            _rebuild = false;
            _rows.Clear();
            _bonus.Clear();
            _rowIds.Clear();
            if (tab == QuestKind.ACHIEVEMENT)
            {
                foreach (var def in sim.AchievementRows())
                {
                    _c.Db.AchievementLadders.TryGetValue(def.Ladder, out var rungs);
                    _rows.Add(new QuestRowVm(def, _c.Loc, GoScreenOf(def.Goal), rungs?.Count ?? 0));
                    _rowIds.Add(def.Id);
                }
            }
            else
            {
                foreach (var def in _c.Db.QuestsOf(tab))
                {
                    if (IsBonus(def)) { _bonus.Add(new QuestBonusVm(def)); continue; }
                    _rows.Add(new QuestRowVm(def, _c.Loc, GoScreenOf(def.Goal), 0));
                    _rowIds.Add(def.Id);
                }
            }
            RowsVersion++;
        }

        /// <summary>Only the Achievements tab changes rows in place (a claimed rung is replaced by the next one).</summary>
        bool RowIdsChanged(Simulation sim, QuestKind tab)
        {
            if (tab != QuestKind.ACHIEVEMENT) return false;
            _scratchIds.Clear();
            foreach (var def in sim.AchievementRows()) _scratchIds.Add(def.Id);
            if (_scratchIds.Count != _rowIds.Count) return true;
            for (int i = 0; i < _rowIds.Count; i++) if (_scratchIds[i] != _rowIds[i]) return true;
            return false;
        }

        // ------------------------------------------------------------------------------------------ rules (engine-free, tested)

        public static QuestRowState ToRow(QuestStatus s) =>
            s == QuestStatus.CLAIMED ? QuestRowState.Claimed : s == QuestStatus.COMPLETED ? QuestRowState.Completed : QuestRowState.InProgress;

        /// <summary>The bonus rows: they count missions on their own tab and are drawn as the bar's chips (06 sections 5.2-5.3).</summary>
        public static bool IsBonus(QuestDef def) =>
            (def.Kind == QuestKind.DAILY && def.Goal == QuestGoal.DAILIES_COMPLETED) ||
            (def.Kind == QuestKind.WEEKLY && def.Goal == QuestGoal.WEEKLIES_COMPLETED);

        /// <summary>COMPLETED (claimable) quests of <paramref name="kind"/>: the tab badge and the Claim all count.</summary>
        public static int ClaimableCount(Simulation sim, QuestKind kind)
        {
            var map = kind == QuestKind.DAILY ? sim.State.Quests.Daily : kind == QuestKind.WEEKLY ? sim.State.Quests.Weekly : sim.State.Quests.Achievement;
            int n = 0;
            foreach (var def in sim.Db.QuestsOf(kind))
                if (map.TryGetValue(def.Id, out var e) && e != null && e.State == QuestStatus.COMPLETED) n++;
            return n;
        }

        /// <summary>Seconds from <paramref name="nowServer"/> to the rollover that resets <paramref name="kind"/>; null for achievements (never reset).</summary>
        public static long? SecondsToRollover(long nowServer, int daySeconds, QuestKind kind)
        {
            int day = DayKeys.DayKey(nowServer, daySeconds);
            if (kind == QuestKind.DAILY) return DayKeys.DayStart(day + 1, daySeconds) - nowServer;
            if (kind == QuestKind.WEEKLY)
            {
                int nextWeekDay = DayKeys.FirstMondayDayKey + (DayKeys.WeekKey(day) + 1) * DayKeys.DaysPerWeek;
                return DayKeys.DayStart(nextWeekDay, daySeconds) - nowServer;
            }
            return null;
        }

        /// <summary>07 section 4.15's forfeit warning: within <paramref name="warnSec"/> of the rollover that forfeits the tab's COMPLETED rows.</summary>
        public static bool ExpiresSoon(long nowServer, int daySeconds, QuestKind kind, int warnSec) =>
            SecondsToRollover(nowServer, daySeconds, kind) is long left && left <= warnSec;

        /// <summary>The <c>[Go &gt;]</c> target for a goal: the screen where the player advances it; null when none does.</summary>
        public static string GoScreenOf(QuestGoal goal)
        {
            switch (goal)
            {
                case QuestGoal.KILL_ENEMIES:
                case QuestGoal.ATTEMPT_BOSS:
                case QuestGoal.CLEAR_STAGES:
                case QuestGoal.REACH_STAGE:
                case QuestGoal.RELEASE_FISH:
                case QuestGoal.MERGE_FISH:
                case QuestGoal.LIFETIME_MERGES:
                case QuestGoal.FISH_LEVEL:
                    return ScreenIds.Main;
                case QuestGoal.SUMMON:
                case QuestGoal.LIFETIME_SUMMONS:
                case QuestGoal.OWN_WEAPONS:
                case QuestGoal.OWN_SKILLS:
                case QuestGoal.OWN_COMPANIONS:
                    return ScreenIds.Summon;
                case QuestGoal.TRAIN_LEVELS:
                case QuestGoal.REACH_PARTY_LEVEL:
                case QuestGoal.AWAKEN_RANK:
                    return ScreenIds.Party;
                case QuestGoal.CLEAR_DUNGEON:
                    return ScreenIds.Dungeons;
                default:   // LOGIN, LOGIN_DAYS, DAILIES_COMPLETED, WEEKLIES_COMPLETED, WATCH_AD
                    return null;
            }
        }

        // ------------------------------------------------------------------------------------------ input

        public void OnTab(QuestKind tab)
        {
            if (Tab.Set(tab)) _rebuild = true;
        }

        public ApplyResult OnClaim(string questId)
        {
            if (Locked.Value) { _c.LockedToast(Feature); return ApplyResult.Reject(RejectCode.Locked, QuestRejectReasons.QUESTS_LOCKED); }
            return _c.Commands?.Send(new ClaimQuest(questId), AnchorClaim(questId)) ?? ApplyResult.Accepted;
        }

        public ApplyResult OnClaimAll()
        {
            if (Locked.Value) { _c.LockedToast(Feature); return ApplyResult.Reject(RejectCode.Locked, QuestRejectReasons.QUESTS_LOCKED); }
            return _c.Commands?.Send(new ClaimAllQuests(Tab.Value), AnchorClaimAll) ?? ApplyResult.Accepted;
        }

        /// <summary><c>[Go &gt;]</c>: SCR_MAIN pops to the root; a Full screen replaces this one; a screen this build lacks toasts its lock.</summary>
        public void OnGo(QuestRowVm row)
        {
            var id = row?.GoScreen;
            if (id == null || _c.Router == null) return;
            if (id == ScreenIds.Dungeons) { _c.LockedToast("DUNGEONS"); return; }   // TODO(gap: SCR_DUNGEONS client screen): route once it exists
            _c.Router.Replace(id);
        }

        // ------------------------------------------------------------------------------------------ events

        public void OnEvent(Event e, bool open)
        {
            switch (e)
            {
                case QuestsBulkClaimed b:
                    _c.Toasts?.Push(SummaryText(_c.Loc, b.Totals), ToastStyle.Reward);
                    break;
                case DayRolledOver _:
                case WeekRolledOver _:
                    _rebuild = true;
                    if (open && e is DayRolledOver) _c.Toast(UiKeys.QuestsToastReset);   // one toast when both roll at once
                    break;
            }
        }

        /// <summary>"+140 Diamonds" (one line per currency, comma-joined), the Claim all summary toast.</summary>
        public static string SummaryText(ILocalizer loc, IReadOnlyList<CurrencyDelta> totals)
        {
            var sb = new StringBuilder();
            if (totals != null)
                foreach (var t in totals)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(loc.Format(UiKeys.QuestsToastClaimed, TextFmt.IntText((long)t.Amount), loc.Text(UiKeys.CurrencyKey(t.Currency))));
                }
            return sb.ToString();
        }
    }
}
