using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.Power;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>
    /// The stage combat loop (03 section 2): one call of <see cref="TickCombat"/> per tick in 03 section 2.1's order, which
    /// is a contract (10's tests and 08's re-simulator rely on it). Damage follows 03 section 7.1's realised split
    /// (issue #10): a hero hit carries the layers without a discrete event; skills deal multiples of SkillUnit, fish
    /// missiles and taps multiples of HeroHit. The only RNG draws are one `crit` per hero hit and one `loot` per boss kill
    /// (03 section 10).
    /// </summary>
    public sealed partial class Simulation
    {
        SimQuery _query;
        int _damageCount;

        /// <summary>Derived numbers: the cached power breakdown, costs, the wall check (03 section 7).</summary>
        public SimQuery Query => _query ?? (_query = new SimQuery(Db, State));

        /// <summary>Opt-in HitLanded events, one per hero hit (03 section 2.2). Off by default: bots and the server never need them.</summary>
        public bool EmitHitLanded { get; set; }

        /// <summary>Taps the last TapZap accepted and ignored (03 section 2.5; ApplyResult carries no payload).</summary>
        public int LastTapZapUsed { get; private set; }

        public int LastTapZapIgnored { get; private set; }

        /// <summary>03 section 7.3. Also called by Apply after every accepted command.</summary>
        internal void MarkDpsDirty() => Query.MarkDirty();

        /// <summary>The fish track's hook (Simulation.Fish.cs): a board change invalidates the DPS cache (05 section 12 rule 5).</summary>
        partial void OnFishBoardDpsChanged() => MarkDpsDirty();

        /// <summary>
        /// True when Emit would fold a kill's EnemyKilled and CurrencyChanged(KILL) into the open EnemyKilledBatch (01 section
        /// 2.3). The kill path checks it first so a folded event is never allocated (03 criterion 19: under 128 B per kill).
        /// </summary>
        internal bool CoalescingKills => ForceCoalesce || _events.Count >= SimConfig.EVENT_COALESCE_AT;

        /// <summary>Adds one kill to the open batch: what Emit does with a coalesced EnemyKilled.</summary>
        void FoldKill(int stage, double gold, double exp)
        {
            var b = OpenBatch(stage);
            b.Count += 1;
            b.GoldTotal += gold;
            b.ExpTotal += exp;
            b.ToStage = stage;
            b.Tick = State.Time.Tick;
        }

        /// <summary>
        /// What Emit does with a coalesced CurrencyChanged(KILL) of GOLD or HERO_EXP: the amount goes into the open batch's
        /// KillGold / KillExp, so the KILL rows of 06 section 13's <c>currency_changed</c> stay exact under coalescing.
        /// </summary>
        internal void FoldKillCurrency(Currency c, double amount)
        {
            var b = OpenBatch(FoughtStage);   // the fought stage, as FoldKill: a side stage when farming one
            if (c == Currency.GOLD) b.KillGold += amount;
            else b.KillExp += amount;
        }

        /// <summary>The fought stage: the selected side stage, else the frontier (03 section 2.6; selection is MVP, #159).</summary>
        int FoughtStage => State.Combat.Selected ?? State.Combat.Stage;

        /// <summary>The transient fight; null after load or profile creation, then a fresh Spawning fight (02 section 3.3).</summary>
        FightState EnsureFight()
        {
            var f = State.Combat.Fight;
            if (f != null) return f;
            f = new FightState { Phase = FightPhase.Spawning, SpawnTimer = Db.Constants.SpawnDelayTicks };
            State.Combat.Fight = f;
            return f;
        }

        // ================================================================================================ tick (03 2.1)

        void TickCombat()
        {
            _damageCount = 0;
            var c = State.Combat;
            var f = EnsureFight();
            RegenMp(c);                                                         // 1. MP regen (MVP)
            TickCooldowns(c);                                                   // 2.
            if (c.Mode == CombatMode.Stage)                                     // TODO(#172): the dungeon fight (06) ticks here too
            {
                if (f.Phase == FightPhase.Spawning)
                {
                    f.SpawnTimer -= 1;
                    if (f.SpawnTimer <= 0) Spawn(f);
                }
                else
                {
                    FightTick(f);
                }
            }
            TickBuffs(f);                                                       // 9.
        }

        void FightTick(FightState f)
        {
            var k = Db.Constants;
            ApplyQueuedFishMissiles(f);                                         // 3.
            if (f.EnemyHp <= 0) { Kill(f); return; }
            ApplyDots(f);                                                       // 4. DOTs first, then at most one cast
            if (f.EnemyHp <= 0) { Kill(f); return; }
            AutoCastSkills(f);
            CastCompanionAbilities(f);                                          // 5. at most one, MP-gated
            if (f.EnemyHp <= 0) { Kill(f); return; }
            f.AttackProgress += Query.AttackGainUnits;                          // 6.
            int interval = k.AttackIntervalTicks * k.AttackProgressUnit;
            if (f.AttackProgress >= interval)
            {
                f.AttackProgress -= interval;
                HeroHit(f);
                if (f.EnemyHp <= 0) { Kill(f); return; }
            }
            ApplyTapZaps(f);                                                    // 7.
            if (f.EnemyHp <= 0) { Kill(f); return; }
            if (f.EnemyIsBoss)                                                  // 8. timer last: a hit on the final tick counts
            {
                f.BossTicksLeft -= 1;
                if (f.BossTicksLeft <= 0) BossFail(f);
            }
        }

        void TickCooldowns(CombatState c)
        {
            var sk = c.SkillCooldowns;
            if (sk != null) for (int i = 0; i < sk.Length; i++) if (sk[i] > 0) sk[i] -= 1;
            var co = c.CompanionCooldowns;
            if (co != null) for (int i = 0; i < co.Length; i++) if (co[i] > 0) co[i] -= 1;
        }

        static void TickBuffs(FightState f)
        {
            if (f.AtkBuffTicksLeft > 0)
            {
                f.AtkBuffTicksLeft -= 1;
                if (f.AtkBuffTicksLeft == 0) f.AtkBuffPct = 0;
            }
            if (f.GoldBuffTicksLeft > 0)
            {
                f.GoldBuffTicksLeft -= 1;
                if (f.GoldBuffTicksLeft == 0) f.GoldBuffPct = 0;
            }
        }

        /// <summary>
        /// 03 section 2.1 step 1: <c>Mp = min(MpPoolMax, Mp + MpRegenPerTick)</c>, both with their Research terms (cached in the
        /// power breakdown). Only in a content tier with companions: the MP pool is MVP, and an M0 build keeps Mp at 0.
        /// </summary>
        void RegenMp(CombatState c)
        {
            if (Db.Companions.Count == 0) return;
            var p = Query.Power;
            double mp = c.Mp + p.MpRegenPerTick;
            c.Mp = mp < p.MpPoolMax ? mp : p.MpPoolMax;
        }

        void AddDamage(double amount, bool crit)
        {
            if (_damageCount < _damage.Length) _damage[_damageCount++] = new DamageNumber(amount, crit);
        }

        // ================================================================================================ damage sources

        /// <summary>03 section 2.2: exactly one `crit` draw per hero hit, always; the ATK buff applies here only.</summary>
        void HeroHit(FightState f)
        {
            var q = Query;
            double dmg = q.HeroHit * (1.0 + f.AtkBuffPct / Units.Percent);
            double u = RngStreams.NextUnit(State.Rng[RngStreams.Crit]);
            State.Stats.HeroHits += 1;
            bool isCrit = u < q.CritRate;
            if (isCrit) dmg *= q.CritDmg;
            f.EnemyHp -= dmg;
            AddDamage(dmg, isCrit);
            if (EmitHitLanded) Emit(new HitLanded(dmg, isCrit, f.EnemyHp));
        }

        /// <summary>03 section 2.5: drain the whole FIFO queue into this enemy (no crit, no draw, no buff); surplus is lost with the kill.</summary>
        void ApplyQueuedFishMissiles(FightState f)
        {
            var queue = State.Combat.MissileQueue;
            if (queue == null || queue.Count == 0) return;
            double hit = Query.HeroHit;
            for (int i = 0; i < queue.Count; i++)
            {
                double frac = queue[i];
                double dmg = hit * frac;
                f.EnemyHp -= dmg;
                AddDamage(dmg, false);
                Emit(new FishMissileHit(frac, dmg));
            }
            queue.Clear();
        }

        /// <summary>03 section 2.5: pending taps deal HeroHit * TAP_ZAP_DMG_PCT each, no crit, no draw.</summary>
        void ApplyTapZaps(FightState f)
        {
            int n = f.PendingTaps;
            if (n <= 0) return;
            double dmg = Query.HeroHit * Db.Constants.TapZapDmgPct * n;
            f.EnemyHp -= dmg;
            f.PendingTaps = 0;
            AddDamage(dmg, false);
            Emit(new TapZapped(n, dmg));
        }

        /// <summary>Active DOTs tick before any cast (03 section 2.3). DamagePerTick is a multiple of SkillUnit.</summary>
        void ApplyDots(FightState f)
        {
            var dots = f.ActiveDots;
            if (dots.Count == 0) return;
            double unit = Query.SkillUnit;
            for (int i = 0; i < dots.Count;)
            {
                var d = dots[i];
                double dmg = unit * d.DamagePerTick;
                f.EnemyHp -= dmg;
                AddDamage(dmg, false);
                if (d.TicksLeft <= 1) dots.RemoveAt(i);
                else { dots[i] = d with { TicksLeft = d.TicksLeft - 1 }; i++; }
            }
        }

        /// <summary>03 section 2.3: slot order is priority; at most one cast per tick; no `crit` draws.</summary>
        void AutoCastSkills(FightState f)
        {
            var slots = State.Loadout.Skills;
            var cds = State.Combat.SkillCooldowns;
            if (slots == null || cds == null) return;
            int n = slots.Length < cds.Length ? slots.Length : cds.Length;
            for (int slot = 0; slot < n; slot++)
            {
                string id = slots[slot];
                if (id == null || cds[slot] > 0) continue;
                if (!Db.Skills.TryGetValue(id, out var def)) continue;
                double lm = PowerFormula.SkillLevelMult(Db, State, id);
                if (lm <= 0) continue;   // not owned: contributes nothing, as in the rated formula
                CastSkill(f, def, lm);
                cds[slot] = def.CooldownTicks;
                Emit(new SkillCast(slot, id));
                return;
            }
        }

        /// <summary>
        /// 03 section 2.4: slot order is priority; locked, empty, unowned and unknown slots are skipped; a ready ability waits
        /// while Mp is below its cost (its cooldown does not restart); at most one cast per tick; no `crit` draws.
        /// </summary>
        void CastCompanionAbilities(FightState f)
        {
            var slots = State.Loadout.Companions;
            var cds = State.Combat.CompanionCooldowns;
            if (slots == null || cds == null || Db.Companions.Count == 0) return;
            int n = slots.Length < cds.Length ? slots.Length : cds.Length;
            int open = -1;
            for (int slot = 0; slot < n; slot++)
            {
                if (slots[slot] == null || cds[slot] > 0) continue;
                if (open < 0) open = PowerFormula.OpenCompanionSlots(Db, State);
                var def = PowerFormula.ActiveCompanion(Db, State, slot, open, out double lm);
                if (def == null) continue;
                var c = State.Combat;
                if (c.Mp < def.MpCost) continue;
                c.Mp -= def.MpCost;
                ApplyCompanion(f, def, lm);
                cds[slot] = def.CooldownTicks;
                Emit(new CompanionAbilityUsed(slot, def.Id, c.Mp));
                return;
            }
        }

        /// <summary>
        /// 03 section 2.4's Apply(c), magnitudes from 04 section 8.1 scaled by LevelMult: DAMAGE deals CompanionUnit *
        /// DamagePctPerCast / 100; BUFF_ATK sets the shared ATK buff exactly as a BUFF skill (strongest wins); BUFF_GOLD sets
        /// the gold-from-kills buff (its own slot, strongest wins, read by Kill); MP_REGEN restores MP up to MpPoolMax.
        /// </summary>
        void ApplyCompanion(FightState f, CompanionDef def, double levelMult)
        {
            double mag = def.Magnitude * levelMult;
            int dur = def.DurationTicks.GetValueOrDefault();
            switch (def.AbilityType)
            {
                case AbilityType.DAMAGE:
                {
                    double dmg = Query.CompanionUnit * mag / Units.Percent;
                    f.EnemyHp -= dmg;
                    AddDamage(dmg, false);
                    return;
                }
                case AbilityType.BUFF_ATK:
                {
                    double pct = f.AtkBuffPct;
                    int left = f.AtkBuffTicksLeft;
                    ApplyBuff(ref pct, ref left, mag, dur);
                    f.AtkBuffPct = pct; f.AtkBuffTicksLeft = left;
                    return;
                }
                case AbilityType.BUFF_GOLD:
                {
                    double pct = f.GoldBuffPct;
                    int left = f.GoldBuffTicksLeft;
                    ApplyBuff(ref pct, ref left, mag, dur);
                    f.GoldBuffPct = pct; f.GoldBuffTicksLeft = left;
                    return;
                }
                case AbilityType.MP_REGEN:
                {
                    var c = State.Combat;
                    double max = Query.Power.MpPoolMax;
                    double mp = c.Mp + mag;
                    c.Mp = mp < max ? mp : max;
                    return;
                }
            }
        }

        /// <summary>Buffs do not stack: the strongest active one applies, and a weaker cast never shortens it (03 section 2.3, 04 AC10).</summary>
        static void ApplyBuff(ref double activePct, ref int ticksLeft, double pct, int dur)
        {
            if (dur <= 0 || pct <= 0) return;
            if (ticksLeft <= 0 || pct > activePct)
            {
                activePct = pct;
                ticksLeft = dur;
            }
            else if (pct == activePct && dur > ticksLeft)
            {
                ticksLeft = dur;
            }
        }

        void CastSkill(FightState f, SkillDef def, double levelMult)
        {
            switch (def.CastType)
            {
                case CastType.INSTANT:
                {
                    double dmg = Query.SkillUnit * def.DamagePctPerCast * levelMult / Units.Percent;
                    f.EnemyHp -= dmg;
                    AddDamage(dmg, false);
                    return;
                }
                case CastType.DOT:
                {
                    int dur = def.DurationTicks.GetValueOrDefault();
                    if (dur <= 0) return;
                    var dots = f.ActiveDots;
                    if (dots.Count >= Db.Constants.SkillSlots) dots.RemoveAt(0);   // max SKILL_SLOTS (6) entries, oldest replaced
                    dots.Add(new DotInstance(dur, def.DamagePctPerTick * levelMult / Units.Percent));
                    return;
                }
                case CastType.BUFF:
                {
                    double pct = f.AtkBuffPct;
                    int left = f.AtkBuffTicksLeft;
                    ApplyBuff(ref pct, ref left, def.BuffPct.GetValueOrDefault() * levelMult, def.DurationTicks.GetValueOrDefault());
                    f.AtkBuffPct = pct; f.AtkBuffTicksLeft = left;
                    return;
                }
            }
        }

        // ================================================================================================ spawn, kill, advance (03 2.6)

        void Spawn(FightState f)
        {
            var c = State.Combat;
            var k = Db.Constants;
            bool isBoss = c.Selected == null && c.Boss == BossPhase.Pending;   // a selected stage never spawns a boss
            int fought = FoughtStage;
            double hp = isBoss ? k.BossHpMult * Query.EnemyHp(c.Stage) : Query.EnemyHp(fought);
            f.EnemyIsBoss = isBoss;
            f.EnemyMaxHp = hp;
            f.EnemyHp = hp;
            f.AttackProgress = 0;
            f.BossTicksLeft = isBoss ? k.BossTimeTicks : 0;
            f.Phase = FightPhase.Fighting;
            Emit(new EnemySpawned(fought, isBoss, hp));
            if (isBoss) QuestsProgress(QuestGoal.ATTEMPT_BOSS, 1);   // one boss spawn = one attempt, the automatic one included (d_boss)
        }

        void Kill(FightState f)
        {
            var c = State.Combat;
            var k = Db.Constants;
            var st = State.Stats;
            int s = FoughtStage;
            bool isBoss = f.EnemyIsBoss;
            var q = Query;
            double gold = q.GoldPerKill(s) * q.GoldMult * (isBoss ? k.BossGoldMult : 1.0);
            if (f.GoldBuffTicksLeft > 0) gold *= 1.0 + f.GoldBuffPct / Units.Percent;   // BUFF_GOLD companion (04 section 8.1)
            double exp = q.ExpPerKill(s) * q.ExpMult;
            var reason = isBoss ? CurrencyReason.BOSS : CurrencyReason.KILL;
            Ledger.Apply(State, new CurrencyDelta(Currency.GOLD, gold), reason, this);
            Ledger.Apply(State, new CurrencyDelta(Currency.HERO_EXP, exp), reason, this);   // the only EXP path: runs CheckHeroLevel
            st.Kills += 1;
            if (isBoss) st.BossKills += 1;
            if (CoalescingKills) FoldKill(s, gold, exp);
            else Emit(new EnemyKilled(s, isBoss, gold, exp));
            QuestsProgress(QuestGoal.KILL_ENEMIES, 1);   // 06 section 5.1: every kill, boss included

            if (isBoss)
            {
                if (RngStreams.NextUnit(State.Rng[RngStreams.Loot]) < k.BossRockCandyP)   // exactly one `loot` draw per boss kill
                    Ledger.Apply(State, new CurrencyDelta(Currency.ROCK_CANDY, 1), CurrencyReason.BOSS, this);
                Emit(new BossCleared(s));
                var p = State.Progress;
                if (s > p.HighestFirstClearBoss)
                {
                    p.HighestFirstClearBoss = s;
                    Ledger.Apply(State, new CurrencyDelta(Currency.DIAMOND, k.FirstClearDiamonds), CurrencyReason.FIRST_CLEAR, this);
                    Emit(new StageFirstCleared(s, k.FirstClearDiamonds));
                }
                AdvanceStage(viaBoss: true);
            }
            else if (c.Selected != null)
            {
                // farming a selected side stage: pays, never advances (issue #35)
            }
            else if (c.Boss == BossPhase.Walled)
            {
                // farming a walled stage: no credit toward the advance
            }
            else
            {
                c.KillsInStage += 1;
                if (c.KillsInStage == k.KillsPerStage - 1 && k.IsBossStage(s)) c.Boss = BossPhase.Pending;   // the 10th enemy is the boss
                else if (c.KillsInStage >= k.KillsPerStage) AdvanceStage(viaBoss: false);
            }
            Despawn(f);
        }

        void Despawn(FightState f)
        {
            f.Phase = FightPhase.Spawning;
            f.SpawnTimer = Db.Constants.SpawnDelayTicks;
            f.ActiveDots.Clear();
        }

        void AdvanceStage(bool viaBoss)
        {
            var c = State.Combat;
            var k = Db.Constants;
            var p = State.Progress;
            int from = c.Stage;
            if (from > p.HighestCleared)
            {
                p.HighestCleared = from;
                AchievementsSetMax(QuestGoal.REACH_STAGE, from);   // the Stage ladder reads Progress.HighestCleared (06 section 5.4)
            }
            if (from >= k.StageCap)
            {
                // Issue #81's decision: at the cap the stage loops (kills reset) and StageCapReached is emitted once.
                c.KillsInStage = 0;
                c.Boss = BossPhase.None;
                if (p.StageCapReached == 0)
                {
                    long t = State.Time.Tick;
                    p.StageCapReached = t > int.MaxValue ? int.MaxValue : (int)t;
                    Emit(new StageCapReached(from));
                }
                return;
            }
            c.Stage = from + 1;
            c.KillsInStage = 0;
            c.Boss = BossPhase.None;
            c.BossFailsOnStage = 0;
            Emit(new StageCleared(from, from + 1, viaBoss));
            QuestsProgress(QuestGoal.CLEAR_STAGES, 1);
            if (from % k.ChapterWidth == 0) Emit(new ChapterEntered(k.ChapterOf(from + 1)));
        }

        // ================================================================================================ the wall (03 2.7)

        void BossFail(FightState f)
        {
            var c = State.Combat;
            int s = c.Stage;
            c.Boss = BossPhase.Walled;
            c.BossFailsOnStage += 1;
            State.Stats.BossFails += 1;
            if (State.Stats.FirstWallStage == 0) State.Stats.FirstWallStage = s;
            double left = f.EnemyMaxHp > 0 ? f.EnemyHp / f.EnemyMaxHp : 0;
            Emit(new BossFailed(s, c.BossFailsOnStage, left));
            Despawn(f);
        }

        // ================================================================================================ commands

        internal ApplyResult HandleAttemptBoss()
        {
            var c = State.Combat;
            if (c.Boss == BossPhase.Pending && c.Selected == null)
                return ApplyResult.Reject(RejectCode.NotAvailable, CombatRejectReasons.BOSS_ALREADY_PENDING);
            if (!Db.Constants.IsBossStage(c.Stage) || c.Boss == BossPhase.None)
                return ApplyResult.Reject(RejectCode.NotAvailable, CombatRejectReasons.NO_BOSS_TO_ATTEMPT);
            if (c.Selected != null)
            {
                c.Selected = null;   // the boss challenge returns to the frontier (issue #35)
                Emit(new StageChanged(c.Stage));
            }
            c.Boss = BossPhase.Pending;
            Despawn(EnsureFight());   // the current mob despawns: no gold, no EXP, no kill credit
            return ApplyResult.Accepted;
        }

        /// <summary>03 section 2.7 SelectStage (issue #35): side-stage farming; the frontier's stage fields are never touched.</summary>
        internal ApplyResult HandleSelectStage(SelectStage cmd)
        {
            var c = State.Combat;
            var k = Db.Constants;
            if (cmd.Stage is int s)
            {
                if (s < 1 || s > k.StageCap) return ApplyResult.Reject(RejectCode.BadArgument, CombatRejectReasons.STAGE_OUT_OF_RANGE);
                int highest = State.Progress.HighestCleared > c.Stage ? State.Progress.HighestCleared : c.Stage;
                if (s > highest) return ApplyResult.Reject(RejectCode.Locked, CombatRejectReasons.STAGE_NOT_CLEARED);
            }
            if (c.Mode != CombatMode.Stage) return ApplyResult.Reject(RejectCode.NotAvailable, CombatRejectReasons.DUNGEON_RUN_IN_PROGRESS);
            int? target = cmd.Stage == c.Stage ? null : cmd.Stage;   // the frontier chip equals Return
            if (target == c.Selected) return ApplyResult.Accepted;   // already fought: no despawn, no event
            c.Selected = target;                                      // Stage, KillsInStage, Boss and BossFailsOnStage keep the frontier
            Despawn(EnsureFight());                                   // the current enemy despawns: no gold, no EXP, no kill credit
            Emit(new StageChanged(FoughtStage));
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleTapZap(TapZap cmd)
        {
            if (cmd.Count < 0) return ApplyResult.Reject(RejectCode.BadArgument, CombatRejectReasons.INVALID_COUNT);
            var k = Db.Constants;
            var f = EnsureFight();
            long now = State.Time.Tick;
            if (now - f.TapWindowTick >= k.TickHz)
            {
                f.TapWindowTick = now;
                f.TapsThisSecond = 0;
            }
            int allowed = k.TapZapMaxPerSec - f.TapsThisSecond;
            if (allowed < 0) allowed = 0;
            int used = cmd.Count < allowed ? cmd.Count : allowed;
            f.TapsThisSecond += used;
            f.PendingTaps += used;
            State.Stats.Taps += used;
            LastTapZapUsed = used;
            LastTapZapIgnored = cmd.Count - used;
            return ApplyResult.Accepted;
        }
    }
}
