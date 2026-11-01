using System;

namespace HeroCat.Bots
{
    /// <summary>Harness-only constants (10 section 3.2; 00 decisions 25 and 61). Not game content.</summary>
    public static class HarnessConstants
    {
        /// <summary>A Monday 09:00 UTC, so weekly keys and the daily rollover (00:00 UTC) are both exercised.</summary>
        public static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

        public const int ActiveMinutesPerDay = 120;   // 00 section 6.6
        public const int SessionsPerDay = 2;          // two 60-minute sessions, 06 section 3

        /// <summary>
        /// Gaps after session 1 and session 2, in hours (issue #29). Odd days: 09:00-10:00 and 21:00-22:00, so the second
        /// gap spans 00:00 UTC. Even days: 09:00-10:00 and 23:30-00:30, so the rollover happens inside a running session.
        /// </summary>
        public static readonly double[] SessionGapHoursOdd = { 11.0, 11.0 };
        public static readonly double[] SessionGapHoursEven = { 13.5, 8.5 };

        public const int DecisionIntervalSeconds = 60;   // the policy decides once per simulated minute
        public const int DefaultDays = 30;
        public const string DefaultSeeds = "1-20";
        public const ulong DefaultSeed = 7;
        public const string DefaultBot = "greedy-f2p";
        public static readonly int[] SummaryDays = { 1, 2, 3, 5, 7, 10, 14, 21, 30 };
        public const double LightSpenderUsdPerMonth = 20.0;
        public const int WhaleDay1Usd = 100;   // dia_6 once on day 1, then dia_4 every 3 days (section 3.4)

        /// <summary>The bots' install id, so a bot save is byte-stable.</summary>
        public static readonly Guid BotDeviceId = new Guid("b07b07b0-0000-4000-8000-000000000001");

        /// <summary>
        /// playerId for <c>--seed N</c>: a GUID derived from the seed (10 section 3.2), never Guid.Empty. It is also the
        /// RngRoot (02 section 3.4: PlayerId when known), so a run is fully specified by (bot, seed, days, content_hash).
        /// </summary>
        public static Guid PlayerIdFor(ulong seed) => new Guid((int)seed, (short)(seed >> 32), 0x4000, new byte[] { 0x80, 0, 0, 0, 0, 0, 0, 1 });

        /// <summary>Same as <see cref="PlayerIdFor"/> (kept for callers that name the RNG root).</summary>
        public static Guid RngRootFor(ulong seed) => PlayerIdFor(seed);

        /// <summary>The gap after session <paramref name="session"/> (0-based) of bot day <paramref name="day"/> (1-based), in seconds.</summary>
        public static long SessionGapSeconds(int day, int session)
        {
            var gaps = day % 2 == 1 ? SessionGapHoursOdd : SessionGapHoursEven;
            return (long)Math.Round(gaps[session] * 3600.0);
        }

        /// <summary>Decisions per session: ActiveMinutesPerDay * 60 / DecisionIntervalSeconds / SessionsPerDay (60).</summary>
        public static int DecisionsPerSession => ActiveMinutesPerDay * 60 / DecisionIntervalSeconds / SessionsPerDay;
    }
}
