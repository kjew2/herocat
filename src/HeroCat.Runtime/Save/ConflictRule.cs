using System;
using System.Collections.Generic;
using HeroCat.Sim.Content;

namespace HeroCat.Runtime.Save
{
    /// <summary>Which candidate of a cloud-save decision (02 section 9.3): L is this device's, R the server's.</summary>
    public enum SaveSide { Local, Remote }

    /// <summary>The step of 02 section 9.3's <c>Resolve(L, R)</c> that decided.</summary>
    public enum ConflictBranch
    {
        /// <summary>Step 1: only L exists.</summary>
        OnlyLocal,

        /// <summary>Step 1: only R exists (written locally).</summary>
        OnlyRemote,

        /// <summary>Step 2: equal sha256, L is used.</summary>
        SameContent,

        /// <summary>Step 3: same deviceId, the higher saveSeq wins silently.</summary>
        SameDevice,

        /// <summary>Step 4: different devices, by stamp, clock authority and saveSeq; the loser may raise the modal.</summary>
        DifferentDevices,
    }

    /// <summary>Why step 4's loser raised the modal (the first test that held, in 9.3's order).</summary>
    public enum ConflictReason { None, ProgressScore, TotalPulls, Diamonds }

    /// <summary>The <c>summary</c> block of 02 section 9.1 (also the modal's card data, 9.4).</summary>
    public sealed class SaveSummary
    {
        public int Stage { get; set; }
        public int PartyLevel { get; set; }
        public long Diamonds { get; set; }
        public long TotalPulls { get; set; }
        public int HighestFish { get; set; }

        /// <summary><c>Party.Slots[i].AwakenRank</c> in slot order.</summary>
        public IReadOnlyList<int> AwakenRanks { get; set; } = Array.Empty<int>();
    }

    /// <summary>The result of <see cref="ConflictRule.Resolve"/>: who wins and whether the player is asked.</summary>
    public sealed class ConflictDecision
    {
        public ConflictDecision(ConflictBranch branch, SaveSide winner, ConflictReason reason)
        {
            Branch = branch; Winner = winner; Reason = reason;
        }

        public ConflictBranch Branch { get; }

        /// <summary>The rule's winner: the save used silently, or the modal's preselected "Recommended" card.</summary>
        public SaveSide Winner { get; }

        public SaveSide Loser => Winner == SaveSide.Local ? SaveSide.Remote : SaveSide.Local;

        /// <summary>None unless the loser raised the modal.</summary>
        public ConflictReason Reason { get; }

        /// <summary>Conflict: both states are returned and the modal asks (9.4). Else Loaded: the winner is used silently.</summary>
        public LoadStatus Status => Reason == ConflictReason.None ? LoadStatus.Loaded : LoadStatus.Conflict;

        public bool IsConflict => Status == LoadStatus.Conflict;

        /// <summary>Step 4 used its winner without asking: <c>cloud_autoresolved{winnerSide}</c> (9.3).</summary>
        public bool AutoResolved => Branch == ConflictBranch.DifferentDevices && !IsConflict;

        /// <summary>R is used silently, so the local copy must become R ("R only: write locally", and any silent R win).</summary>
        public bool UsesRemote => !IsConflict && Winner == SaveSide.Remote;

        public override string ToString() => $"{Branch} winner={Winner} status={Status} reason={Reason}";
    }

    /// <summary>
    /// 02 section 9.3, exactly: last-writer-wins by server stamp with clock authority and a progress override that asks
    /// the player. Pure: metadata in, decision out; fetching, writing and analytics are <see cref="CloudConflictResolver"/>'s.
    /// </summary>
    public static class ConflictRule
    {
        /// <summary>The analytics spelling of a side (<c>winnerSide</c>, <c>chosenSide</c>).</summary>
        public static string SideName(SaveSide side) => side == SaveSide.Local ? "local" : "remote";

        /// <summary><c>PULL_COST[WEAPON]</c> from content (00 section 4): the diamond lead that raises the modal.</summary>
        public static long WeaponPullCost(ContentDb db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (!db.Banners.TryGetValue(Sim.Content.Banner.WEAPON, out var b))
                throw new InvalidOperationException("content has no WEAPON banner (PULL_COST[WEAPON])");
            return b.PullCostDiamonds;
        }

        public static ConflictDecision Resolve(SaveMeta l, SaveMeta r, ContentDb db) => Resolve(l, r, WeaponPullCost(db));

        /// <param name="l">The local candidate (02 section 8.3), or null when there is none.</param>
        /// <param name="r">The remote candidate (<c>GET /save/meta</c>), or null when the server has none.</param>
        /// <param name="weaponPullCost"><c>PULL_COST[WEAPON]</c>.</param>
        public static ConflictDecision Resolve(SaveMeta l, SaveMeta r, long weaponPullCost)
        {
            // 1. only one exists
            if (l == null && r == null) throw new ArgumentException("Resolve needs at least one candidate");
            if (r == null) return new ConflictDecision(ConflictBranch.OnlyLocal, SaveSide.Local, ConflictReason.None);
            if (l == null) return new ConflictDecision(ConflictBranch.OnlyRemote, SaveSide.Remote, ConflictReason.None);

            // 2. same content
            if (!string.IsNullOrEmpty(l.Sha256) && string.Equals(l.Sha256, r.Sha256, StringComparison.OrdinalIgnoreCase))
                return new ConflictDecision(ConflictBranch.SameContent, SaveSide.Local, ConflictReason.None);

            // 3. same lineage: the higher saveSeq wins silently (equal seqs with different bytes keep L, the device's own copy)
            if (l.DeviceId == r.DeviceId)
                return new ConflictDecision(ConflictBranch.SameDevice, r.SaveSeq > l.SaveSeq ? SaveSide.Remote : SaveSide.Local,
                                            ConflictReason.None);

            // 4. different devices
            SaveSide winner;
            if (l.LastSavedServerTime != r.LastSavedServerTime)
                winner = l.LastSavedServerTime > r.LastSavedServerTime ? SaveSide.Local : SaveSide.Remote;
            else
                winner = l.SaveSeq > r.SaveSeq ? SaveSide.Local : SaveSide.Remote;   // tie: higher saveSeq, then R
            if (!l.ClockAuthoritative && r.ClockAuthoritative) winner = SaveSide.Remote;
            if (!r.ClockAuthoritative && l.ClockAuthoritative) winner = SaveSide.Local;

            var w = winner == SaveSide.Local ? l : r;
            var lo = winner == SaveSide.Local ? r : l;
            var ws = w.Summary ?? new SaveSummary();
            var ls = lo.Summary ?? new SaveSummary();
            ConflictReason reason = ConflictReason.None;
            if (lo.ProgressScore > w.ProgressScore * (1 + SaveConfig.CONFLICT_PROGRESS_MARGIN)) reason = ConflictReason.ProgressScore;
            else if (ls.TotalPulls > ws.TotalPulls) reason = ConflictReason.TotalPulls;
            else if (ls.Diamonds >= ws.Diamonds + weaponPullCost) reason = ConflictReason.Diamonds;
            return new ConflictDecision(ConflictBranch.DifferentDevices, winner, reason);
        }
    }
}
