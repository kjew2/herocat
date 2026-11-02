using System;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;

namespace HeroCat.Sim
{
    /// <summary>
    /// ApplyInboxGrant (01 section 2.5, 06 section 9, 08 section 3.2; issue #176): the one door through which a
    /// server-confirmed inbox claim enters the sim. Runtime's InboxFlow issues it only after the server (or the local
    /// inbox) confirms the claim.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>The grant line kind for a currency (08 section 3.2 wire format).</summary>
        public const string GrantKindCurrency = "currency";

        /// <summary>The grant line kind for a collection item (08 section 3.2). Not applied yet: TODO(#271).</summary>
        public const string GrantKindItem = "item";

        /// <summary>
        /// Checks, in order: BadArgument/INBOX_BAD_GRANT (no payload or id, null lines, a reason outside IAP / MAIL /
        /// REFUND / ADMIN, a line that is not a known currency with a positive amount), NotAvailable/
        /// INBOX_ITEM_GRANT_UNSUPPORTED (an item line, TODO(#271)), AlreadyClaimed/INBOX_ALREADY_APPLIED (the id is in
        /// Inbox.ClaimedIds, so a replayed or reconciled claim applies nothing twice). Then the id joins ClaimedIds and
        /// each line goes through Wallet.Apply with the payload's reason (any currency, Rock Candy and Fish Coins
        /// included, 06 rule 9.4). A payload with no lines (a refund notice) only records the id.
        /// </summary>
        internal ApplyResult HandleApplyInboxGrant(ApplyInboxGrant c)
        {
            var g = c.Grant;
            if (g == null || string.IsNullOrEmpty(g.InboxItemId) || g.Lines == null || !IsInboxReason(g.Reason))
                return ApplyResult.Reject(RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
            var deltas = new CurrencyDelta[g.Lines.Length];
            for (int i = 0; i < g.Lines.Length; i++)
            {
                var line = g.Lines[i];
                if (line == null) return ApplyResult.Reject(RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
                if (string.Equals(line.Kind, GrantKindItem, StringComparison.Ordinal))
                    return ApplyResult.Reject(RejectCode.NotAvailable, InboxRejectReasons.INBOX_ITEM_GRANT_UNSUPPORTED);
                if (!string.Equals(line.Kind, GrantKindCurrency, StringComparison.Ordinal) || line.Amount <= 0 ||
                    !TryParseCurrency(line.Id, out var currency))
                    return ApplyResult.Reject(RejectCode.BadArgument, InboxRejectReasons.INBOX_BAD_GRANT);
                deltas[i] = new CurrencyDelta(currency, line.Amount);
            }
            if (State.Inbox.ClaimedIds.Contains(g.InboxItemId))
                return ApplyResult.Reject(RejectCode.AlreadyClaimed, InboxRejectReasons.INBOX_ALREADY_APPLIED);

            State.Inbox.ClaimedIds.Add(g.InboxItemId);
            foreach (var d in deltas) Ledger.Apply(State, d, g.Reason, this);
            return ApplyResult.Accepted;
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
