namespace HeroCat.Sim
{
    /// <summary>
    /// The canonical day and week keys (00 contract 7, decision 21; 06 section 4.1; issue #107):
    /// <c>dayKey = floor(serverSeconds / DAY_SECONDS)</c> (00:00 UTC) and <c>weekKey = floor((dayKey - 4) / 7)</c>
    /// (Monday 00:00 UTC: 1970-01-01, day 0, was a Thursday, so day 4 is the first Monday and has week key 0).
    /// Both divisions floor, so keys before the epoch are correct too. DAY_SECONDS comes from ContentDb.
    /// </summary>
    public static class DayKeys
    {
        /// <summary>Day 4 (1970-01-05) was the first Monday.</summary>
        public const int FirstMondayDayKey = 4;

        public const int DaysPerWeek = 7;

        public static int DayKey(long serverSeconds, int daySeconds) => (int)FloorDiv(serverSeconds, daySeconds);

        public static int WeekKey(int dayKey) => (int)FloorDiv((long)dayKey - FirstMondayDayKey, DaysPerWeek);

        public static int WeekKeyAt(long serverSeconds, int daySeconds) => WeekKey(DayKey(serverSeconds, daySeconds));

        /// <summary>Unix seconds at which <paramref name="dayKey"/> starts.</summary>
        public static long DayStart(int dayKey, int daySeconds) => (long)dayKey * daySeconds;

        internal static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
