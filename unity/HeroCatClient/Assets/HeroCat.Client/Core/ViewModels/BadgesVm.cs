using System;
using HeroCat.Runtime.Services;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// The red-dot policy (07 section 10 rule 2; issue #180). A dot means "a free thing is waiting": a claimable quest, an
    /// unused dungeon ticket, an unclaimed inbox item, an affordable Research node, an unseen new collectible, the free daily
    /// gift. Never "you could spend money" and never affordable Training (it would be permanent), so no count here reads Gold
    /// or the Training cost. Every count is recomputed from state on each refresh, so a dot clears in the same refresh as the
    /// claim (or the "seen" mark) that consumed it, and never on a timer.
    ///
    /// A thing behind a locked <c>feature_unlocks</c> gate, or a feature whose kill switch is off, cannot be taken, so it
    /// shows no dot (the Locked state names the condition instead, <see cref="LockedVm"/>). Allocation-free (07 AC 12).
    /// </summary>
    public sealed class BadgesVm
    {
        const string Quests = "QUESTS", Dungeons = "DUNGEONS", Shop = "SHOP";

        static readonly ResearchEffect[] Effects = (ResearchEffect[])Enum.GetValues(typeof(ResearchEffect));
        static readonly Dungeon[] DungeonKinds = (Dungeon[])Enum.GetValues(typeof(Dungeon));

        readonly VmContext _c;

        public BadgesVm(VmContext c) { _c = c; }

        /// <summary>Daily, weekly and achievement rows in COMPLETED (claimable) state; 0 while Quests are locked.</summary>
        public readonly BadgeDot QuestsClaimable = new BadgeDot();

        /// <summary>Dungeon tickets held across the dungeons; 0 while Dungeons are locked or kill-switched off.</summary>
        public readonly BadgeDot DungeonTicketsUnused = new BadgeDot();

        /// <summary>Available unclaimed inbox items (06 rule 9.5), from Runtime's inbox flow.</summary>
        public readonly BadgeDot InboxUnclaimed = new BadgeDot();

        /// <summary>Research nodes a <c>Research</c> command would accept now; 0 while Research is locked.</summary>
        public readonly BadgeDot ResearchAffordable = new BadgeDot();

        /// <summary>Owned items marked new in ClientPrefs and not seen yet (issue #47), all three collections.</summary>
        public readonly BadgeDot NewCollectibles = new BadgeDot();

        /// <summary>1 while today's free daily gift is unclaimed and the Shop is open (06 section 8).</summary>
        public readonly BadgeDot DailyGiftFree = new BadgeDot();

        public void Refresh(Simulation sim, int inboxUnclaimed, int newCollectibles)
        {
            var db = sim.Db;
            var st = sim.State;
            var sw = _c.Switches;
            QuestsClaimable.Set(ClaimableQuests(sim));
            DungeonTicketsUnused.Set(UnusedDungeonTickets(db, st, sw));
            InboxUnclaimed.Set(inboxUnclaimed);
            ResearchAffordable.Set(AffordableResearch(db, st));
            NewCollectibles.Set(newCollectibles);
            DailyGiftFree.Set(FreeDailyGift(db, st, sw) ? 1 : 0);
        }

        /// <summary>COMPLETED rows across the three quest kinds (a missing entry is IN_PROGRESS, 02 rule P12).</summary>
        public static int ClaimableQuests(Simulation sim)
        {
            if (!sim.Db.IsUnlocked(Quests, sim.State)) return 0;
            return Completed(sim, QuestKind.DAILY) + Completed(sim, QuestKind.WEEKLY) + Completed(sim, QuestKind.ACHIEVEMENT);
        }

        static int Completed(Simulation sim, QuestKind kind)
        {
            var defs = sim.Db.QuestsOf(kind);
            int n = 0;
            for (int i = 0; i < defs.Count; i++)
                if (sim.QuestStateOf(defs[i]) == QuestStatus.COMPLETED) n++;
            return n;
        }

        /// <summary>The ticket balances of every dungeon this build has (bought and mailed tickets count too).</summary>
        public static int UnusedDungeonTickets(ContentDb db, GameState s, KillSwitches sw)
        {
            if (sw != null && !sw.DungeonsEnabled) return 0;
            if (!db.IsUnlocked(Dungeons, s)) return 0;
            int n = 0;
            for (int i = 0; i < DungeonKinds.Length; i++)
            {
                if (db.DungeonOf(DungeonKinds[i])?.TicketCurrency is Currency ticket)
                {
                    double have = s.Wallet.Get(ticket);
                    if (have > 0) n += (int)Math.Min(have, int.MaxValue - n);
                }
            }
            return n;
        }

        /// <summary>Nodes <see cref="ResearchRules.CanResearch"/> accepts now (open, below max level, affordable).</summary>
        public static int AffordableResearch(ContentDb db, GameState s)
        {
            int n = 0;
            for (int i = 0; i < Effects.Length; i++)
            {
                var node = db.ResearchFor(Effects[i]);   // one node per effect (09 E046); no dictionary enumerator
                if (node != null && ResearchRules.CanResearch(db, s, node.Id)) n++;
            }
            return n;
        }

        /// <summary>ClaimDailyGift would be accepted: Shop open and switched on, a gift in content, not claimed today.</summary>
        public static bool FreeDailyGift(ContentDb db, GameState s, KillSwitches sw)
        {
            if (sw != null && !sw.ShopEnabled) return false;
            if (!db.IsUnlocked(Shop, s)) return false;
            return db.Constants.DailyGiftWeaponTickets > 0 && !s.Shop.DailyGiftClaimed;
        }
    }
}
