using System;
using System.Collections.Generic;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;
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

        // ================================================================================================ weapons (04 6.3)

        internal ApplyResult HandleEquipWeapon(EquipWeapon c)
        {
            if (c.WeaponId == null || !Db.Weapons.ContainsKey(c.WeaponId))
                return ApplyResult.Reject(RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            if (!CollectionRules.Owns(State.Weapons, c.WeaponId))
                return ApplyResult.Reject(RejectCode.NotOwned, CollectionRejectReasons.ITEM_NOT_OWNED);
            if (State.Loadout.Weapon == c.WeaponId)
                return ApplyResult.Reject(RejectCode.InvalidTarget, CollectionRejectReasons.ALREADY_EQUIPPED);
            SetWeapon(c.WeaponId, auto: false);
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleAutoEquipBestWeapon(AutoEquipBestWeapon c)
        {
            AutoEquipWeapon(auto: false);
            return ApplyResult.Accepted;
        }

        /// <summary>Switches to the best owned weapon only when it is strictly better than the equipped one (04 section 6.3; ranking in CollectionRules.RankOwned).</summary>
        void AutoEquipWeapon(bool auto)
        {
            string best = CollectionRules.BestWeapon(Db, State);
            if (best == null || best == State.Loadout.Weapon) return;
            string cur = State.Loadout.Weapon;
            if (cur != null && CollectionRules.Owns(State.Weapons, cur) && !CollectionRules.StrictlyBetter(Db, Banner.WEAPON, State.Weapons, best, cur))
                return;
            SetWeapon(best, auto);
        }

        void SetWeapon(string id, bool auto)
        {
            string prev = State.Loadout.Weapon;
            State.Loadout.Weapon = id;
            SummonsMarkDpsDirty();
            Emit(new ItemEquipped(Banner.WEAPON, 0, id, prev, auto));
        }

        // ================================================================================================ skills (04 7.2)

        internal ApplyResult HandleEquipSkill(EquipSkill c)
        {
            var slots = SkillSlots();
            if (c.Slot < 0 || c.Slot >= slots.Length) return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            if (c.SkillId == null || !Db.Skills.ContainsKey(c.SkillId))
                return ApplyResult.Reject(RejectCode.InvalidTarget, CollectionRejectReasons.UNKNOWN_ITEM);
            if (!CollectionRules.Owns(State.Skills, c.SkillId))
                return ApplyResult.Reject(RejectCode.NotOwned, CollectionRejectReasons.ITEM_NOT_OWNED);
            if (slots[c.Slot] == c.SkillId) return ApplyResult.Reject(RejectCode.InvalidTarget, CollectionRejectReasons.ALREADY_EQUIPPED);
            for (int i = 0; i < slots.Length; i++)
                if (i != c.Slot && slots[i] == c.SkillId)
                    return ApplyResult.Reject(RejectCode.InvalidTarget, CollectionRejectReasons.SKILL_IN_OTHER_SLOT);
            SetSkill(slots, c.Slot, c.SkillId);
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleUnequipSkill(UnequipSkill c)
        {
            var slots = SkillSlots();
            if (c.Slot < 0 || c.Slot >= slots.Length) return ApplyResult.Reject(RejectCode.BadArgument, CollectionRejectReasons.BAD_SLOT);
            if (slots[c.Slot] == null) return ApplyResult.Reject(RejectCode.InvalidTarget, CollectionRejectReasons.SLOT_EMPTY);
            SetSkill(slots, c.Slot, null);
            return ApplyResult.Accepted;
        }

        internal ApplyResult HandleAutoEquipBestSkills(AutoEquipBestSkills c)
        {
            var slots = SkillSlots();
            var best = CollectionRules.BestSkills(Db, State);
            for (int i = 0; i < slots.Length; i++)
            {
                string id = i < best.Length ? best[i] : null;
                if (slots[i] != id) SetSkill(slots, i, id);
            }
            return ApplyResult.Accepted;
        }

        /// <summary>
        /// Loadout.Skills sized SKILL_SLOTS. ProfileFactory sizes it; for a hand-built state of another size this is a
        /// resized copy that only SetSkill stores, so a reject never changes the state.
        /// </summary>
        string[] SkillSlots()
        {
            int n = Db.Constants.SkillSlots;
            var s = State.Loadout.Skills;
            if (s != null && s.Length == n) return s;
            var r = new string[n];
            if (s != null) Array.Copy(s, r, Math.Min(s.Length, n));
            return r;
        }

        /// <summary>
        /// A skill put into a slot starts on its full cooldown; an emptied slot has none (the integration track's decision
        /// for 04 section 7.2: re-slotting can never be used to skip a cooldown, and a fresh loadout bursts once the
        /// cooldowns run out). Combat.SkillCooldowns is sized SKILL_SLOTS by ProfileFactory and the save validator.
        /// </summary>
        void StartSlotCooldown(int slot, string id)
        {
            var cds = State.Combat.SkillCooldowns;
            if (cds == null || slot < 0 || slot >= cds.Length) return;
            cds[slot] = id != null && Db.Skills.TryGetValue(id, out var def) ? def.CooldownTicks : 0;
        }

        void SetSkill(string[] slots, int slot, string id)
        {
            string prev = slots[slot];
            slots[slot] = id;
            State.Loadout.Skills = slots;
            StartSlotCooldown(slot, id);
            SummonsMarkDpsDirty();
            Emit(new ItemEquipped(Banner.SKILL, slot, id, prev, false));
        }
    }
}
