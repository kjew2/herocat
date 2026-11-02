using System.Net;
using System.Text;
using System.Text.Json;
using HeroCat.Server.Infrastructure.Idempotency;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Idempotency;

/// <summary>Counts the stand-in handlers' writes and lets a test hold a handler mid-flight.</summary>
public sealed class IdempotencyProbe
{
    public long Writes;
    public TaskCompletionSource? Gate;
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class WriteBody
{
    public string? Value { get; set; }
}

/// <summary>Stand-in idempotent routes for the store's behaviour (not spec endpoints; the contract test skips <c>/_test/</c>).</summary>
[ApiController]
public sealed class IdempotencyRoutesController : ControllerBase
{
    readonly IdempotencyProbe _probe;
    public IdempotencyRoutesController(IdempotencyProbe probe) => _probe = probe;

    [HttpPost("v1/_test/idem/write"), Auth(AuthPolicy.Jwt), Idempotent]
    public IActionResult Write(WriteBody body)
    {
        long n = Interlocked.Increment(ref _probe.Writes);
        Response.Headers.ETag = $"\"w{n}\"";
        return StatusCode(StatusCodes.Status201Created, new { n, nonce = Guid.NewGuid(), echo = body.Value });
    }

    [HttpPost("v1/_test/idem/refuse"), Auth(AuthPolicy.Jwt), Idempotent]
    public object Refuse()
    {
        long n = Interlocked.Increment(ref _probe.Writes);
        throw new ApiException(402, "INSUFFICIENT_FUNDS", "Not enough diamonds.", new { need = 1000, have = n });
    }

    [HttpPost("v1/_test/idem/fail"), Auth(AuthPolicy.Jwt), Idempotent]
    public object Fail()
    {
        Interlocked.Increment(ref _probe.Writes);
        throw new InvalidOperationException("boom");
    }

    [HttpPost("v1/_test/idem/busy"), Auth(AuthPolicy.Jwt), Idempotent]
    public object Busy()
    {
        Interlocked.Increment(ref _probe.Writes);
        throw ApiException.Of(ErrorCodes.IDEMPOTENCY_IN_FLIGHT, retryAfterSec: 1);
    }

    [HttpPost("v1/_test/idem/unavailable"), Auth(AuthPolicy.Jwt), Idempotent]
    public IActionResult Unavailable()
    {
        Interlocked.Increment(ref _probe.Writes);
        return StatusCode(503);
    }

    [HttpPost("v1/_test/idem/slow"), Auth(AuthPolicy.Jwt), Idempotent]
    public async Task<object> Slow()
    {
        _probe.Entered.TrySetResult();
        if (_probe.Gate != null) await _probe.Gate.Task;
        return new { n = Interlocked.Increment(ref _probe.Writes) };
    }

    [HttpPost("v1/_test/idem/guest"), Auth(AuthPolicy.None), Idempotent(IdempotencyKeyKind.Device)]
    public object Guest() => new { n = Interlocked.Increment(ref _probe.Writes) };
}

/// <summary>The idempotency store in the request pipeline (specs/08 section 1.1, 00 section 8.13; issue #194).</summary>
[TestFixture]
public sealed class IdempotencyTests
{
    PipelineHost _host = null!;
    HttpClient _client = null!;

    [SetUp]
    public void Start()
    {
        _host = new PipelineHost();
        _client = _host.Client();
    }

    [TearDown]
    public void Stop()
    {
        _client.Dispose();
        _host.Dispose();
    }

    static HttpRequestMessage Post(string path, string? requestId, string body = "{\"value\":\"a\"}", Guid? player = null,
                                   string? deviceId = PipelineHost.DeviceId, string? token = null)
    {
        var r = PipelineHost.Request(HttpMethod.Post, path, player, deviceId: deviceId, token: token);
        r.Headers.Remove(ApiHeaders.RequestId);
        if (requestId != null) r.Headers.TryAddWithoutValidation(ApiHeaders.RequestId, requestId);
        r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return r;
    }

    Task<long> Writes() => Task.FromResult(Interlocked.Read(ref _host.Probe.Writes));

    static async Task<JsonElement> Body(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    [Test]
    public async Task A_mutating_route_requires_a_32_hex_request_id() =>
        await IdempotencyReplay.AssertRequestIdRequiredAsync(_client, id => Post("/v1/_test/idem/write", id));

    [Test]
    public async Task The_same_request_id_twice_replays_byte_identically_with_no_second_write()
    {
        byte[] body = await IdempotencyReplay.AssertReplayAsync(_client, id => Post("/v1/_test/idem/write", id), Writes);
        Assert.That(await Writes(), Is.EqualTo(1));
        Assert.That(Encoding.UTF8.GetString(body), Does.Contain("\"n\":1"));

        string id = Guid.NewGuid().ToString("N");
        using var a = await _client.SendAsync(Post("/v1/_test/idem/write", id));
        using var b = await _client.SendAsync(Post("/v1/_test/idem/write", id));
        Assert.That(b.StatusCode, Is.EqualTo(HttpStatusCode.Created), "the stored status");
        Assert.That(b.Headers.ETag?.Tag, Is.EqualTo("\"w2\"").And.EqualTo(a.Headers.ETag?.Tag), "ETag is replayed");
    }

    [Test]
    public async Task A_refusal_below_500_is_the_answer_and_replays()
    {
        byte[] body = await IdempotencyReplay.AssertReplayAsync(_client, id => Post("/v1/_test/idem/refuse", id), Writes);
        Assert.That(Encoding.UTF8.GetString(body), Does.Contain("INSUFFICIENT_FUNDS"));
        Assert.That(await Writes(), Is.EqualTo(1));
    }

    [Test]
    public async Task A_5xx_or_an_exception_is_not_stored_and_the_retry_runs_fresh()
    {
        foreach (string path in new[] { "/v1/_test/idem/fail", "/v1/_test/idem/unavailable", "/v1/_test/idem/busy" })
        {
            string id = Guid.NewGuid().ToString("N");
            long before = await Writes();
            using var a = await _client.SendAsync(Post(path, id));
            using var b = await _client.SendAsync(Post(path, id));
            Assert.That(b.Headers.Contains(IdempotencyMiddleware.ReplayHeader), Is.False, path);
            Assert.That(b.StatusCode, Is.EqualTo(a.StatusCode), path);
            Assert.That(await Writes(), Is.EqualTo(before + 2), $"{path}: the retry ran the handler again");
        }
        Assert.That(_host.Idempotency.Count, Is.Zero, "no row left behind");
    }

    [Test]
    public async Task A_concurrent_request_with_the_same_id_is_409_IDEMPOTENCY_IN_FLIGHT()
    {
        _host.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string id = Guid.NewGuid().ToString("N");
        var first = _client.SendAsync(Post("/v1/_test/idem/slow", id));
        await _host.Probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using (var second = await _client.SendAsync(Post("/v1/_test/idem/slow", id)))
        {
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            var b = await Body(second);
            Assert.That(b.GetProperty("code").GetString(), Is.EqualTo(ErrorCodes.IDEMPOTENCY_IN_FLIGHT));
            Assert.That(b.GetProperty("retryAfterSec").GetInt32(), Is.EqualTo(1));
            Assert.That(second.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(1)));
        }

        _host.Probe.Gate.SetResult();
        using var done = await first;
        Assert.That(done.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var replay = await _client.SendAsync(Post("/v1/_test/idem/slow", id));
        Assert.That(replay.Headers.GetValues(IdempotencyMiddleware.ReplayHeader).Single(), Is.EqualTo("true"));
        Assert.That(await Writes(), Is.EqualTo(1));
    }

    [Test]
    public async Task A_request_id_used_by_another_player_is_403_and_flags_DUPLICATE_REQUEST_ID()
    {
        string id = Guid.NewGuid().ToString("N");
        using var mine = await _client.SendAsync(Post("/v1/_test/idem/write", id));
        Assert.That(mine.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        var thief = Guid.NewGuid();
        using var theirs = await _client.SendAsync(Post("/v1/_test/idem/write", id, player: thief));
        Assert.That(theirs.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var b = await Body(theirs);
        Assert.That(b.GetProperty("code").GetString(), Is.EqualTo(ErrorCodes.FORBIDDEN));
        Assert.That(b.GetProperty("details").GetProperty("reason").GetString(), Is.EqualTo("DUPLICATE_REQUEST_ID"));
        Assert.That(_host.Flags.Raised, Has.Count.EqualTo(1));
        Assert.That(_host.Flags.Raised[0].PlayerId, Is.EqualTo(thief));
        Assert.That(_host.Flags.Raised[0].Code, Is.EqualTo("DUPLICATE_REQUEST_ID"));
        Assert.That(_host.Flags.Raised[0].Severity, Is.EqualTo("certain"));
        Assert.That(await Writes(), Is.EqualTo(1));
    }

    [Test]
    public async Task The_same_id_for_a_different_body_or_route_is_400_VALIDATION()
    {
        string id = Guid.NewGuid().ToString("N");
        using var a = await _client.SendAsync(Post("/v1/_test/idem/write", id, "{\"value\":\"a\"}"));
        foreach (var other in new[] { Post("/v1/_test/idem/write", id, "{\"value\":\"b\"}"), Post("/v1/_test/idem/refuse", id) })
        {
            using var r = await _client.SendAsync(other);
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            var b = await Body(r);
            Assert.That(b.GetProperty("code").GetString(), Is.EqualTo(ErrorCodes.VALIDATION));
            Assert.That(b.GetProperty("details").GetProperty("reason").GetString(), Is.EqualTo("REQUEST_ID_REUSED"));
        }
        Assert.That(await Writes(), Is.EqualTo(1));
    }

    [Test]
    public async Task After_IDEMPOTENCY_TTL_H_the_id_executes_fresh_and_the_purge_removes_the_row()
    {
        string id = Guid.NewGuid().ToString("N");
        using (await _client.SendAsync(Post("/v1/_test/idem/write", id))) { }
        _host.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        using (var stillReplayed = await _client.SendAsync(Post("/v1/_test/idem/write", id)))
            Assert.That(stillReplayed.Headers.Contains(IdempotencyMiddleware.ReplayHeader), "inside 24 h");

        _host.Clock.Advance(TimeSpan.FromSeconds(2));
        using (var fresh = await _client.SendAsync(Post("/v1/_test/idem/write", id)))
        {
            Assert.That(fresh.Headers.Contains(IdempotencyMiddleware.ReplayHeader), Is.False, "00 decision 28: executes fresh after 24 h");
            Assert.That(await fresh.Content.ReadAsStringAsync(), Does.Contain("\"n\":2"));
        }

        Assert.That(await _host.Idempotency.PurgeAsync(_host.Clock.GetUtcNow() - TimeSpan.FromHours(24), default), Is.Zero, "the fresh row is young");
        _host.Clock.Advance(TimeSpan.FromHours(25));
        Assert.That(await _host.Idempotency.PurgeAsync(_host.Clock.GetUtcNow() - TimeSpan.FromHours(24), default), Is.EqualTo(1));
        Assert.That(_host.Idempotency.Count, Is.Zero);
    }

    [Test]
    public async Task A_guest_route_is_keyed_by_the_device()
    {
        await IdempotencyReplay.AssertReplayAsync(_client, id => Post("/v1/_test/idem/guest", id, token: ""), Writes);
        using var noDevice = await _client.SendAsync(Post("/v1/_test/idem/guest", Guid.NewGuid().ToString("N"), token: "", deviceId: null));
        Assert.That(noDevice.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await Body(noDevice)).GetProperty("details").GetProperty("header").GetString(), Is.EqualTo(ApiHeaders.DeviceId));
    }

    [Test]
    public async Task Gate_refusals_are_not_stored()
    {
        string id = Guid.NewGuid().ToString("N");
        using var unauth = await _client.SendAsync(Post("/v1/_test/idem/write", id, token: "bad"));
        Assert.That(unauth.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var ok = await _client.SendAsync(Post("/v1/_test/idem/write", id));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(ok.Headers.Contains(IdempotencyMiddleware.ReplayHeader), Is.False);
    }

    [Test]
    public async Task The_worker_purge_deletes_rows_older_than_IDEMPOTENCY_TTL_H()
    {
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1_793_670_900));
        var store = new InMemoryIdempotencyStore(TimeSpan.FromHours(24));
        await store.BeginAsync(Guid.NewGuid(), Guid.NewGuid().ToString("N"), "post/x", "h", clock.GetUtcNow() - TimeSpan.FromHours(25), default);
        await store.BeginAsync(Guid.NewGuid(), Guid.NewGuid().ToString("N"), "post/x", "h", clock.GetUtcNow() - TimeSpan.FromHours(23), default);
        var options = Microsoft.Extensions.Options.Options.Create(new HeroCat.Server.Hosting.ServerOptions { Limits = { IdempotencyTtlHours = 24 } });
        var purge = new IdempotencyPurgeService(store, clock, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdempotencyPurgeService>.Instance);
        Assert.That(await purge.PurgeOnceAsync(default), Is.EqualTo(1));
        Assert.That(store.Count, Is.EqualTo(1));
        Assert.That(IdempotencyPurgeService.Interval, Is.EqualTo(TimeSpan.FromHours(1)), "hourly (specs/02 section 13.1)");
    }

    [Test]
    public void The_device_key_is_deterministic_case_insensitive_and_version_8()
    {
        var a = IdempotencyKeys.ForDevice("0F9E8D7C-0000-4000-8000-000000000001");
        var b = IdempotencyKeys.ForDevice("0f9e8d7c-0000-4000-8000-000000000001");
        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Is.Not.EqualTo(IdempotencyKeys.ForDevice("0f9e8d7c-0000-4000-8000-000000000002")));
        string s = a.ToString("D");
        Assert.That(s[14], Is.EqualTo('8'), "RFC 9562 version 8");
        Assert.That("89ab", Does.Contain(s[19].ToString()), "RFC 9562 variant");
    }
}
