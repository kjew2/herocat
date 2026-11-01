namespace HeroCat.Runtime.Services
{
    /// <summary>
    /// The clock seam (00 contract 7, 02 section 11.1). The sim never reads a clock: Runtime reads an IClock and feeds the
    /// time in through the logged ApplyServerTime / ComputeOfflineCatchup system commands (11.2 rule 1).
    /// </summary>
    public interface IClock
    {
        /// <summary>Unix seconds, UTC, anchored to server time.</summary>
        long NowServerSeconds { get; }

        /// <summary>True only when anchored to a server observation in this process.</summary>
        bool IsAuthoritative { get; }
    }

    /// <summary>
    /// A clock served floored at the loaded save's <c>LastSavedServerTime</c> until it is authoritative (02 section 11.2
    /// rule 4). <see cref="HeroCat.Runtime.Save.LocalSaveStore"/> sets the floor after every successful load;
    /// wrappers (the client's debug OffsetClock) forward it.
    /// </summary>
    public interface IClockFloor
    {
        void SetFloor(long lastSavedServerTime);
    }

    /// <summary>Tests and the bot harness. Starts wherever you put it and only moves when told.</summary>
    public sealed class FakeClock : IClock
    {
        public FakeClock(long startServerSeconds = 0) { NowServerSeconds = startServerSeconds; }

        public long NowServerSeconds { get; private set; }
        public bool IsAuthoritative => true;

        public void Set(long serverSeconds) => NowServerSeconds = serverSeconds;
        public void Advance(long seconds) => NowServerSeconds += seconds;
    }
}
