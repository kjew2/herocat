using System.Data.Common;
using HeroCat.Server.Hosting;
using HeroCat.Server.Pipeline;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Features.Save;

/// <summary>An accepted upload as the plausibility module sees it (specs/08 section 3.1 steps 5a-6).</summary>
public sealed record SaveUpload(Guid PlayerId, Guid DeviceId, long SaveSeq, byte[] File, SaveEnvelope Envelope, SaveMetaDto Meta,
                                StoredSave? Previous, DateTimeOffset Now);

/// <summary>What the inline step returns: extra flags and the diamond reconciliation for the response.</summary>
public sealed record InlinePlausibility(IReadOnlyList<PlausibilityFlag> Flags, DiamondReconcileDto? DiamondReconcile)
{
    public static readonly InlinePlausibility None = new(Array.Empty<PlausibilityFlag>(), null);
}

/// <summary>
/// The plausibility hook of <c>PUT /save</c> (specs/08 section 3.1 steps 5a and 6, section 7.2). <see cref="RunInlineAsync"/>
/// runs inside the upload's transaction before the save is written: crediting client-earned currency and the
/// <c>DIAMOND_LEDGER_MISMATCH</c> check, whose reconciliation is in the response. <see cref="AfterCommitAsync"/> hands the
/// accepted upload to the asynchronous checks.
/// </summary>
public interface ISavePlausibility
{
    Task<InlinePlausibility> RunInlineAsync(SaveUpload upload, DbConnection connection, DbTransaction transaction, CancellationToken ct);
    Task AfterCommitAsync(SaveUpload upload, IReadOnlyList<PlausibilityFlag> flags, CancellationToken ct);
}

/// <summary>No checks beyond the ones the upload steps raise themselves: the default until the anti-cheat module lands.</summary>
public sealed class NoSavePlausibility : ISavePlausibility
{
    // TODO(#206): step 5a crediting, DIAMOND_LEDGER_MISMATCH inline, the other section 7.2 checks via the worker.
    public Task<InlinePlausibility> RunInlineAsync(SaveUpload upload, DbConnection connection, DbTransaction transaction, CancellationToken ct) =>
        Task.FromResult(InlinePlausibility.None);

    public Task AfterCommitAsync(SaveUpload upload, IReadOnlyList<PlausibilityFlag> flags, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The re-simulation sampler (specs/08 section 7.3): <c>RESIM_SAMPLE_PCT</c> percent of uploads at random plus every
/// upload carrying a <c>suspicious</c> flag. A sampled upload sets <c>saves.cmdlog_requested</c>, so this and every later
/// <c>PUT /save</c> response carries <c>requestCmdLog: true</c> until an upload includes the log.
/// </summary>
public interface IResimSampler
{
    bool Sample(IReadOnlyList<PlausibilityFlag> flags);
}

public sealed class ResimSampler : IResimSampler
{
    readonly double _pct;
    readonly Func<double> _uniform;

    public ResimSampler(IOptions<ServerOptions> options) : this(options.Value.Limits.ResimSamplePct, Random.Shared.NextDouble) { }

    public ResimSampler(double pct, Func<double> uniform)
    {
        _pct = pct;
        _uniform = uniform;
    }

    public bool Sample(IReadOnlyList<PlausibilityFlag> flags) =>
        flags.Any(f => f.Severity == PlausibilityFlag.Suspicious) || _uniform() * 100 < _pct;
}
