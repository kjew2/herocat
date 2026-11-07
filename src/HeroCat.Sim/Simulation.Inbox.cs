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
    /// ApplyInboxGrant (01 section 2.5, 06 section 9, 08 section 3.2; issues #176 and #271): the one door through which a
    /// server-confirmed inbox claim enters the sim. Runtime's InboxFlow issues it only after the server (or the local
    /// inbox) confirms the claim.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>The grant line kind for a currency (08 section 3.2 wire format).</summary>
        public const string GrantKindCurrency = "currency";

        /// <summary>The grant line kind for a collection item: <c>amount</c> copies of the item <c>id</c> (08 section 3.2, 06 section 9).</summary>
        public const string GrantKindItem = "item";

        /// <summary>
        /// Checks, in order: BadArgument/INBOX_BAD_GRANT (no payload or id, null lines, a reason outside IAP / MAIL /
        /// REFUND / ADMIN, a currency line that is not a known currency with a positive amount, an item line whose id is
        /// not a weapon, skill or companion of this content or whose amount is outside [1, int.MaxValue]),
        /// AlreadyClaimed/INBOX_ALREADY_APPLIED (the id is in Inbox.ClaimedIds, so a replayed or reconciled claim applies
        /// nothing twice). Then the id joins ClaimedIds and the lines apply in order: a currency line through Wallet.Apply
        /// with the payload's reason (any currency, Rock Candy and Fish Coins included, 06 rule 9.4); an item line through
        /// the summon item path (00 decision 116): <c>amount</c> copies of the item, the copies past DUPE_LVL_CUM[^1]
        /// converted to Rock Candy at the rarity's DUPE_OVERFLOW_ROCK_CANDY (with the payload's reason), one ItemAcquired
        /// per line. Afterwards the OWN_* achievements of each banner touched run, and weapon auto-equip runs when a
        /// weapon line was granted; quests do not count a grant (it is not a SUMMON). A payload with no lines (a refund
        /// notice) only records the id.
        /// </summary>
        internal ApplyResult HandleApplyInboxGrant(ApplyInboxGrant c)
        {
            var g = c.Grant;
            if (g == null || string.IsNullOrEmpty(g.InboxItemId) || g.Lines == null || !IsInboxReason(g.Reason))
                return ApplyResult.Reject(RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
            var deltas = new CurrencyDelta[g.Lines.Length];
            var kinds = new Banner?[g.Lines.Length];
            for (int i = 0; i < g.Lines.Length; i++)
            {
                var line = g.Lines[i];
                if (line == null) return ApplyResult.Reject(RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
                if (string.Equals(line.Kind, GrantKindItem, StringComparison.Ordinal))
                {
                    if (line.Amount <= 0 || line.Amount > int.MaxValue || line.Id == null || !CollectionRules.TryKindOf(Db, line.Id, out var kind))
                        return ApplyResult.Reject(RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
                    kinds[i] = kind;
                    continue;
                }
                if (!string.Equals(line.Kind, GrantKindCurrency, StringComparison.Ordinal) || line.Amount <= 0 ||
                    !TryParseCurrency(line.Id, out var currency))
                    return ApplyResult.Reject(RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
                deltas[i] = new CurrencyDelta(currency, line.Amount);
            }
            if (State.Inbox.ClaimedIds.Contains(g.InboxItemId))
                return ApplyResult.Reject(RejectCode.AlreadyClaimed, InboxRejectReasons.INBOX_ALREADY_APPLIED);

            State.Inbox.ClaimedIds.Add(g.InboxItemId);
            List<Banner> touched = null;
            for (int i = 0; i < g.Lines.Length; i++)
            {
                if (kinds[i] is Banner kind)
                {
                    GrantItemCopies(kind, g.Lines[i].Id, (int)g.Lines[i].Amount, g.Reason);
                    touched ??= new List<Banner>();
                    if (!touched.Contains(kind)) touched.Add(kind);
                }
                else Ledger.Apply(State, deltas[i], g.Reason, this);
            }
            if (touched != null)
            {
                touched.Sort();
                foreach (var b in touched) AchievementsOnCollection(b, CollectionRules.For(State, b));   // OWN_* ladders (06 section 5.4)
                if (touched.Contains(Banner.WEAPON)) AutoEquipWeapon(auto: true);                       // as a summon (04 open question 4)
                SummonsMarkDpsDirty();
            }
            return ApplyResult.Accepted;
        }

        /// <summary>
        /// <paramref name="amount"/> copies of one item, as ApplySummonResult applies pulls (04 section 5.3): copies up to
        /// DUPE_LVL_CUM[^1], each copy past it converted to the rarity's DUPE_OVERFLOW_ROCK_CANDY; a new item's
        /// FirstAcquiredServerTime is the claim's virtual server time. One ItemAcquired for the whole line.
        /// </summary>
        void GrantItemCopies(Banner kind, string id, int amount, CurrencyReason reason)
        {
            var col = CollectionRules.For(State, kind);
            if (!col.Items.TryGetValue(id, out var item) || item == null)
                col.Items[id] = item = new OwnedItem { FirstAcquiredServerTime = VirtualNowServerSeconds };
            var rarity = CollectionRules.RarityOf(Db, kind, id);
            bool isNew = item.Copies <= 0;
            int before = Db.DupeLevel(item.Copies);
            int added = Math.Min(Math.Max(0, PullAlgorithm.MaxCopies(Db) - item.Copies), amount);
            item.Copies += added;
            long overflow = (long)(amount - added) * Db.Rarities[rarity].DupeOverflowRockCandy;
            if (overflow > 0) Ledger.Apply(State, new CurrencyDelta(Currency.ROCK_CANDY, overflow), reason, this);
            Emit(new ItemAcquired(kind, id, rarity, isNew, before, Db.DupeLevel(item.Copies), overflow));
        }

        /// <summary>The reasons a server grant may carry (01 section 2.5, 08 section 3.2).</summary>
        public static bool IsInboxReason(CurrencyReason r) =>
            r == CurrencyReason.IAP || r == CurrencyReason.MAIL || r == CurrencyReason.REFUND || r == CurrencyReason.ADMIN;

        /// <summary>An exact Currency name ("DIAMOND"); numbers and other spellings are not currencies.</summary>
        static bool TryParseCurrency(string id, out Currency currency)
        {
            foreach (Currency v in Enum.GetValues(typeof(Currency)))
                if (string.Equals(v.ToString(), id, StringComparison.Ordinal)) { currency = v; return true; }
            currency = default;
            return false;
        }
    }
}
