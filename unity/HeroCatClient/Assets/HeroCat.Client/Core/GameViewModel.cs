using System;
using System.Collections.Generic;
using System.Text;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.Gacha;
using HeroCat.Sim.State;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// The one object screens talk to (specs/07 section 7.2). Pull for numbers: Refresh() copies State/Query into
    /// the sub-view-models. Push for feedback: OnSimEvent turns events into feed lines, toasts and popups.
    /// Commands call the sim and toast the reject reason. No game rule lives here.
    /// </summary>
    public sealed class GameViewModel
    {
        readonly Simulation _sim;
        readonly EventOutbox _outbox;
        readonly OffsetClock _clock;
        readonly ContentDb _c;
        readonly Constants _k;
        readonly List<SimEvent> _events = new List<SimEvent>(1024);
        readonly Dictionary<string, string> _glyphs = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly List<string> _popups = new List<string>();
        bool _heroesDirty = true;
        bool _summaryMode = false;  // only DebugRunHours sets it; explicit so Release builds do not warn (CS0649)

        public GameViewModel(Simulation sim, EventOutbox outbox, OffsetClock clock, string saveDescription = "")
        {
            _sim = sim ?? throw new ArgumentNullException(nameof(sim));
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _clock = clock;
            _c = sim.Content;
            _k = _c.Constants;
            SaveDescription = saveDescription;

            // Hero glyphs: tier letter + 1-based index within the tier, in content order (C01..M03; section 10.2).
            var perTier = new int[5];
            foreach (var h in _c.HeroList)
            {
                int n = ++perTier[(int)h.Tier];
                _glyphs[h.Id] = h.Tier + n.ToString("00");
            }
            Gacha.RatesText = BuildRatesText(_c.HeroBanner);
            Gear.Slots = new GearSlotRow[_c.GearSlotCount];
            for (int i = 0; i < Gear.Slots.Length; i++) Gear.Slots[i] = new GearSlotRow();
            Refresh();
        }

        /// <summary>Base rates plus the pity-consolidated L+/M rates (specs/07 section 11.6, specs/01 section 3.3).</summary>
        static string BuildRatesText(BannerDef b)
        {
            double survive = 1, expected = 0;
            for (int n = 1; n <= b.PityHard; n++)
            {
                double q = b.PityRate(n);
                expected += n * survive * q;
                survive *= 1 - q;
            }
            double lplus = expected > 0 ? 1 / expected : 0;
            var sb = new StringBuilder("Rates: ");
            for (int t = 0; t < b.Rates.Count; t++)
            {
                if (t > 0) sb.Append("   ");
                sb.Append((Rarity)t).Append(' ').Append((b.Rates[t] * 100).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)).Append('%');
            }
            sb.Append("\nWith pity: L+ ").Append((lplus * 100).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
              .Append("%  (M ").Append((lplus * b.MythicShareOfLplus * 100).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
              .Append("%), expected ").Append(expected.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" pulls per L+.")
              .Append("\nL+ chance rises from pull ").Append(b.PitySoftStart + 1).Append("; guaranteed at ").Append(b.PityHard)
              .Append(". A ").Append(b.GuaranteeBatchSize).Append("-pull guarantees at least one ").Append(b.GuaranteeMinTier).Append('.');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ state for screens

        public readonly HudVm Hud = new HudVm();
        public readonly CombatVm Combat = new CombatVm();
        public readonly StatsVm Stats = new StatsVm();
        public readonly GearVm Gear = new GearVm();
        public readonly HeroesVm Heroes = new HeroesVm();
        public readonly GachaVm Gacha = new GachaVm();
        public readonly ClassVm Class = new ClassVm();
        public readonly EventFeed Feed = new EventFeed(8);
        public readonly ToastQueue Toasts = new ToastQueue();

        public string SaveDescription { get; }
        public string ContentVersion => _c.Version;
        public string ProfileId => _sim.State.ProfileId.ToString();
        public long ClockOffsetSeconds => _clock?.OffsetSeconds ?? 0;
        public long OutboxDropped => _outbox.Dropped;

        /// <summary>Sim seconds per real second; only the debug panel changes it, so Release always runs at 1.</summary>
        public double TimeScale { get; private set; } = 1.0;

        /// <summary>Modal popups (offline earnings, class advance), oldest first.</summary>
        public string CurrentPopup => _popups.Count > 0 ? _popups[0] : null;

        public void DismissPopup() { if (_popups.Count > 0) _popups.RemoveAt(0); }

        public bool Dirty { get; private set; }
        public bool SaveRequested { get; private set; }
        public bool ResetRequested { get; private set; }

        public bool ConsumeDirty() { bool d = Dirty; Dirty = false; return d; }
        public bool ConsumeSaveRequest() { bool s = SaveRequested; SaveRequested = false; return s; }

        public string Glyph(string heroId) => heroId != null && _glyphs.TryGetValue(heroId, out var g) ? g : "--";

        public string HeroName(string heroId) =>
            heroId != null && _c.Heroes.TryGetValue(heroId, out var def) ? _c.Text(def.NameKey) : "?";

        public Rarity HeroTier(string heroId) =>
            heroId != null && _c.Heroes.TryGetValue(heroId, out var def) ? def.Tier : Rarity.C;

        // ------------------------------------------------------------------ events

        /// <summary>Drain the outbox and route every event. Returns the count drained.</summary>
        public int DrainEvents()
        {
            int n = _outbox.Drain(_events);
            for (int i = 0; i < n; i++) OnSimEvent(_events[i]);
            _events.Clear();
            return n;
        }

        public void OnSimEvent(SimEvent e)
        {
            // Never call a command from here (reentrancy rule, specs/07 section 7.2).
            switch (e)
            {
                case EnemyKilled k:
                    if (_summaryMode) break;
                    Feed.Add(k.IsBoss ? "BOSS down on " + k.Stage + ", +" + TextFmt.Num(k.Gold) + " gold"
                                      : "Killed stage " + k.Stage + " mob, +" + TextFmt.Num(k.Gold) + " gold");
                    break;
                case StageAdvanced s:
                    if (_summaryMode) break;
                    Feed.Add("Stage " + s.FromStage + " -> " + s.ToStage);
                    break;
                case BossFailed b:
                    if (_summaryMode) break;
                    Feed.Add("BOSS FAILED on " + b.Stage + " (attempt " + b.FailsOnStage + ")");
                    Toasts.Push("Boss failed on stage " + b.Stage + " - farming. Get stronger, then retry.", 2.5);
                    break;
                case BossCleared bc:
                    if (_summaryMode) break;
                    Feed.Add("Boss " + bc.Stage + " cleared");
                    break;
                case GearDropped g:
                    if (_summaryMode) break;
                    PushGearToast(g);
                    break;
                case HeroPulled _:
                case HeroEquipped _:
                    _heroesDirty = true;
                    break;
                case ClassAdvanced ca:
                    {
                        string name = ca.Advances >= 1 && ca.Advances <= _c.ClassAdvances.Count
                            ? _c.Text(_c.ClassAdvances[ca.Advances - 1].NameKey) : "?";
                        _popups.Add("CLASS ADVANCE!\n" + name + " (advance " + ca.Advances + "/" + _c.ClassAdvances.Count + ")\n" +
                                    "ATK " + TextFmt.Mult(ca.Mult) + " total\n" +
                                    "DPS " + TextFmt.Num(Combat.Dps) + " -> " + TextFmt.Num(_sim.Query.Dps));
                        SaveRequested = true;
                        break;
                    }
                case OfflineGoldCredited o:
                    _popups.Add("Welcome back!\nAway " + TextFmt.Duration(o.ElapsedSec) + ", credited " + TextFmt.Duration(o.CreditedSec) +
                                (o.Capped ? " (cap " + _k.OfflineCapH + "h)" : "") + "\n+" + TextFmt.Num(o.Gold) + " gold");
                    break;
                case DayRolledOver _:
                    if (_summaryMode) break;
                    Toasts.Push("New day: " + _c.HeroBanner.FreePullsPerDay + " free pulls");
                    break;
                case ClockWentBackwards cb:
                    Toasts.Push("Clock went back " + TextFmt.Duration(cb.ByMs / 1000));
                    break;
                case StageCapReached _:
                    if (_summaryMode) break;
                    Toasts.Push("Stage cap reached");
                    break;
                case QuestCompleted q:
                    if (_summaryMode) break;
                    Toasts.Push("+" + q.Gems + " gems: daily quest " + q.QuestId);
                    break;
                case FirstClear fc:
                    if (_summaryMode) break;
                    Feed.Add("+" + fc.Gems + " gems: first clear of boss " + fc.Stage);
                    break;
                case IdleChestClaimed ic:
                    Toasts.Push("+" + ic.Gems + " gems from the idle chest");
                    break;
            }
        }

        void PushGearToast(GearDropped g)
        {
            var p = g.Piece;
            string slot = SlotName(g.Slot);
            string piece = "[" + p.Rarity + "] " + slot + " T" + p.Tier + " " + TextFmt.PctSigned(_sim.Query.GearBasePct(p));
            string text;
            if (g.Equipped)
            {
                text = "Equipped " + piece;
                if (g.Replaced != null)
                    text += "  (was [" + g.Replaced.Rarity + "] T" + g.Replaced.Tier + " +" + g.Replaced.Enhance + ": " +
                            TextFmt.PctSigned(_sim.Query.GearEffPct(g.Replaced)) + ")";
            }
            else text = "Sold " + piece + " (not better), +" + TextFmt.Num(g.SoldFor) + " gold";
            double dur = p.Rarity >= Rarity.L ? 4.0 : p.Rarity == Rarity.E ? 2.5 : 1.5;
            Toasts.Push(text, dur);
        }

        string SlotName(int slot) => slot >= 0 && slot < _c.GearSlots.Count ? _c.Text(_c.GearSlots[slot].NameKey) : "slot " + slot;

        // ------------------------------------------------------------------ refresh (pull numbers)

        public void Refresh()
        {
            var st = _sim.State;
            var q = _sim.Query;

            // HUD
            Hud.Gold = st.Currencies.Gold;
            Hud.Gems = st.Currencies.Gems;
            Hud.Shards = st.Currencies.Shards;
            Hud.FreePullsDaily = st.Currencies.FreePullsDaily;
            Hud.FreePullsBanked = st.Currencies.FreePullsBanked;

            // Combat strip
            var c = Combat;
            int stage = st.Stage.Stage;
            var rule = _c.BossRuleFor(stage);
            c.Stage = stage;
            c.KillsInStage = st.Stage.KillsInStage;
            c.KillsPerStage = _k.KillsPerStage;
            c.BossPhase = st.Stage.Boss;
            c.BossPending = st.Stage.Boss == BossPhase.Pending;
            c.BossFailsOnStage = st.Stage.BossFailsOnStage;
            c.IsBossStage = q.IsBossStage;
            c.Spawning = st.Combat.Phase == CombatPhase.Spawning;
            c.EnemyIsBoss = st.Combat.EnemyIsBoss;
            c.EnemyHp = Math.Max(0, st.Combat.EnemyHp);
            c.EnemyMaxHp = st.Combat.EnemyMaxHp;
            c.BossTimerS = st.Combat.BossTimer / (double)_k.TickHz;
            c.BossTimeMaxS = rule.TimeTicks / (double)_k.TickHz;
            c.Dps = q.Dps;
            c.HitDamage = q.HitDamage;
            c.HitsToKill = q.HitsToKill(c.EnemyIsBoss ? c.EnemyMaxHp : q.Hp(stage));
            c.KillTimeS = q.KillTimeSeconds(stage);
            c.IsWalled = q.IsWalled;
            c.BossClearable = c.IsBossStage && q.BossClearable;
            c.NeedDps = q.BossHp(stage) * _k.TickHz / rule.TimeTicks;
            c.NeedDpsRatio = c.Dps > 0 ? c.NeedDps / c.Dps : 0;
            c.ShowBossButton = c.IsWalled;
            c.ClassIndex = st.Progression.ClassAdvances;
            c.EnemyHue = (float)(((_k.TierOf(stage) / Math.Max(1, _k.TierWidth)) * 23) % 360);
            var eq = st.Heroes.Equipped;
            for (int i = 0; i < c.PartyGlyphs.Length; i++)
                c.PartyGlyphs[i] = i < eq.Length && eq[i] != null ? Glyph(eq[i]) : null;
            c.ChestReady = q.IsChestReady;
            c.ChestReadyInSec = Math.Max(0, q.ChestReadyAtServerSec - q.NowServerSec);
            c.ChestGems = _k.IdleChestGems;
            c.LifetimeKills = st.Lifetime.Kills;
            c.LifetimeBossKills = st.Lifetime.BossKills;
            c.LifetimeBossFails = st.Lifetime.BossFails;
            c.LifetimeDrops = st.Lifetime.GearDrops;
            c.FirstWallStage = st.Lifetime.FirstWallStage;

            // Stats
            double gold = st.Currencies.Gold;
            int lvl = st.Progression.StatLevel;
            Stats.Level = lvl;
            Stats.StatMult = q.StatMult;
            FillStatRow(Stats.One, 1, q.NextStatCost, gold, lvl);
            FillStatRow(Stats.Ten, 10, q.BulkStatCost(10), gold, lvl);
            int m = q.MaxAffordableStatLevels();
            FillStatRow(Stats.Max, m, m > 0 ? q.BulkStatCost(m) : q.NextStatCost, gold, lvl);
            Stats.Max.Affordable = m > 0;

            // Gear
            var slots = st.Gear.Slots;
            for (int i = 0; i < Gear.Slots.Length && i < slots.Length; i++)
            {
                var row = Gear.Slots[i];
                var p = slots[i];
                row.Name = SlotName(i);
                row.IsEmpty = p == null;
                if (p != null)
                {
                    row.Rarity = p.Rarity;
                    row.Tier = p.Tier;
                    row.Enhance = p.Enhance;
                    row.Maxed = p.Enhance >= _k.EnhMax;
                    row.BasePct = q.GearBasePct(p);
                    row.EffPct = q.GearEffPct(p);
                }
                row.SlotMult = q.GearSlotMult(i);
            }
            if (Gear.SelectedSlot < 0 || Gear.SelectedSlot >= slots.Length) Gear.SelectedSlot = 0;
            var sel = slots.Length > 0 ? slots[Gear.SelectedSlot] : null;
            Gear.SelectedEmpty = sel == null;
            Gear.EnhanceMaxed = sel != null && sel.Enhance >= _k.EnhMax;
            Gear.EnhanceCost = q.EnhanceCost;
            Gear.EnhanceChance = sel != null ? q.EnhanceSuccess(sel.Enhance) : 0;
            Gear.EnhanceAffordable = gold >= Gear.EnhanceCost;
            Gear.GearMult = q.GearMult;

            // Heroes (rows rebuilt only when copies/party changed)
            if (_heroesDirty) RebuildHeroRows();
            Heroes.HeroMult = q.HeroMult;
            Heroes.AutoEquipOn = st.Heroes.AutoEquipBest;

            // Gacha
            var b = _c.HeroBanner;
            var g = Gacha;
            g.BannerName = _c.Text(b.NameKey);
            g.PityCounter = q.PityCounter;
            g.PitySoftStart = b.PitySoftStart;
            g.PityHard = b.PityHard;
            g.TotalPulls = st.HeroBanner.TotalPulls;
            g.FreePullsDaily = st.Currencies.FreePullsDaily;
            g.FreePullsBanked = st.Currencies.FreePullsBanked;
            g.FreePullsPerDay = b.FreePullsPerDay;
            g.Gems = st.Currencies.Gems;
            g.NextPullLplus = q.NextPullLplusRate;
            g.CanPull = b.Enabled && q.CanPull;
            int batch = b.GuaranteeBatchSize;
            g.CanPull10 = b.Enabled && (st.Currencies.FreePullsBanked >= batch || st.Currencies.Gems >= (long)b.PullCostGems * batch);
            g.Pull1Label = st.Currencies.FreePullsDaily > 0 ? "Pull x1   FREE (" + st.Currencies.FreePullsDaily + " today)"
                         : st.Currencies.FreePullsBanked > 0 ? "Pull x1   FREE (" + st.Currencies.FreePullsBanked + " banked)"
                         : "Pull x1   " + b.PullCostGems + " gems";
            g.Pull10Label = st.Currencies.FreePullsBanked >= batch ? "Pull x" + batch + "   FREE (" + batch + " banked)"
                          : "Pull x" + batch + "   " + ((long)b.PullCostGems * batch) + " gems";

            // Class
            var adv = _c.ClassAdvances;
            int a = st.Progression.ClassAdvances;
            Class.Advances = a;
            Class.MaxAdvances = adv.Count;
            Class.ClassName = a == 0 ? "Novice" : _c.Text(adv[Math.Min(a, adv.Count) - 1].NameKey);
            Class.ClassMult = q.ClassMult;
            Class.NextStage = a < adv.Count ? adv[a].Stage : 0;
            int prev = a == 0 ? 1 : adv[Math.Min(a, adv.Count) - 1].Stage;
            Class.Progress01 = Class.NextStage > prev ? Clamp01((stage - prev) / (double)(Class.NextStage - prev)) : 1.0;
            if (Class.Milestones.Length != adv.Count)
            {
                Class.Milestones = new ClassMilestone[adv.Count];
                for (int i = 0; i < adv.Count; i++)
                    Class.Milestones[i] = new ClassMilestone { Stage = adv[i].Stage, Name = _c.Text(adv[i].NameKey), Mult = _c.ClassMult(i + 1) };
            }
            for (int i = 0; i < Class.Milestones.Length; i++) Class.Milestones[i].Reached = a > i;
        }

        void FillStatRow(StatRow r, int count, double cost, double gold, int lvl)
        {
            r.Count = count;
            r.Cost = cost;
            r.Affordable = count > 0 && gold >= cost;
            r.DeltaPct = _sim.Query.StatDeltaPct(count);
            r.ResultMult = 1.0 + _k.StatPerLvl * (lvl + count);
        }

        void RebuildHeroRows()
        {
            _heroesDirty = false;
            var list = _c.HeroList;
            if (Heroes.All.Length != list.Count)
            {
                Heroes.All = new HeroRow[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    var h = list[i];
                    Heroes.All[i] = new HeroRow { Id = h.Id, Tier = h.Tier, Glyph = Glyph(h.Id), Name = _c.Text(h.NameKey) };
                }
            }
            var q = _sim.Query;
            var thresholds = _c.HeroLevelThresholds;
            var eq = _sim.State.Heroes.Equipped;
            if (Heroes.Equipped.Length != eq.Length) Heroes.Equipped = new HeroRow[eq.Length];
            for (int s = 0; s < Heroes.Equipped.Length; s++) Heroes.Equipped[s] = null;
            foreach (var row in Heroes.All)
            {
                row.Copies = q.HeroCopies(row.Id);
                row.Level = q.HeroLevel(row.Copies);
                row.NextLevelCopies = row.Level < thresholds.Count ? thresholds[row.Level] : -1;
                row.Pct = q.HeroPct(row.Id);
                row.EquippedSlot = -1;
                for (int s = 0; s < eq.Length; s++)
                    if (eq[s] == row.Id) { row.EquippedSlot = s; Heroes.Equipped[s] = row; }
            }
            Heroes.Version++;
        }

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // ------------------------------------------------------------------ commands

        void Changed() => Dirty = true;

        void ToastReject(Reject r)
        {
            if (r == Sim.Reject.None) return;
            Toasts.Push(RejectText(r), 1.5);
        }

        public static string RejectText(Reject r) => r switch
        {
            Sim.Reject.NotEnoughGold => "Not enough gold",
            Sim.Reject.NotEnoughGems => "Not enough gems",
            Sim.Reject.EmptySlot => "Empty slot",
            Sim.Reject.EnhanceMaxed => "Already at max enhance",
            Sim.Reject.HeroNotOwned => "Hero not owned",
            Sim.Reject.HeroAlreadyEquipped => "Hero already in another slot",
            Sim.Reject.NoBossToAttempt => "No boss to attempt",
            Sim.Reject.BossAlreadyPending => "Boss already incoming",
            Sim.Reject.StageCap => "Stage cap reached",
            Sim.Reject.ChestNotReady => "Chest not ready yet",
            Sim.Reject.BannerDisabled => "Banner disabled",
            Sim.Reject.InvalidCount => "Invalid count",
            _ => r.ToString(),
        };

        public void BuyStat(int count)
        {
            double before = _sim.Query.StatMult;
            bool ok = count == 1 ? _sim.BuyStatLevel().Ok : _sim.BuyStatLevels(count);
            if (!ok) ToastReject(Sim.Reject.NotEnoughGold);
            else Toasts.Push("ATK +" + count + " lv  " + TextFmt.Mult(before) + " -> " + TextFmt.Mult(_sim.Query.StatMult), 1.0);
            Changed();
        }

        public void BuyStatMax()
        {
            int n = _sim.BuyStatLevelsMax();
            if (n <= 0) ToastReject(Sim.Reject.NotEnoughGold);
            else Toasts.Push("Bought " + n + " levels", 1.5);
            Changed();
        }

        public void SelectGearSlot(int slot) { Gear.SelectedSlot = slot; Changed(); }

        public void EnhanceSelected()
        {
            var r = _sim.Enhance(Gear.SelectedSlot);
            if (!r.Ok) ToastReject(r.Reason);
            else Toasts.Push(r.Success ? SlotName(Gear.SelectedSlot) + " +" + r.Level + "!"
                                       : SlotName(Gear.SelectedSlot) + " enhance failed (gold spent)", 1.5);
            Changed();
        }

        public void EnhanceWeakest()
        {
            int s = _sim.Query.WeakestGearSlot();
            if (s < 0) { Toasts.Push("No gear yet", 1.5); return; }
            Gear.SelectedSlot = s;
            EnhanceSelected();
        }

        public void Pull() => DoPull(_sim.Pull());
        public void Pull10() => DoPull(_sim.Pull10());

        void DoPull(PullOutcome o)
        {
            if (!o.Ok) { ToastReject(o.Reason); Changed(); return; }
            var cards = new PullCard[o.Results.Length];
            int[] tiers = new int[5];
            for (int i = 0; i < o.Results.Length; i++)
            {
                var r = o.Results[i];
                tiers[(int)r.Tier]++;
                cards[i] = new PullCard
                {
                    Glyph = Glyph(r.Hero), Name = HeroName(r.Hero), Tier = r.Tier, IsNew = r.IsNew,
                    LevelBefore = r.LevelBefore, LevelAfter = r.LevelAfter, Shards = r.ShardsGained,
                };
            }
            var sb = new StringBuilder();
            for (int t = 4; t >= 0; t--)
            {
                if (tiers[t] == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(tiers[t]).Append(' ').Append(TextFmt.Tier((Rarity)t));
            }
            sb.Append(";  pity ").Append(_sim.Query.PityCounter).Append('/').Append(_c.HeroBanner.PityHard);
            sb.Append(";  paid: ").Append(o.PaidWith == PaidWith.Gems ? o.GemsSpent + " gems" : o.PaidWith.ToString());
            Gacha.LastResults = cards;
            Gacha.LastSummary = sb.ToString();
            Gacha.ResultsVersion++;
            _heroesDirty = true;
            SaveRequested = true;
            Changed();
        }

        public void SelectHero(string id)
        {
            Heroes.SelectedHero = Heroes.SelectedHero == id ? null : id;
            Heroes.Version++;
            Changed();
        }

        /// <summary>Tap a party slot: place the selected hero there, or unequip when nothing is selected.</summary>
        public void TapPartySlot(int slot)
        {
            var sel = Heroes.SelectedHero;
            if (sel != null)
            {
                var r = _sim.EquipHero(slot, sel);
                ToastReject(r.Reason);
                if (r.Ok) Heroes.SelectedHero = null;
            }
            else
            {
                _sim.UnequipHero(slot);
            }
            _heroesDirty = true;
            Changed();
        }

        public void EquipSelectedToEmptySlot()
        {
            var sel = Heroes.SelectedHero;
            if (sel == null) { Toasts.Push("Select a hero first", 1.5); return; }
            var eq = _sim.State.Heroes.Equipped;
            int empty = Array.IndexOf(eq, null);
            if (empty < 0) { Toasts.Push("No empty slot - tap a party slot to replace", 1.5); return; }
            TapPartySlot(empty);
        }

        public void UnequipSelected()
        {
            var sel = Heroes.SelectedHero;
            int slot = sel == null ? -1 : Array.IndexOf(_sim.State.Heroes.Equipped, sel);
            if (slot < 0) { Toasts.Push("Selected hero is not equipped", 1.5); return; }
            _sim.UnequipHero(slot);
            _heroesDirty = true;
            Changed();
        }

        public void AutoEquip()
        {
            int n = _sim.AutoEquipBestHeroes();
            Toasts.Push("Auto-equip on: changed " + n + " slot" + (n == 1 ? "" : "s"), 1.5);
            _heroesDirty = true;
            Changed();
        }

        public void AttemptBoss()
        {
            var r = _sim.AttemptBoss();
            if (r.Ok) Feed.Add("Retrying boss " + _sim.State.Stage.Stage);
            else ToastReject(r.Reason);
            Changed();
        }

        public void ClaimIdleChest()
        {
            ToastReject(_sim.ClaimIdleChest().Reason);
            Changed();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || HEROCAT_CHEATS
        // ------------------------------------------------------------------ debug panel
        // Compiled only where Shell.DebugEnabled is true (specs/07 sections 6.8 and 13); a Release build has no
        // code path that grants currency, sets the stage, moves the clock or changes TimeScale.

        public void DebugGrantGold(double amount) { _sim.Debug.GrantGold(amount); Changed(); }

        /// <summary>Gold worth k times the next stat level's cost.</summary>
        public void DebugGrantGoldStatCosts(double k) { _sim.Debug.GrantGold(_sim.Query.NextStatCost * k); Changed(); }

        public void DebugGrantGems(long gems) { _sim.Debug.GrantGems(gems); Changed(); }

        public void DebugGrantAllHeroes(int copies)
        {
            foreach (var h in _c.HeroList) _sim.Debug.GrantHeroCopies(h.Id, copies);
            Toasts.Push("+" + copies + " copies of every hero", 1.5);
            _heroesDirty = true;
            Changed();
        }

        public void DebugGrantSelectedHero(int copies)
        {
            var sel = Heroes.SelectedHero;
            if (sel == null) { Toasts.Push("Select a hero in the Heroes tab first", 1.5); return; }
            _sim.Debug.GrantHeroCopies(sel, copies);
            _heroesDirty = true;
            Changed();
        }

        public void DebugSetStage(int stage)
        {
            _sim.Debug.SetStage(stage);
            Feed.Add("[debug] stage set to " + _sim.State.Stage.Stage);
            Changed();
        }

        public void DebugAddStages(int delta) => DebugSetStage(_sim.State.Stage.Stage + delta);

        /// <summary>Run N simulated hours synchronously, then toast one summary line (specs/07 section 11.8).</summary>
        public void DebugRunHours(double hours)
        {
            var st = _sim.State;
            int stage0 = st.Stage.Stage;
            long kills0 = st.Lifetime.Kills, drops0 = st.Lifetime.GearDrops, fails0 = st.Lifetime.BossFails;
            double gold0 = st.Lifetime.GoldEarned;
            DrainEvents();
            _summaryMode = true;
            try
            {
                _sim.Advance((long)(hours * 3600 * _k.TickHz));
                DrainEvents();
            }
            finally { _summaryMode = false; }
            // The sim's virtual clock is now N hours ahead of the wall clock; move the debug clock with it so the next
            // 60 s sync neither reports ClockWentBackwards nor credits the same window as offline gold.
            if (_clock != null)
            {
                _clock.OffsetSeconds += (long)(hours * 3600);
                _sim.SyncWallClock(_clock, applyCatchUp: false);
            }
            Toasts.Push(hours + "h: " + (st.Lifetime.Kills - kills0) + " kills, " + (st.Lifetime.GearDrops - drops0) + " drops, stages " +
                        stage0 + " -> " + st.Stage.Stage + ", " + (st.Lifetime.BossFails - fails0) + " walls, +" +
                        TextFmt.Num(st.Lifetime.GoldEarned - gold0) + " gold", 4.0);
            _heroesDirty = true;
            Changed();
        }

        /// <summary>Move the wall clock forward and sync immediately so the offline popup shows (specs/07 section 11.8).</summary>
        public void DebugClockSkip(double hours)
        {
            if (_clock == null) return;
            _clock.OffsetSeconds += (long)(hours * 3600);
            _sim.SyncWallClock(_clock);
            Changed();
        }

        public void DebugSetTimeScale(double scale) { TimeScale = scale <= 0 ? 1 : scale; Changed(); }

        public void DebugValidate()
        {
            try { _sim.Validate(); Toasts.Push("Validate: OK", 1.5); }
            catch (InvariantViolationException e) { Toasts.Push("Validate FAILED: " + e.Message, 4.0); }
        }

        public void DebugRequestSave() { SaveRequested = true; Toasts.Push("Saved (" + SaveDescription + ")", 1.5); }

        public void DebugResetProfile() { ResetRequested = true; }
#endif
    }
}
