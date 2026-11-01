using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Content;
using HeroCat.Sim.Summons;

namespace HeroCat.Runtime.Summons
{
    // The summon seam of 04 section 12 (00 section 8 item 11). Sealed classes rather than 04's records: Runtime has no
    // IsExternalInit polyfill and the shapes are immutable either way.

    public enum SummonPayment { Diamonds, Tickets }

    public enum SummonError { None, NotEnoughDiamonds, NotEnoughTickets, BannerClosed, RateLimited, Network, Replayed }

    /// <summary>One request, 1 or 10 pulls (04 section 2). RequestId: a GUID in "N" format (01 section 2.5).</summary>
    public sealed class SummonRequest
    {
        public SummonRequest(string requestId, Banner banner, int count, SummonPayment payment)
        {
            RequestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
            Banner = banner;
            Count = count;
            Payment = payment;
        }

        public string RequestId { get; }
        public Banner Banner { get; }
        public int Count { get; }
        public SummonPayment Payment { get; }

        public static SummonRequest New(Banner banner, int count, SummonPayment payment) =>
            new SummonRequest(Guid.NewGuid().ToString("N"), banner, count, payment);
    }

    /// <summary>A value or an error (04 section 12's <c>Result&lt;T, E&gt;</c>).</summary>
    public readonly struct Result<T, TError>
    {
        Result(bool ok, T value, TError error)
        {
            IsOk = ok;
            Value = value;
            Error = error;
        }

        public bool IsOk { get; }
        public T Value { get; }
        public TError Error { get; }

        public static Result<T, TError> Ok(T value) => new Result<T, TError>(true, value, default);
        public static Result<T, TError> Fail(TError error) => new Result<T, TError>(false, default, error);

        public override string ToString() => IsOk ? "Ok(" + Value + ")" : "Fail(" + Error + ")";
    }

    /// <summary>The published odds of one banner (SCR_SUMMON), every level's row plus the pity rule.</summary>
    public sealed class RateTableDto
    {
        public RateTableDto(Banner banner, string contentVersion, IReadOnlyList<RateTableRow> levels, int pitySoftStart,
                            double pitySoftStep, int pityHard, double mShareOfLplus)
        {
            Banner = banner; ContentVersion = contentVersion; Levels = levels; PitySoftStart = pitySoftStart;
            PitySoftStep = pitySoftStep; PityHard = pityHard; MShareOfLplus = mShareOfLplus;
        }

        public Banner Banner { get; }
        public string ContentVersion { get; }
        public IReadOnlyList<RateTableRow> Levels { get; }
        public int PitySoftStart { get; }
        public double PitySoftStep { get; }
        public int PityHard { get; }
        public double MShareOfLplus { get; }
    }

    /// <summary>
    /// M0: <see cref="LocalSummonService"/>; MVP: RemoteSummonService (TODO(#145)). Either way the caller applies the
    /// payload with the logged <see cref="ApplySummonResult"/> system command (04 section 10).
    /// </summary>
    public interface ISummonService
    {
        Task<Result<SummonResultPayload, SummonError>> PullAsync(SummonRequest request, CancellationToken ct);

        Task<RateTableDto> GetRateTableAsync(Banner banner, CancellationToken ct);
    }
}
