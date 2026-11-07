using System.Diagnostics;
using HeroCat.Server.Observability;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenTelemetry.Trace;

namespace HeroCat.Server.Tests.Observability;

/// <summary>Traces are sampled at <c>TraceSamplePct</c> (10%), and every request that ends 5xx keeps its trace (specs/08 section 9.4).</summary>
[TestFixture]
public sealed class TraceSamplingTests
{
    static SamplingParameters Root(ActivityTraceId id) =>
        new(default, id, "GET /v1/time", ActivityKind.Server);

    [Test]
    public void A_root_span_outside_the_share_is_recorded_but_not_sampled()
    {
        var id = ActivityTraceId.CreateRandom();
        Assert.That(new ErrorKeepingSampler(0).ShouldSample(Root(id)).Decision, Is.EqualTo(SamplingDecision.RecordOnly));
        Assert.That(new ErrorKeepingSampler(1).ShouldSample(Root(id)).Decision, Is.EqualTo(SamplingDecision.RecordAndSample));
    }

    [Test]
    public void About_the_configured_share_of_roots_is_sampled()
    {
        var sampler = new ErrorKeepingSampler(0.10);
        int sampled = 0;
        for (int i = 0; i < 20_000; i++)
            if (sampler.ShouldSample(Root(ActivityTraceId.CreateRandom())).Decision == SamplingDecision.RecordAndSample) sampled++;
        Assert.That(sampled / 20_000.0, Is.InRange(0.085, 0.115));
    }

    [Test]
    public void A_local_child_follows_its_parent_and_a_remote_parent_cannot_force_sampling()
    {
        var id = ActivityTraceId.CreateRandom();
        var sampler = new ErrorKeepingSampler(0);
        var recordedParent = new ActivityContext(id, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var droppedParent = new ActivityContext(id, ActivitySpanId.CreateRandom(), ActivityTraceFlags.None);
        var remoteSampled = new ActivityContext(id, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);
        Assert.That(sampler.ShouldSample(new SamplingParameters(recordedParent, id, "db", ActivityKind.Client)).Decision, Is.EqualTo(SamplingDecision.RecordAndSample));
        Assert.That(sampler.ShouldSample(new SamplingParameters(droppedParent, id, "db", ActivityKind.Client)).Decision, Is.EqualTo(SamplingDecision.Drop));
        Assert.That(sampler.ShouldSample(new SamplingParameters(remoteSampled, id, "GET", ActivityKind.Server)).Decision, Is.EqualTo(SamplingDecision.RecordOnly));
    }

    [Test]
    public async Task With_no_head_sampling_only_the_5xx_request_is_exported()
    {
        var exported = new List<Activity>();
        using var host = new PipelineHost(
            new Dictionary<string, string?> { ["HeroCat:Telemetry:TraceSamplePct"] = "0" },
            s => s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(exported)));
        using var client = host.Client();
        using (var ok = await client.SendAsync(PipelineHost.Request(HttpMethod.Get, "/v1/time"))) Assert.That((int)ok.StatusCode, Is.EqualTo(200));
        using (var fail = await client.SendAsync(PipelineHost.Request(HttpMethod.Get, "/v1/_test/throw"))) Assert.That((int)fail.StatusCode, Is.EqualTo(500));

        await UntilAsync(exported, 1);
        await Task.Delay(200); // the 200's span, had it been kept, would be in by now
        Assert.That(exported, Has.Count.EqualTo(1), string.Join(", ", exported.Select(a => a.DisplayName)));
        Assert.That(exported[0].GetTagItem("http.response.status_code"), Is.EqualTo(500));
    }

    /// <summary>The server span ends (and is exported) just after the response reaches the test client.</summary>
    static async Task UntilAsync(List<Activity> exported, int count)
    {
        for (int i = 0; i < 100 && exported.Count < count; i++) await Task.Delay(50);
    }

    [Test]
    public async Task With_full_head_sampling_every_request_is_exported()
    {
        var exported = new List<Activity>();
        using var host = new PipelineHost(
            new Dictionary<string, string?> { ["HeroCat:Telemetry:TraceSamplePct"] = "100" },
            s => s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(exported)));
        using var client = host.Client();
        for (int i = 0; i < 3; i++)
            using (var r = await client.SendAsync(PipelineHost.Request(HttpMethod.Get, "/v1/time"))) Assert.That((int)r.StatusCode, Is.EqualTo(200));
        await UntilAsync(exported, 3);
        Assert.That(exported, Has.Count.EqualTo(3));
    }
}
