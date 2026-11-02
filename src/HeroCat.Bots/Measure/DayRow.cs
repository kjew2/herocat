using HeroCat.Sim.Content;

namespace HeroCat.Bots.Measure
{
    /// <summary>
    /// One row of the day table (10 section 3.5), snapshotted after the day's second session. Currency and pull columns
    /// are cumulative since profile creation; <see cref="DiamondsEarned"/> counts earned diamonds only (TUTORIAL, LOGIN,
    /// FIRST_CLEAR, QUEST, ...), never bought ones (reason IAP, which <see cref="UsdSpent"/> accounts for).
    /// <see cref="GoldEarned"/> and <see cref="StateHash"/> are harness additions (the acceptance table's goldEarned column;
    /// the end-of-day StateHash for the golden tables). <see cref="AwakenRankMax"/> and <see cref="AwakenRankSum"/> are the
    /// highest and the summed party slot ranks (10 section 3.5, 00 decision 109).
    /// </summary>
    public sealed record DayRow(
        int Day, int Stage, int HighestCleared, double Dps,
        double Gold, double GoldEarned, double GoldSpent, long Diamonds, long DiamondsEarned, long DiamondsSpent, double UsdSpent,
        int Pulls, int LPlus, Rarity? HighestRarity, int PartyLevel, int AwakenRankMax, int AwakenRankSum,
        int FishMaxLevel, double FishCoins, long RockCandy,
        int FirstWallStage, double LongestWallHours, double CurrentWallHours,
        LayerShares Shares, string StateHash)
    {
        /// <summary>The day's Fish Board columns (05 section 8.2, issue #105); not in the golden tables.</summary>
        public FishDayStats Fish { get; init; }
    }
}
