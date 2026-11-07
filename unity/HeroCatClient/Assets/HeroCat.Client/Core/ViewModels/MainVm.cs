using System;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// The HUD bar (07 sections 2 and 4.3; issue #111): wallet chips, avatar, inbox and settings. Shown on every Full
    /// screen. Chip taps route per section 2 (gold opens SCR_PARTY, diamonds SCR_SHOP, Fish Coins SCR_RESEARCH, Rock Candy
    /// SCR_PARTY); targets this build does not have show a locked toast.
    /// </summary>
    public sealed class HudVm
    {
        readonly VmContext _c;

        public HudVm(VmContext c) { _c = c; }

        public readonly NumberLabel Gold = new NumberLabel();
        public readonly IntLabel Diamonds = new IntLabel();

        /// <summary>FISH_COIN is a double; the UI floors the display (00 section 6.4, issue #16).</summary>
        public readonly NumberLabel FishCoin = new NumberLabel();

        public readonly IntLabel RockCandy = new IntLabel();

        /// <summary>Shown once Progress.HighestCleared reaches AWAKEN_STAGES[0] (07 section 4.3, MVP row).</summary>
        public readonly Flag RockCandyVisible = new Flag();

        public readonly IntLabel PartyLevel = new IntLabel();
        public readonly BadgeDot Inbox = new BadgeDot();

        public void Refresh(Simulation sim, in Snapshot s)
        {
            Gold.Set(s.Gold);
            Diamonds.Set(s.Diamonds);
            FishCoin.Set(Math.Floor(s.FishCoin));
            RockCandy.Set(s.RockCandy);
            var aw = _c.Db.Awakening;
            RockCandyVisible.Set(aw != null && aw.Count > 0 && sim.State.Progress.HighestCleared >= aw[0].StageGate);
            PartyLevel.Set(s.PartyLevel);
            Inbox.Set(s.PendingInboxCount);
        }

        /// <summary>Gold pulses on BOSS / OFFLINE gains only, never per kill; diamonds pulse on any increase (07 section 4.3).</summary>
        public void OnCurrency(CurrencyChanged c)
        {
            if (c.Delta <= 0) return;
            switch (c.Currency)
            {
                case Currency.GOLD:
                    if (c.Reason == CurrencyReason.BOSS || c.Reason == CurrencyReason.OFFLINE) _c.Cues?.Push(CueKind.GoldPulse, value: c.Delta);
                    break;
                case Currency.DIAMOND:
                    _c.Cues?.Push(CueKind.DiamondPulse, value: c.Delta);
                    break;
                case Currency.FISH_COIN:
                    if (c.Reason != CurrencyReason.FISH_MERGE) _c.Cues?.Push(CueKind.FishCoinPulse, value: c.Delta);
                    break;
            }
        }

        public void OnGoldChip() => _c.Router?.Replace(ScreenIds.Party);
        public void OnDiamondChip() => _c.LockedToast("SHOP");
        /// <summary>Opens SCR_RESEARCH even before its gate (the screen shows its Locked state; 07 section 4.3).</summary>
        public void OnFishCoinChip() => ResearchVm.Open(_c, toast: false);
        public void OnRockCandyChip() => _c.Router?.Replace(ScreenIds.Party);
        public void OnAvatar() => _c.Toast(UiKeys.ComingSoon, ToastStyle.Reject);
        public void OnInbox() => _c.Toast(UiKeys.ComingSoon, ToastStyle.Reject);
        public void OnSettings() => _c.Toast(UiKeys.ComingSoon, ToastStyle.Reject);
    }

    /// <summary>The six tabs (07 section 2): Party, Collection, Summon, Dungeons, Quests, Shop.</summary>
    public enum MainTab { Party, Collection, Summon, Dungeons, Quests, Shop }

    /// <summary>
    /// The SCR_MAIN combat band (07 section 4.3; issue #113): stage label, kill counter, WALL badge, enemy HP and boss
    /// timer, the two-row party formation (00 decision 110: slots 1-3 in front, 4-5 behind; each with its companion, weapon,
    /// skill, MP bar and Awakening aura), DPS readout (at most 2 Hz), the Tap Zap throttle and the pooled damage numbers. Every value comes from the per-tick snapshot or the drained events (00 contract 8).
    /// </summary>
    public sealed class CombatVm
    {
        public const string AnchorBoss = "main.boss";

        readonly VmContext _c;
        double _lastDps = double.NegativeInfinity;
        bool _failToastPending;
        int _lastFailStage;

        public CombatVm(VmContext c)
        {
            _c = c;
            var k = c.K;
            Taps = new TapZapThrottle(k.TapZapMaxPerSec);
            int n = k.PartySlots;
            Skills = new Bindable<SlotLook>[n];
            Companions = new Bindable<SlotLook>[n];
            WeaponIds = new Bindable<string>[n];
            AwakenRanks = new Bindable<int>[n];
            SlotMp = new BarFill[n];
            for (int i = 0; i < n; i++)
            {
                Skills[i] = new Bindable<SlotLook>();
                Companions[i] = new Bindable<SlotLook>();
                WeaponIds[i] = new Bindable<string>();
                AwakenRanks[i] = new Bindable<int>();
                SlotMp[i] = new BarFill();
            }
            StageLabel = c.Label();
            Boss = c.Button();
        }

        public readonly IntLabel Stage = new IntLabel();
        public readonly IntLabel Chapter = new IntLabel();

        /// <summary>"Stage 100 · Ch.2", or "Farming 95 · frontier 100" on a side stage (issue #35).</summary>
        public readonly TextKeyLabel StageLabel;

        public readonly IntLabel Kills = new IntLabel();
        public readonly IntLabel KillsPerStage = new IntLabel();

        /// <summary>Kill counter red, WALL badge shown.</summary>
        public readonly Flag Walled = new Flag();

        public readonly Flag EnemyIsBoss = new Flag();
        public readonly Flag EnemyVisible = new Flag();
        public readonly NumberLabel EnemyHp = new NumberLabel();
        public readonly NumberLabel EnemyMaxHp = new NumberLabel();
        public readonly BarFill EnemyHpFill = new BarFill();

        /// <summary>Placeholder art (07 section 15.1): hue = (chapter * 23) mod 360.</summary>
        public readonly Bindable<float> EnemyHue = new Bindable<float>();

        public readonly Flag BossTimerVisible = new Flag();
        public readonly BarFill BossTimerFill = new BarFill();
        public readonly NumberLabel BossTimer = new NumberLabel(NumberStyle.BossTimer);

        /// <summary>Under 5 s the bar turns <c>bad</c>.</summary>
        public readonly Flag BossTimerBad = new Flag();

        public readonly NumberLabel Dps = new NumberLabel();
        /// <summary>Per party slot (slot 1 first): the skill (cooldown ring), the companion (ability cooldown), the equipped
        /// weapon (an id only; no held sprite is drawn, 00 decision 113), the Awakening aura and the slot's own MP bar (03 section 2.4).</summary>
        public readonly Bindable<SlotLook>[] Skills;
        public readonly Bindable<SlotLook>[] Companions;
        public readonly Bindable<string>[] WeaponIds;
        public readonly Bindable<int>[] AwakenRanks;
        public readonly BarFill[] SlotMp;

        /// <summary>The formation row of a slot (07 section 4.3, 00 decision 110): 0 front (slots 1-3), 1 back (slots 4-5). Presentation only.</summary>
        public static int RowOf(int slot) => slot < 3 ? 0 : 1;

        /// <summary>The action strip's Boss button: visible only while walled (07 sections 2, 4.3).</summary>
        public readonly ButtonState Boss;

        /// <summary>Green border when the boss is clearable at the current DPS.</summary>
        public readonly Flag BossClearable = new Flag();

        public readonly NumberLabel NeedDpsRatio = new NumberLabel(NumberStyle.Ratio);
        public readonly TapZapThrottle Taps;
        public readonly DamageNumberPool DamageNumbers = new DamageNumberPool();

        public bool IsBoss => EnemyIsBoss.Value;

        public void Refresh(Simulation sim, in Snapshot s, double now)
        {
            var st = sim.State;
            var k = _c.K;
            Stage.Set(s.Stage);
            Chapter.Set(s.Chapter);
            int? sel = s.SelectedStage;
            if (sel.HasValue) StageLabel.Set(UiKeys.MainStageFarming, sel.Value, s.Stage);
            else StageLabel.Set(UiKeys.MainStage, s.Stage, s.Chapter);
            Kills.Set(s.KillsInStage);
            KillsPerStage.Set(k.KillsPerStage);
            bool walled = s.BossPhase == BossPhase.Walled;
            Walled.Set(walled);

            var f = st.Combat.Fight;
            bool fighting = f != null && f.Phase == FightPhase.Fighting;
            bool boss = fighting && f.EnemyIsBoss;
            EnemyVisible.Set(fighting);
            EnemyIsBoss.Set(boss);
            EnemyHp.Set(s.EnemyHp);
            EnemyMaxHp.Set(s.EnemyHpMax);
            EnemyHpFill.Set(s.EnemyHp, s.EnemyHpMax);
            EnemyHue.Set(Palette.EnemyHue(s.Chapter));

            BossTimerVisible.Set(boss);
            BossTimerFill.Set(boss ? s.BossTicksLeft : 0, k.BossTimeTicks);
            double left = boss ? s.BossTicksLeft / (double)k.TickHz : 0;
            BossTimer.Set(left);
            BossTimerBad.Set(boss && left < UiConstants.BossTimerWarnSec);

            if (now - _lastDps >= UiConstants.DpsRefreshSec || double.IsNegativeInfinity(_lastDps))
            {
                _lastDps = now;
                Dps.Set(s.PartyDps);
            }

            var party = st.Party.Slots;
            var cds = st.Combat.SkillCooldowns;
            for (int i = 0; i < Skills.Length; i++)
            {
                var x = i < party.Length ? party[i] : null;
                WeaponIds[i].Set(x?.Weapon);
                AwakenRanks[i].Set(s.Slot(i).AwakenRank);
                SlotMp[i].Set(s.Slot(i).Mp, s.MpMax);
                string id = x?.Skill;
                if (id == null || !_c.Db.Skills.TryGetValue(id, out var def)) { Skills[i].Set(default); continue; }
                int cd = cds != null && i < cds.Length ? cds[i] : 0;
                float pct = def.CooldownTicks > 0 ? (float)Math.Round(Math.Min(1.0, cd / (double)def.CooldownTicks) * 100) / 100f : 0f;
                st.Skills.Items.TryGetValue(id, out var owned);
                Skills[i].Set(new SlotLook(id, def.Icon, def.Rarity, owned != null ? _c.Db.DupeLevel(owned.Copies) : 0, pct));
            }
            var ccds = st.Combat.CompanionCooldowns;
            for (int i = 0; i < Companions.Length; i++)
            {
                string id = i < party.Length ? party[i]?.Companion : null;
                if (id == null || !_c.Db.Companions.TryGetValue(id, out var cdef)) { Companions[i].Set(default); continue; }
                int cd = ccds != null && i < ccds.Length ? ccds[i] : 0;
                float pct = cdef.CooldownTicks > 0 ? (float)Math.Round(Math.Min(1.0, cd / (double)cdef.CooldownTicks) * 100) / 100f : 0f;
                st.Companions.Items.TryGetValue(id, out var owned);
                Companions[i].Set(new SlotLook(id, cdef.Icon, cdef.Rarity, owned != null ? _c.Db.DupeLevel(owned.Copies) : 0, pct));
            }

            // Boss button: "Boss: need ~2.3x" or "Boss: clearable" (green border).
            Boss.Visible.Set(walled);
            Boss.Enabled.Set(walled);
            bool clearable = walled && sim.Query.BossClearable(s.Stage);
            BossClearable.Set(clearable);
            double dps = s.PartyDps;
            double need = dps > 0 ? k.BossHpMult * sim.Query.EnemyHp(s.Stage) / (dps * k.BossTimeTicks / k.TickHz) : double.PositiveInfinity;
            NeedDpsRatio.Set(walled ? need : 0);
            if (clearable) Boss.Label.Set(UiKeys.MainBossClearable);
            else Boss.Label.Set(UiKeys.MainBossNeed, LabelArg.Num(walled ? need : 0, NumberStyle.Ratio));
            Boss.Reason.Set(null);
            if (_failToastPending)
            {
                _failToastPending = false;
                double r = dps > 0 ? k.BossHpMult * sim.Query.EnemyHp(_lastFailStage) / (dps * k.BossTimeTicks / k.TickHz) : double.PositiveInfinity;
                _c.Toasts?.Push(_c.Loc.Format(UiKeys.ToastBossFailed, TextFmt.Ratio(r)), ToastStyle.Info);
            }
        }

        // ------------------------------------------------------------------------------------------ input

        /// <summary>A tap on the viewport (07 section 4.3): true when it counts; false still plays the tap VFX.</summary>
        public bool Tap(double now) => Taps.Tap(now);

        /// <summary>Once per frame: the frame's accepted taps as one TapZap(count); nothing when there were none.</summary>
        public void FlushTaps()
        {
            int n = Taps.Flush();
            if (n > 0) _c.Commands?.Send(new TapZap(n));
        }

        public void OnBoss() => _c.Commands?.Send(new AttemptBoss(), AnchorBoss);

        /// <summary>Stage label tap: SCR_STAGE_SELECT is MVP, a no-op in M0 (07 section 4.3).</summary>
        public void OnStageLabel() { }

        /// <summary>Skill icon tap pushes SCR_ITEM_DETAIL for that skill.</summary>
        public void OnSkillIcon(int slot, ItemDetailVm detail)
        {
            if (slot < 0 || slot >= Skills.Length) return;
            var id = Skills[slot].Value.ItemId;
            if (id == null) { _c.Router?.Replace(ScreenIds.Skills); return; }
            detail?.Open(Banner.SKILL, id);
        }

        // ------------------------------------------------------------------------------------------ events

        public void OnEvent(Event e, double now)
        {
            switch (e)
            {
                case EnemySpawned sp:
                    _c.Cues?.Push(CueKind.EnemySpawned, level: sp.Stage, value: sp.MaxHp, id: sp.IsBoss ? "boss" : null);
                    if (sp.IsBoss) _c.Cues?.Feel(Haptic.Medium, Sounds.BossEnter);
                    break;
                case HitLanded h:
                    DamageNumbers.Add(h.Damage, h.IsCrit, DamageSource.Attack, h.Tick, now);
                    if (h.IsCrit) _c.Cues?.Feel(Haptic.None, Sounds.HitCrit);
                    break;
                case FishMissileHit m:
                    DamageNumbers.Add(m.Damage, false, DamageSource.Fish, m.Tick, now);
                    break;
                case TapZapped t:
                    if (t.Damage > 0) DamageNumbers.Add(t.Damage, false, DamageSource.Tap, t.Tick, now);
                    break;
                case SkillCast sc:
                    _c.Cues?.Push(CueKind.SkillCast, a: sc.Slot, id: sc.SkillId);
                    break;
                case EnemyKilled k:
                    _c.Cues?.Push(CueKind.EnemyKilled, level: k.Stage, value: k.Gold);
                    _c.Cues?.Push(CueKind.GoldBurst, value: k.Gold);
                    _c.Cues?.Feel(Haptic.None, Sounds.HitKill);
                    break;
                case StageCleared _:
                    _c.Cues?.Push(CueKind.StagePulse);
                    _c.Cues?.Feel(Haptic.None, Sounds.StageUp);
                    break;
                case BossFailed bf:
                    _c.Cues?.Push(CueKind.BossFailed, level: bf.Stage, value: bf.RemainingFraction);
                    _c.Cues?.Feel(Haptic.Failure, Sounds.BossFail);
                    _lastFailStage = bf.Stage;
                    _failToastPending = true;   // toasted on the next Refresh, with the need ratio at the new DPS
                    break;
                case BossCleared bc:
                    _c.Cues?.Push(CueKind.BossFlash, level: bc.Stage);
                    _c.Cues?.Feel(Haptic.Success, Sounds.BossClear);
                    break;
                case StageFirstCleared fc:
                    if (fc.Diamonds > 0) _c.Toasts?.Push(_c.Loc.Format(UiKeys.ToastFirstClear, TextFmt.IntText(fc.Diamonds)), ToastStyle.Reward);
                    break;
                case LevelUp lu:
                    _c.Cues?.Push(CueKind.LevelUpFloat, level: lu.Level);
                    _c.Cues?.Feel(Haptic.Success, Sounds.LevelUp);
                    break;
                case StageCapReached _:
                    _c.Cues?.Push(CueKind.StageCapReached);
                    _c.Toasts?.Push(_c.Loc.Text(UiKeys.ToastStageCap), ToastStyle.Info);
                    break;
            }
        }

        public void Update(double now) => DamageNumbers.Update(now);
    }

    /// <summary>
    /// SCR_MAIN (07 section 4.3): the HUD, combat band, action strip, Fish Board and tab bar. Red-dot pills (Quests,
    /// Dungeons, Inbox) and the Research button are MVP; their badges stay at zero in M0.
    /// </summary>
    public sealed class MainVm
    {
        readonly VmContext _c;

        public MainVm(VmContext c)
        {
            _c = c;
            Hud = new HudVm(c);
            Combat = new CombatVm(c);
            Board = new FishBoardVm(c);
            Research = new ResearchEntryVm(c, toastWhenLocked: true);
            for (int i = 0; i < TabBadges.Length; i++) TabBadges[i] = new BadgeDot();
            for (int i = 0; i < TabLocked.Length; i++) TabLocked[i] = new Flag();
        }

        public readonly HudVm Hud;
        public readonly CombatVm Combat;
        public readonly FishBoardVm Board;

        /// <summary>The Fish Board's [Research] (MVP, 07 section 4.3): lock glyph and condition toast before the gate.</summary>
        public readonly ResearchEntryVm Research;

        public readonly BadgeDot QuestsPill = new BadgeDot();
        public readonly BadgeDot DungeonsPill = new BadgeDot();
        public readonly BadgeDot InboxPill = new BadgeDot();

        /// <summary>Per <see cref="MainTab"/>: the red dot and the lock glyph.</summary>
        public readonly BadgeDot[] TabBadges = new BadgeDot[6];
        public readonly Flag[] TabLocked = new Flag[6];

        /// <summary>The tab tinted <c>accent</c>, or -1 on SCR_MAIN.</summary>
        public readonly Bindable<int> ActiveTab = new Bindable<int>();

        public void Refresh(Simulation sim, in Snapshot s, double now, int newCollectibles)
        {
            Hud.Refresh(sim, s);
            Combat.Refresh(sim, s, now);
            Board.Refresh(sim, s);
            Research.Refresh(sim);
            QuestsPill.Set(0);
            DungeonsPill.Set(0);
            InboxPill.Set(s.PendingInboxCount);
            TabBadges[(int)MainTab.Collection].Set(newCollectibles);
            var st = sim.State;
            TabLocked[(int)MainTab.Dungeons].Set(!_c.Db.IsUnlocked("DUNGEONS", st));
            TabLocked[(int)MainTab.Quests].Set(!_c.Db.IsUnlocked("QUESTS", st));
            TabLocked[(int)MainTab.Shop].Set(false);   // never locked; the screen itself is MVP
            ActiveTab.Set(TabOf(_c.Router?.TopFull.Id));
        }

        static int TabOf(string id)
        {
            switch (id)
            {
                case ScreenIds.Party: return (int)MainTab.Party;
                case ScreenIds.Weapons:
                case ScreenIds.Skills:
                case ScreenIds.Companions: return (int)MainTab.Collection;
                case ScreenIds.Summon: return (int)MainTab.Summon;
                case ScreenIds.Dungeons: return (int)MainTab.Dungeons;
                case ScreenIds.Quests: return (int)MainTab.Quests;
                case ScreenIds.Shop: return (int)MainTab.Shop;
                default: return -1;
            }
        }

        /// <summary>A tab tap replaces the top Full screen (07 section 1 rule 2); an MVP tab shows the lock toast.</summary>
        public void OnTab(MainTab tab)
        {
            switch (tab)
            {
                case MainTab.Party: _c.Router?.Replace(ScreenIds.Party); break;
                case MainTab.Collection: _c.Router?.Replace(ScreenIds.Weapons); break;
                case MainTab.Summon: _c.Router?.Replace(ScreenIds.Summon); break;
                case MainTab.Dungeons: _c.LockedToast("DUNGEONS"); break;
                case MainTab.Quests: _c.LockedToast("QUESTS"); break;
                case MainTab.Shop: _c.LockedToast("SHOP"); break;
            }
        }

        public void OnEvent(Event e, double now)
        {
            if (e is CurrencyChanged cc) Hud.OnCurrency(cc);
            Combat.OnEvent(e, now);
            Board.OnEvent(e);
        }
    }
}
