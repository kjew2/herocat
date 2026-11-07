using System;

namespace HeroCat.Runtime.Save
{
    /// <summary>One card of the conflict modal (02 section 9.4): a candidate's summary as the rule saw it.</summary>
    public sealed class ConflictCard
    {
        public SaveSide Side { get; set; }

        /// <summary>"This device" (L) or "Other device" (R).</summary>
        public bool IsThisDevice => Side == SaveSide.Local;

        /// <summary>The rule's winner, preselected and labelled "Recommended".</summary>
        public bool Recommended { get; set; }

        public int Stage { get; set; }
        public int PartyLevel { get; set; }
        public long Diamonds { get; set; }
        public long TotalPulls { get; set; }
        public int HighestFish { get; set; }

        /// <summary>Server seconds of the candidate's last save; the card shows it as relative time.</summary>
        public long LastPlayedServerTime { get; set; }

        /// <summary>The device model name (R: from <c>devices</c>, 08; L: this device's). Null when unknown.</summary>
        public string DeviceModel { get; set; }

        /// <summary>Seconds since the last save, never negative (the card's relative time).</summary>
        public long LastPlayedAgoSec(long nowServerSec) => Math.Max(0, nowServerSec - LastPlayedServerTime);
    }

    /// <summary>
    /// The conflict modal's data (02 section 9.4; 07 renders it): two cards with the rule's winner preselected, and the
    /// warning line shown when the other device's save is selected ("Progress on this device since &lt;time&gt; will be
    /// lost", <see cref="LocalProgressSinceServerTime"/>). Engine-free; the client binds it.
    /// </summary>
    public sealed class ConflictModalModel
    {
        ConflictModalModel(ConflictCard thisDevice, ConflictCard otherDevice, SaveSide recommended, long since)
        {
            ThisDevice = thisDevice; OtherDevice = otherDevice; Recommended = recommended; LocalProgressSinceServerTime = since;
        }

        public ConflictCard ThisDevice { get; }
        public ConflictCard OtherDevice { get; }

        /// <summary>The rule's winner: the initial selection.</summary>
        public SaveSide Recommended { get; }

        /// <summary>
        /// The "since &lt;time&gt;" of the warning line: when L was loaded and the conflict found. What runs on L after it is
        /// the price of choosing the other device (open question 3's default: replace immediately).
        /// </summary>
        public long LocalProgressSinceServerTime { get; }

        /// <summary>Whether the warning line shows for a selection: only when the other device's save is selected.</summary>
        public bool ShowsLossLine(SaveSide selected) => selected == SaveSide.Remote;

        public ConflictCard Card(SaveSide side) => side == SaveSide.Local ? ThisDevice : OtherDevice;

        public static ConflictModalModel From(PendingConflict p, string thisDeviceModel = null)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            var win = p.Decision.Winner;
            return new ConflictModalModel(CardOf(SaveSide.Local, p.LocalMeta, win, thisDeviceModel ?? p.LocalMeta?.DeviceModel),
                                          CardOf(SaveSide.Remote, p.RemoteMeta, win, p.RemoteMeta?.DeviceModel),
                                          win, p.DetectedAtServerTime);
        }

        static ConflictCard CardOf(SaveSide side, SaveMeta m, SaveSide winner, string model)
        {
            var s = m?.Summary ?? new SaveSummary();
            return new ConflictCard
            {
                Side = side, Recommended = side == winner, Stage = s.Stage, PartyLevel = s.PartyLevel, Diamonds = s.Diamonds,
                TotalPulls = s.TotalPulls, HighestFish = s.HighestFish, LastPlayedServerTime = m?.LastSavedServerTime ?? 0,
                DeviceModel = model,
            };
        }
    }

    /// <summary>What is on screen when the client asks whether the conflict modal may open (02 section 9.4).</summary>
    public readonly struct ConflictModalContext
    {
        public ConflictModalContext(bool onMainScreen, bool bossActive, bool revealActive, bool dungeonRunActive, bool otherModalOpen)
        {
            OnMainScreen = onMainScreen; BossActive = bossActive; RevealActive = revealActive; DungeonRunActive = dungeonRunActive;
            OtherModalOpen = otherModalOpen;
        }

        /// <summary>SCR_MAIN is the top screen.</summary>
        public bool OnMainScreen { get; }
        public bool BossActive { get; }
        public bool RevealActive { get; }
        public bool DungeonRunActive { get; }
        public bool OtherModalOpen { get; }
    }

    public static class ConflictModalRules
    {
        /// <summary>
        /// 02 section 9.4: only on SCR_MAIN idle, never during a boss, a reveal, a dungeon run or over another modal. Until
        /// it shows, the game runs on L and uploads stay suspended (<see cref="CloudConflictResolver.UploadsSuspended"/>).
        /// </summary>
        public static bool CanShow(in ConflictModalContext c) =>
            c.OnMainScreen && !c.BossActive && !c.RevealActive && !c.DungeonRunActive && !c.OtherModalOpen;
    }
}
