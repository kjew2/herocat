namespace HeroCat.Bots.Measure
{
    /// <summary>
    /// One row of the day table, snapshotted after the night's sync. TODO(#134): 10 section 3.5's measurements
    /// (DPS, layer shares, walls, pulls) once the systems that produce them land.
    /// </summary>
    public sealed record DayRow(int Day, int Stage, int HighestCleared, int HeroLevel, double Gold, long Diamonds, double FishCoin,
                                long Pulls, string StateHash);
}
