using System;

namespace HeroCat.Bots
{
    /// <summary>Harness-only constants (10 section 3.2). Not game content.</summary>
    public static class HarnessConstants
    {
        /// <summary>TODO(#134): decision 25's Monday 09:00 UTC epoch with two 60-minute sessions a day.</summary>
        public static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public const int ActiveMinutesPerDay = 120;
        public const int DecisionIntervalSeconds = 60;
        public const int DefaultDays = 30;
        public const string DefaultSeeds = "1-20";
        public const ulong DefaultSeed = 7;
        public const string DefaultBot = "idle";
        public static readonly int[] SummaryDays = { 1, 2, 3, 5, 7, 10, 14, 21, 30 };

        /// <summary>The bots' install id, so a bot save is byte-stable.</summary>
        public static readonly Guid BotDeviceId = new Guid("b07b07b0-0000-4000-8000-000000000001");

        /// <summary>RngRoot for a seed: deterministic, never Guid.Empty.</summary>
        public static Guid RngRootFor(ulong seed) => new Guid((int)seed, (short)(seed >> 32), 0x4000, new byte[] { 0x80, 0, 0, 0, 0, 0, 0, 1 });
    }
}
