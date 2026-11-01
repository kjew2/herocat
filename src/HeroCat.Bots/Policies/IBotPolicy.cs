using System;
using HeroCat.Bots.Measure;
using HeroCat.Runtime.Services;
using HeroCat.Runtime.Session;
using HeroCat.Runtime.Summons;
using HeroCat.Sim;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Power;
using HeroCat.Sim.State;

namespace HeroCat.Bots.Policies
{
    /// <summary>
    /// What a policy sees (10 section 3.3): the session runner that owns the Simulation, the clock, the local seams and the
    /// sink. Policies mutate only through <see cref="Apply"/> and <see cref="Summon"/> (00 contract 5) and read through
    /// <see cref="Query"/> and <see cref="State"/> (read-only by convention; never written).
    /// </summary>
    public sealed class BotContext
    {
        readonly ulong _seed;
        long _requests;

        public BotContext(SessionRunner runner, FakeClock clock, BotEventSink sink, ulong seed)
        {
            Runner = runner ?? throw new ArgumentNullException(nameof(runner));
            Clock = clock;
            Sink = sink;
            _seed = seed;
        }

        public SessionRunner Runner { get; }
        public Simulation Sim => Runner.Sim;
        public GameState State => Runner.Sim.State;
        public ContentDb Content => Runner.Sim.Db;
        public SimQuery Query => Runner.Sim.Query;
        public GameServices Seams => Runner.Services;
        public FakeClock Clock { get; }
        public BotEventSink Sink { get; }

        /// <summary>Bot day, 1-based.</summary>
        public int Day { get; set; }

        /// <summary>Session of the day, 0-based.</summary>
        public int Session { get; set; }

        /// <summary>Apply through the one door, logged by the sim (00 contract 5); a reject is published on the bus.</summary>
        public ApplyResult Apply(Command c) => Runner.Issue(c);

        /// <summary>
        /// The Runtime summon flow (01 section 8.5) on the local seam: LocalSummonService rolls on a clone, the payload is
        /// applied with the logged ApplySummonResult. The request id is derived from (seed, request counter) so a run is
        /// reproducible. Returns false when the service refuses (not enough currency, banner closed).
        /// </summary>
        public bool Summon(Banner banner, int count, SummonPayment payment)
        {
            var svc = Seams.Summon as LocalSummonService ?? throw new InvalidOperationException("bots need the LocalSummonService seam");
            var req = new SummonRequest(RequestId(), banner, count, payment);
            var r = svc.Pull(req);
            if (!r.IsOk) return false;
            var applied = Apply(new ApplySummonResult(r.Value));
            if (!applied.Ok) throw new InvalidOperationException("ApplySummonResult rejected a local payload: " + applied);
            return true;
        }

        /// <summary>A GUID in "N" format from (seed, counter): deterministic, unique within the run.</summary>
        string RequestId()
        {
            long n = ++_requests;
            var b = new byte[16];
            BitConverter.GetBytes(_seed).CopyTo(b, 0);
            BitConverter.GetBytes(n).CopyTo(b, 8);
            return new Guid(b).ToString("N");
        }
    }

    /// <summary>A bot (10 section 3.4).</summary>
    public interface IBotPolicy
    {
        /// <summary>CLI id and golden-file component.</summary>
        string Name { get; }

        /// <summary>After the session's resume (catch-up, rollover): claim, pull tickets, spend.</summary>
        void OnSessionStart(BotContext ctx);

        /// <summary>Once per DecisionIntervalSeconds of active play.</summary>
        void Decide(BotContext ctx);

        /// <summary>Before the session suspends: spend what is left, release the tank.</summary>
        void OnSessionEnd(BotContext ctx);
    }
}
