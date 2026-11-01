using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;

namespace HeroCat.Sim.Commands
{
    /// <summary>
    /// A player or system intent fed to <see cref="Simulation.Apply"/> (01 section 2.1), named VerbNoun (00 section 5.1).
    ///
    /// Dispatch: each command type implements <see cref="Execute"/>, so adding a command touches one file (its record and
    /// its handler) and no central switch. Execute is internal, so only the sim assembly can define commands; Runtime,
    /// bots and the client construct them. A handler validates first and either returns a reject with no state change
    /// and no event, or mutates and emits (01 section 2.2 rules 1-2). It never throws on bad input.
    /// </summary>
    public abstract record Command
    {
        /// <summary>Simulation.Tick at the moment of Apply; set by Simulation, not the caller.</summary>
        public long IssuedAtTick { get; init; }

        internal abstract ApplyResult Execute(Simulation sim);
    }

    /// <summary>Reject category (01 section 2.1); picks the UI presentation. The reason string is the owning spec's.</summary>
    public enum RejectCode
    {
        None, CannotAfford, InvalidTarget, Locked, Cooldown, Capped, AlreadyClaimed, NotOwned,
        SlotOccupied, BoardFull, TankEmpty, LevelMismatch, NotAvailable, RateLimited, BadArgument, StaleRequest,
    }

    /// <summary>
    /// The result of Apply. A readonly struct rather than 01's <c>readonly record struct</c>, which is C# 10 (issue #64).
    /// Reason is an UPPER_SNAKE key from the owning spec's closed list, localized as <c>reject.&lt;reason&gt;</c>.
    /// </summary>
    public readonly struct ApplyResult
    {
        public ApplyResult(bool ok, RejectCode code, string reason)
        {
            Ok = ok;
            Code = code;
            Reason = reason;
        }

        public bool Ok { get; }
        public RejectCode Code { get; }
        public string Reason { get; }

        public static readonly ApplyResult Accepted = new ApplyResult(true, RejectCode.None, null);

        public static ApplyResult Reject(RejectCode code, string reason = null) => new ApplyResult(false, code, reason);

        public override string ToString() => Ok ? "Accepted" : "Reject(" + Code + ", " + (Reason ?? "-") + ")";
    }

    /// <summary>Reject reasons the sim's skeleton owns until the owning issues replace them.</summary>
    public static class SimRejectReasons
    {
        /// <summary>The command type exists (its shape is fixed) but its body belongs to a later issue.</summary>
        public const string NOT_IMPLEMENTED = "NOT_IMPLEMENTED";

        /// <summary>A system command carried a negative server time.</summary>
        public const string BAD_SERVER_TIME = "BAD_SERVER_TIME";
    }

    // ------------------------------------------------------------------------------------- system commands (01)

    /// <summary>
    /// Re-anchors the virtual server clock and runs EnsureDay (01 section 3.1, 06 section 4.2; issue #5). Issued by
    /// Runtime at boot, resume, gap recovery and every WALL_CLOCK_SYNC_SEC.
    /// </summary>
    public sealed record ApplyServerTime(long ServerSeconds) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleApplyServerTime(this);
    }

    /// <summary>
    /// Offline catch-up compute (01 section 8.7, 06 section 7). Stores or sums into Offline.Pending and stamps
    /// Time.LastTickServerTime. TODO(#108): the skeleton only stamps the anchor; nothing is credited yet.
    /// </summary>
    public sealed record ComputeOfflineCatchup(long NowServerSeconds) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleComputeOfflineCatchup(this);
    }

    /// <summary>The only summon mutator (01 section 2.5, 04 section 10). TODO(#91): the body.</summary>
    public sealed record ApplySummonResult(SummonResultPayload Payload) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => ApplyResult.Reject(RejectCode.NotAvailable, SimRejectReasons.NOT_IMPLEMENTED);
    }

    /// <summary>Server-granted items (01 section 2.5, 06 section 9). TODO(#176): the body.</summary>
    public sealed record ApplyInboxGrant(InboxGrantPayload Grant) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => ApplyResult.Reject(RejectCode.NotAvailable, SimRejectReasons.NOT_IMPLEMENTED);
    }

    // ------------------------------------------------------------------------------------- service payloads (01 2.5)

    public sealed record SummonResultPayload(
        string RequestId,                 // client-generated GUID "N" format, 32 hex chars
        Banner Banner,
        int Count,                        // 1 or 10
        SummonPull[] Pulls,               // length == Count
        int PityAfter,                    // pulls since last L+ after this batch
        int BannerLevelAfter, int BannerExpAfter,
        CurrencyDelta[] Debits,           // what the pull costs: DIAMOND or a ticket; ApplySummonResult performs the debit
        CurrencyDelta[] BalancesAfter,    // Remote: the server's balances for the debited currencies; null in Local
        long? StreamCounterAfter,         // Local: summon_<banner>.Counter after the pull; null in Remote
        long ServerTimeSec);

    public sealed record SummonPull(string ItemId, Rarity Rarity, bool IsNew, int LevelBefore, int LevelAfter,
                                    int CopiesAfter,           // capped at 160 (04 section 5.3)
                                    long OverflowRockCandy,    // Rock Candy from a copy past the cap, else 0
                                    bool GuaranteeApplied);

    /// <summary>Reason in {IAP, MAIL, REFUND, ADMIN}.</summary>
    public sealed record InboxGrantPayload(string InboxItemId, GrantLine[] Lines, CurrencyReason Reason);

    /// <summary>Kind: "currency" | "item".</summary>
    public sealed record GrantLine(string Kind, string Id, long Amount);
}
