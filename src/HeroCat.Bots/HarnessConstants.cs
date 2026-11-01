using System;

namespace HeroCat.Bots
{
    /// <summary>Harness-only constants (specs/09 section 3). Not game content.</summary>
    public static class HarnessConstants
    {
        public static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public const int ActiveMinutesPerDay = 120;
        public const int DecisionIntervalSeconds = 60;
        public const int DefaultDays = 30;
        public const string DefaultSeeds = "1-20";
        public const ulong DefaultSeed = 7;
        public static readonly int[] SummaryDays = { 1, 2, 3, 5, 7, 10, 14, 21, 30 };
    }
}
