using System.Diagnostics;
using OpenTelemetry.Trace;

namespace HeroCat.Server.Observability;

/// <summary>
/// Trace sampling of specs/08 section 9.4: 10% of traces (<see cref="TelemetryOptions.TraceSamplePct"/>), and every
/// trace whose request ended 5xx. A root span outside the sampled share is still recorded (not exported) so that
/// <see cref="KeepOnError"/> can mark it sampled when the request fails; its child spans follow the parent's decision
/// when they start, so a kept error trace holds its root span (with the status, route and exception) only.
/// A client's <c>traceparent</c> cannot force sampling: a remote parent's flag is ignored and the ratio applies.
/// </summary>
public sealed class ErrorKeepingSampler : Sampler
{
    readonly TraceIdRatioBasedSampler _ratio;

    public ErrorKeepingSampler(double probability)
    {
        _ratio = new TraceIdRatioBasedSampler(Math.Clamp(probability, 0, 1));
        Description = $"ErrorKeepingSampler{{{probability:0.###}}}";
    }

    public override SamplingResult ShouldSample(in SamplingParameters p)
    {
        if (p.ParentContext.TraceId != default && !p.ParentContext.IsRemote)
            return new SamplingResult((p.ParentContext.TraceFlags & ActivityTraceFlags.Recorded) != 0 ? SamplingDecision.RecordAndSample : SamplingDecision.Drop);
        return _ratio.ShouldSample(p).Decision == SamplingDecision.RecordAndSample
            ? new SamplingResult(SamplingDecision.RecordAndSample)
            : new SamplingResult(SamplingDecision.RecordOnly);
    }
}

public static class TraceSampling
{
    /// <summary>Marks a recorded-only span sampled, so the exporter ships it when it ends (a request that ended 5xx).</summary>
    public static void KeepOnError(Activity? activity)
    {
        if (activity is { IsAllDataRequested: true, Recorded: false }) activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
    }
}
