using System;
using System.Collections.Generic;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;
using HeroCat.Sim.Power;
using HeroCat.Sim.Summons;

namespace HeroCat.Sim
{
    /// <summary>
    /// Summon results and the collection commands (04 sections 6.3, 7.2, 10). ApplySummonResult is the only summon
    /// mutator in both modes (00 contract 5, issues #6 and #13): the pull itself was rolled elsewhere (LocalSummonService
    /// on a clone, or the server) and arrives as a payload.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>
        /// Request ids applied by this Simulation instance, newest last, at most <see cref="AppliedRequestsMax"/>.
        /// In memory only: 02 has no persisted applied set. A Local-mode payload is also guarded across reloads by the
        /// summon stream counter (a payload must start at the saved counter). TODO(lead): persist the set for Remote mode.
        /// </summary>
        readonly HashSet<string> _appliedRequests = new HashSet<string>(StringComparer.Ordinal);
        readonly Queue<string> _appliedOrder = new Queue<string>();
        internal const int AppliedRequestsMax = 256;   // literal-ok: bookkeeping bound, not balance

        // ================================================================================================ ApplySummonResult

        internal ApplyResult HandleApplySummonResult(ApplySummonResult c)
        {
            var p = c.Payload;
            var reject = ValidateSummon(p, out var def, out var debit);
            if (!reject.Ok) return reject;

            bool local = p.StreamCounterAfter.HasValue;
            var col = CollectionRules.For(State, p.Banner);

            // 1. pay (Local: reject if unaffordable, checked in ValidateSummon; Remote: the server's balance wins)
            if (local) Ledger.Apply(State, debit, CurrencyReason.SUMMON, this);
            else ApplyRemoteDebit(debit, p.BalancesAfter);

            // 2. stream counter (Local only; 00 decision 14: Remote ignores it)
            if (local) State.Rng[PullAlgorithm.StreamName(p.Banner)].Counter = p.StreamCounterAfter.Value;

            // 3. banner state from the result
            if (!State.Banners.TryGetValue(p.Banner, out var bs) || bs == null) State.Banners[p.Banner] = bs = new BannerState();
            int oldLevel = def.LevelFor(bs.Exp);
            bs.Pity = p.PityAfter;
            bs.TotalPulls += p.Count;
            bs.Exp = p.BannerExpAfter;
            State.Stats.Pulls += p.Count;

            // 4. items, overflow Rock Candy, one ItemAcquired per pull
            foreach (var r in p.Pulls)
            {
                if (!col.Items.TryGetValue(r.ItemId, out var item) || item == null)
                    col.Items[r.ItemId] = item = new OwnedItem { FirstAcquiredServerTime = p.ServerTimeSec };
                item.Copies = r.CopiesAfter;
                if (r.OverflowRockCandy > 0)
                    Ledger.Apply(State, new CurrencyDelta(Currency.ROCK_CANDY, r.OverflowRockCandy), CurrencyReason.SUMMON, this);
                Emit(new ItemAcquired(p.Banner, r.ItemId, r.Rarity, r.IsNew, r.LevelBefore, r.LevelAfter, r.OverflowRockCandy));
            }

            int newLevel = def.LevelFor(bs.Exp);
            if (newLevel > oldLevel) Emit(new BannerLeveledUp(p.Banner, newLevel));

            QuestsProgress(QuestGoal.SUMMON, p.Count, (int)p.Banner);   // each pull, with the banner filter (06 section 5.1)
            AchievementsOnCollection(p.Banner, col);                     // OWN_* ladders: distinct ids owned (06 section 5.4)
            if (p.Banner == Banner.WEAPON) AutoEquipWeapon(auto: true);   // weapons only (04 open question 4 default)
            SummonsMarkDpsDirty();
            RememberApplied(p.RequestId);
            return ApplyResult.Accepted;
        }

        ApplyResult ValidateSummon(SummonResultPayload p, out BannerDef def, out CurrencyDelta debit)
        {
            def = null;
            debit = default;
            if (p == null || p.Pulls == null || p.Debits == null || string.IsNullOrEmpty(p.RequestId))
                return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.SUMMON_BAD_PAYLOAD);
            if (!Db.Banners.TryGetValue(p.Banner, out def) || !def.Enabled || def.Levels.Count == 0)
                return ApplyResult.Reject(RejectCode.NotAvailable, CollectionRejectReasons.BANNER_CLOSED);
            if ((p.Count != 1 && p.Count != def.GuaranteeBatchSize) || p.Pulls.Length != p.Count)
                return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.SUMMON_BAD_COUNT);
            if (_appliedRequests.Contains(p.RequestId))
                return ApplyResult.Reject(RejectCode.StaleRequest, CollectionRejectReasons.SUMMON_ALREADY_APPLIED);

            // one debit, all diamonds or all tickets of this banner: no mixed payment, no discount (04 section 2)
            if (p.Debits.Length != 1) return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.SUMMON_BAD_PAYMENT);
            debit = p.Debits[0];
            bool diamonds = debit.Currency == Currency.DIAMOND && debit.Amount == -(double)p.Count * def.PullCostDiamonds;
            bool tickets = debit.Currency == def.TicketCurrency && debit.Amount == -(double)p.Count;
            if (!diamonds && !tickets) return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.SUMMON_BAD_PAYMENT);

            int maxCopies = PullAlgorithm.MaxCopies(Db);
            foreach (var r in p.Pulls)
            {
                if (r == null || !CollectionRules.IsKnown(Db, p.Banner, r.ItemId) || CollectionRules.RarityOf(Db, p.Banner, r.ItemId) != r.Rarity ||
                    r.CopiesAfter < 1 || r.CopiesAfter > maxCopies || r.OverflowRockCandy < 0)
                    return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.SUMMON_BAD_PAYLOAD);
            }
            State.Banners.TryGetValue(p.Banner, out var bs);
            long exp = bs != null ? bs.Exp : 0;
            if (p.PityAfter < 0 || p.PityAfter >= def.PityHard || p.BannerExpAfter < exp)
                return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.SUMMON_BAD_PAYLOAD);

            if (p.StreamCounterAfter.HasValue)   // Local mode: the payload must have been rolled from the saved counter
            {
                if (!State.Rng.TryGetValue(PullAlgorithm.StreamName(p.Banner), out var stream) || stream == null ||
                    p.StreamCounterAfter.Value != stream.Counter + (long)PullAlgorithm.DrawsPerPull * p.Count)
                    return ApplyResult.Reject(RejectCode.StaleRequest, CollectionRejectReasons.SUMMON_STREAM_MISMATCH);
                if (!State.Wallet.CanAfford(debit.Currency, -debit.Amount))
                    return ApplyResult.Reject(RejectCode.CannotAfford, CollectionRejectReasons.NotEnough(debit.Currency));
            }
            else if (p.BalancesAfter == null)
            {
                return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.SUMMON_BAD_PAYLOAD);
            }
            return ApplyResult.Accepted;
        }

        /// <summary>
        /// Remote mode (MVP): debit locally, then snap each debited balance to the server's (server ledger wins, 00
        /// section 4). Runtime compares the before/after balances to emit summon_reconciled (04 section 13).
        /// </summary>
        void ApplyRemoteDebit(CurrencyDelta debit, CurrencyDelta[] balancesAfter)
        {
            if (State.Wallet.CanAfford(debit.Currency, -debit.Amount)) Ledger.Apply(State, debit, CurrencyReason.SUMMON, this);
            foreach (var b in balancesAfter)
            {
                double diff = b.Amount - State.Wallet.Get(b.Currency);
                if (diff != 0) Ledger.Apply(State, new CurrencyDelta(b.Currency, diff), CurrencyReason.SUMMON, this);
            }
        }

        void RememberApplied(string requestId)
        {
            if (!_appliedRequests.Add(requestId)) return;
            _appliedOrder.Enqueue(requestId);
            while (_appliedOrder.Count > AppliedRequestsMax) _appliedRequests.Remove(_appliedOrder.Dequeue());
        }

        /// <summary>Equips and copy gains invalidate the Power layer's DPS cache (03 section 7.3).</summary>
        void SummonsMarkDpsDirty() => MarkDpsDirty();

        // ================================================================================================ party slots (04 6.3, 7.2, 8.2)

        /// <summary>
        /// The state's party slots sized PARTY_SLOTS. ProfileFactory and the save validator size them; for a hand-built state
        /// of another size this repairs the array in place (missing slots become empty), which changes no rated number.
        /// </summary>
        PartySlot[] PartySlots()
        {
            int n = Db.Constants.PartySlots;
            var cur = State.Party.Slots;
            if (cur != null && cur.Length == n && Array.TrueForAll(cur, x => x != null)) return cur;
            var r = PartyState.NewSlots(n);
            if (cur != null) for (int i = 0; i < n && i < cur.Length; i++) if (cur[i] != null) r[i] = cur[i];
            State.Party.Slots = r;
            return r;
        }

        /// <summary>The shared equip checks (04 sections 6.3, 7.2, 8.2): BAD_SLOT, UNKNOWN_ITEM, ITEM_NOT_OWNED, ALREADY_EQUIPPED, IN_OTHER_SLOT.</summary>
        ApplyResult CheckEquip(int slot, string id, Banner kind, Func<PartySlot, string> field, string inOtherSlot)
        {
            int n = Db.Constants.PartySlots;
            if (slot < 0 || slot >= n) return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            if (id == null || !CollectionRules.IsKnown(Db, kind, id))
                return ApplyResult.Reject(RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            if (!CollectionRules.Owns(CollectionRules.For(State, kind), id))
                return ApplyResult.Reject(RejectCode.NotOwned, CollectionRejectReasons.ITEM_NOT_OWNED);
            var slots = PartySlots();
            if (field(slots[slot]) == id) return ApplyResult.Reject(RejectCode.InvalidTarget, CollectionRejectReasons.ALREADY_EQUIPPED);
            for (int i = 0; i < slots.Length; i++)
                if (i != slot && field(slots[i]) == id) return ApplyResult.Reject(RejectCode.InvalidTarget, inOtherSlot);
            return ApplyResult.Accepted;
        }

        bool BadSlot(int slot) => slot < 0 || slot >= Db.Constants.PartySlots;

        // ================================================================================================ weapons (04 6.3)

        internal ApplyResult HandleEquipWeapon(EquipWeapon c)
        {
            var r = CheckEquip(c.Slot, c.WeaponId, Banner.WEAPON, x => x.Weapon, CollectionRejectReasons.WEAPON_IN_OTHER_SLOT);
            if (!r.Ok) return r;
            SetWeapon(c.Slot, c.WeaponId, auto: false);
            return ApplyResult.Accepted;
        }

        /// <summary>Rejects only BAD_SLOT; an already unarmed slot is accepted with no event (04 section 6.3).</summary>
        internal ApplyResult HandleUnequipWeapon(UnequipWeapon c)
        {
            if (BadSlot(c.Slot)) return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            if (PartySlots()[c.Slot].Weapon != null) SetWeapon(c.Slot, null, auto: false);
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleAutoEquipBestWeapons(AutoEquipBestWeapons c)
        {
            ApplyWeapons(CollectionRules.BestWeapons(Db, State), auto: false);
            return ApplyResult.Accepted;
        }

        /// <summary>
        /// After a weapon summon result (04 section 6.3): switches to AutoEquipBestWeapons' arrangement only when it rates
        /// strictly higher than the current one, so a tie never reshuffles a player's manual choice.
        /// </summary>
        void AutoEquipWeapon(bool auto)
        {
            var best = CollectionRules.BestWeapons(Db, State);
            var probe = CollectionRules.WithField(PartySlots(), best, (x, id) => x.Weapon = id);
            if (PowerFormula.Compute(Db, State, 0.0, probe).RatedDps > Query.RatedDps) ApplyWeapons(best, auto);
        }

        void ApplyWeapons(string[] best, bool auto)
        {
            // One ItemEquipped per slot whose weapon changed (04 section 6.3); the command is atomic, so a weapon moving between
            // two slots is never seen in both.
            var slots = PartySlots();
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].Weapon != best[i]) SetWeapon(i, best[i], auto);
        }

        void SetWeapon(int slot, string id, bool auto)
        {
            var x = PartySlots()[slot];
            string prev = x.Weapon;
            x.Weapon = id;
            SummonsMarkDpsDirty();
            Emit(new ItemEquipped(Banner.WEAPON, slot, id, prev, auto));
        }

        // ================================================================================================ limit breaks (04 5.2)

        /// <summary>
        /// 04 section 5.2. An unknown id is rejected like the equip commands (InvalidTarget, UNKNOWN_ITEM) before the
        /// spec's three checks. Weapons, skills and companions (CollectionRules.LimitBreakKinds). No level-10 requirement.
        /// </summary>
        internal ApplyResult HandleLimitBreak(LimitBreak c)
        {
            if (!CollectionRules.TryKindOf(Db, c.ItemId, out var kind))
                return ApplyResult.Reject(RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            var col = CollectionRules.For(State, kind);
            if (!CollectionRules.Owns(col, c.ItemId))
                return ApplyResult.Reject(RejectCode.NotOwned, CollectionRejectReasons.ITEM_NOT_OWNED);
            var item = col.Items[c.ItemId];
            if (item.LimitBreaks >= Db.Constants.LimitBreakRanks)
                return ApplyResult.Reject(RejectCode.Capped, CollectionRejectReasons.LIMIT_BREAK_CAPPED);
            long cost = CollectionRules.NextLimitBreakCost(Db, kind, col, c.ItemId);
            if (!State.Wallet.CanAfford(Currency.ROCK_CANDY, cost) ||
                !Ledger.Apply(State, new CurrencyDelta(Currency.ROCK_CANDY, -cost), CurrencyReason.LIMIT_BREAK, this))
                return ApplyResult.Reject(RejectCode.CannotAfford, CollectionRejectReasons.NotEnough(Currency.ROCK_CANDY));
            item.LimitBreaks += 1;
            SummonsMarkDpsDirty();
            Emit(new ItemLimitBroken(c.ItemId, item.LimitBreaks, cost));
            return ApplyResult.Accepted;
        }

        // ================================================================================================ skills (04 7.2)

        internal ApplyResult HandleEquipSkill(EquipSkill c)
        {
            var r = CheckEquip(c.Slot, c.SkillId, Banner.SKILL, x => x.Skill, CollectionRejectReasons.SKILL_IN_OTHER_SLOT);
            if (!r.Ok) return r;
            SetSkill(c.Slot, c.SkillId);
            return ApplyResult.Accepted;
        }

        /// <summary>Rejects only BAD_SLOT; an already empty slot is accepted with no event (04 section 7.2).</summary>
        internal ApplyResult HandleUnequipSkill(UnequipSkill c)
        {
            if (BadSlot(c.Slot)) return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            if (PartySlots()[c.Slot].Skill != null) SetSkill(c.Slot, null);
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleAutoEquipBestSkills(AutoEquipBestSkills c)
        {
            var best = CollectionRules.BestSkills(Db, State);
            var slots = PartySlots();
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].Skill != best[i]) SetSkill(i, best[i]);
            return ApplyResult.Accepted;
        }

        /// <summary>
        /// A skill put into a slot starts on its full cooldown; an emptied slot has none (the integration track's decision
        /// for 04 section 7.2: re-slotting can never be used to skip a cooldown, and a fresh loadout bursts once the
        /// cooldowns run out). Combat.SkillCooldowns is sized PARTY_SLOTS by ProfileFactory and the save validator.
        /// </summary>
        void StartSlotCooldown(int slot, string id)
        {
            var cds = State.Combat.SkillCooldowns;
            if (cds == null || slot < 0 || slot >= cds.Length) return;
            cds[slot] = id != null && Db.Skills.TryGetValue(id, out var def) ? def.CooldownTicks : 0;
        }

        void SetSkill(int slot, string id)
        {
            var x = PartySlots()[slot];
            string prev = x.Skill;
            x.Skill = id;
            StartSlotCooldown(slot, id);
            SummonsMarkDpsDirty();
            Emit(new ItemEquipped(Banner.SKILL, slot, id, prev, false));
        }

        // ================================================================================================ companions (04 8.2)

        internal ApplyResult HandleEquipCompanion(EquipCompanion c)
        {
            var r = CheckEquip(c.Slot, c.CompanionId, Banner.COMPANION, x => x.Companion, CollectionRejectReasons.COMPANION_IN_OTHER_SLOT);
            if (!r.Ok) return r;
            SetCompanion(c.Slot, c.CompanionId, auto: false);
            return ApplyResult.Accepted;
        }

        /// <summary>Rejects only BAD_SLOT; an already empty slot is accepted with no event (04 section 8.2).</summary>
        internal ApplyResult HandleUnequipCompanion(UnequipCompanion c)
        {
            if (BadSlot(c.Slot)) return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            if (PartySlots()[c.Slot].Companion != null) SetCompanion(c.Slot, null, auto: false);
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleAutoEquipBestCompanions(AutoEquipBestCompanions c)
        {
            var best = CollectionRules.BestCompanions(Db, State);
            var slots = PartySlots();
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].Companion != best[i]) SetCompanion(i, best[i], auto: false);
            return ApplyResult.Accepted;
        }

        /// <summary>
        /// As skills (<see cref="StartSlotCooldown"/>): a companion put into a slot starts its ability on its full cooldown,
        /// an emptied slot has none, so re-slotting never skips a cooldown. The slot keeps its weapon, skill, Awakening rank,
        /// MP pool and attack progress (00 decision 109, 04 section 8.2).
        /// </summary>
        void SetCompanion(int slot, string id, bool auto)
        {
            var x = PartySlots()[slot];
            string prev = x.Companion;
            x.Companion = id;
            var cds = State.Combat.CompanionCooldowns;
            if (cds != null && slot < cds.Length)
                cds[slot] = id != null && Db.Companions.TryGetValue(id, out var def) ? def.CooldownTicks : 0;
            SummonsMarkDpsDirty();
            Emit(new ItemEquipped(Banner.COMPANION, slot, id, prev, auto));
        }
    }
}
