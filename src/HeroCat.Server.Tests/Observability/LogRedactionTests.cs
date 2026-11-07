using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HeroCat.Server.Observability;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Observability;

/// <summary>
/// A stand-in handler that logs carelessly: the raw Authorization header, the request body, a receipt, a client IP and a
/// display name, by concatenation, as structured arguments and inside an exception. The structured log must still
/// carry none of them (specs/08 section 9.4).
/// </summary>
[ApiController]
public sealed class CarelessLoggingController : ControllerBase
{
    readonly ILogger<CarelessLoggingController> _log;

    public CarelessLoggingController(ILogger<CarelessLoggingController> log) => _log = log;

    [HttpPost("v1/_test/careless-log"), Auth(AuthPolicy.None), RateLimit(100)]
    public async Task<object> Log()
    {
        string body = await new StreamReader(Request.Body).ReadToEndAsync();
        string auth = Request.Headers.Authorization.ToString();
        var json = JsonDocument.Parse(body).RootElement;
        string receipt = json.GetProperty("receipt").GetString()!;
        string token = json.GetProperty("purchaseToken").GetString()!;
        string legacy = json.GetProperty("legacyReceipt").GetString()!;

        _log.LogInformation("validate called with " + auth + " body " + body);
        _log.LogInformation("headers {Authorization} receipt {Receipt} token {PurchaseToken} from {ClientIp}", auth, receipt, token, "203.0.113.77");
        _log.LogInformation("store answered {StoreResponse} for {Body}", "{\"purchaseToken\":\"" + token + "\",\"orderId\":\"GPA.1234\"}", body);
        _log.LogWarning("legacy receipt {Payload} via 203.0.113.77 and 2001:db8:abcd:12::1", legacy);
        _log.LogInformation("player {DisplayName} renamed", "Sir Whiskers");
        _log.LogError(new InvalidOperationException("bad receipt " + receipt + " / " + token + " auth=" + auth), "validation failed");
        return new { ok = true };
    }
}

[TestFixture]
public sealed class LogRedactionTests
{
    const string Jwt = "eyJhbGciOiJSUzI1NiIsImtpZCI6ImsxIn0.eyJzdWIiOiI2YjFmMmM0MC0wMDAwLTQwMDAtODAwMC0wMDAwMDAwMDAwMDEifQ.c2lnbmF0dXJlLWJ5dGVzLWhlcmUtc2lnbmF0dXJl";
    const string Jws = "eyJhbGciOiJFUzI1NiIsIng1YyI6WyJNSUlFTURDQ0E3YWdBd0lCQWdJUWZUbGZkMGZOdkZXdnpDMVlJQU5zWGpBS0JnZ3Foa2pPUFFRREF6QjFNVVF3UWdZRFZRUURERHRCY0hCc1pTQlhiM0pzWkhkcFpHVWdSR1YyWld4dmNHVnlJRkpsYkdGMGFXOXVjeUJEWlhKMGFXWnBZMkYwYVc5dUlFRjFkR2h2Y21sMGVURUxNQWtHQTFVRUN3d0NSell4RXpBUkJnTlZCQW9NQ2tGd2NHeGxJRWx1WXk0eEN6QUpCZ05WQkFZVEFsVlQiXX0.eyJ0cmFuc2FjdGlvbklkIjoiMjAwMDAwMDQ1NjE3ODkwMSIsImFwcEFjY291bnRUb2tlbiI6IjZiMWYyYzQwLTAwMDAtNDAwMC04MDAwLTAwMDAwMDAwMDAwMSJ9.MEUCIQDexample-signature_bytes";
    const string GoogleToken = "kgnbnhjfmmkkdhjlelgicmpb.AO-J1OzRWUq1FVrCZvJ2u0hq5TaiNRCmG_xvdXc5fiw0fQ9yL6KZaIx1Tsoz0aBwbPv3xjTe8-4Ul4GsFr1QZ4fnCWmUtdcvwU9QYHxZt3J3RIWvMsDUSXA";
    const string LegacyReceipt = "MIIT3QYJKoZIhvcNAQcCoIITzjCCE8oCAQExCzAJBgUrDgMCGgUAMIIDfgYJKoZIhvcNAQcBoIIDbwSCA2sxggNnMAoCAQgCAQEEAhYAMAoCARQCAQEEAgwAMAsCAQ4CAQEEAwIBejALAgEZAgEBBAMCAQMwDAIBCgIBAQQEFgI0KzAMAgEOAgEBBAQCAgCJMA0CAQ0CAQEEBQIDAdbg==";

    [Test]
    public async Task A_request_with_a_bearer_token_and_a_receipt_logs_neither()
    {
        var capture = new LogCapture();
        using var host = new PipelineHost(services: s => s.AddSingleton<ILogSink>(capture));
        using var client = host.Client();
        var req = PipelineHost.Request(HttpMethod.Post, "/v1/_test/careless-log", token: "");
        req.Content = new StringContent(JsonSerializer.Serialize(new { platform = "apple", receipt = Jws, purchaseToken = GoogleToken, legacyReceipt = LegacyReceipt }),
                                        Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt);
        req.Headers.Remove(ApiHeaders.RequestId);
        req.Headers.Add(ApiHeaders.RequestId, "0123456789abcdef0123456789abcdef");
        using var r = await client.SendAsync(req);
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK), await r.Content.ReadAsStringAsync());

        var lines = capture.Lines;
        Assert.That(lines.Count(l => l.Contains("CarelessLoggingController")), Is.EqualTo(6), "every careless line was written");
        string all = capture.All;
        foreach (var (what, secret) in new[]
                 {
                     ("bearer token", Jwt), ("bearer token's signature", "c2lnbmF0dXJlLWJ5dGVzLWhlcmUtc2lnbmF0dXJl"), ("Apple JWS", Jws),
                     ("JWS payload", "eyJ0cmFuc2FjdGlvbklkIjoiMjAwMDAwMDQ1NjE3ODkwMSIs"), ("Google purchase token", "AO-J1OzRWUq1FVrCZvJ2u0hq5TaiNRCmG"),
                     ("legacy receipt", "MIIT3QYJKoZIhvcNAQcCoIITzjCCE8oCAQExCzAJBgUrDgMCGgUA"), ("display name", "Sir Whiskers"),
                     ("full IPv4", "203.0.113.77"), ("full IPv6", "2001:db8:abcd:12::1"),
                 })
            Assert.That(all, Does.Not.Contain(secret), what + " reached the log");
        Assert.That(all, Does.Contain("203.0.113.0/24"), "IPs are kept to their /24");
        Assert.That(all, Does.Contain("2001:db8:abcd::/48"), "IPv6 is kept to its /48");
        Assert.That(all, Does.Contain(LogRedactor.Redacted));

        var access = capture.Json.Single(j => j.GetProperty("category").GetString() == typeof(RequestTelemetryMiddleware).FullName);
        Assert.That(access.GetProperty("route").GetString(), Is.EqualTo("post/_test/careless-log"));
        Assert.That(access.GetProperty("status").GetInt32(), Is.EqualTo(200));
        Assert.That(access.GetProperty("requestId").GetString(), Is.EqualTo("0123456789abcdef0123456789abcdef"));
        Assert.That(access.TryGetProperty("latencyMs", out _));
        foreach (var careless in capture.Json.Where(j => j.GetProperty("category").GetString()!.Contains("CarelessLogging")))
            Assert.That(careless.GetProperty("requestId").GetString(), Is.EqualTo("0123456789abcdef0123456789abcdef"), "handler lines carry the request scope");
    }

    [Test]
    public async Task An_authenticated_request_logs_the_hashed_player_device_route_status_and_code()
    {
        var capture = new LogCapture();
        using var host = new PipelineHost(services: s => s.AddSingleton<ILogSink>(capture));
        using var client = host.Client();
        using (var r = await client.SendAsync(PipelineHost.Request(HttpMethod.Get, "/v1/save/meta")))
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK), await r.Content.ReadAsStringAsync());
        using (var r = await client.SendAsync(PipelineHost.Request(HttpMethod.Get, "/v1/_test/api-error")))
            Assert.That((int)r.StatusCode, Is.EqualTo(402), await r.Content.ReadAsStringAsync());

        var access = capture.Json.Where(j => j.GetProperty("category").GetString() == typeof(RequestTelemetryMiddleware).FullName).ToList();
        Assert.That(access, Has.Count.EqualTo(2));
        var ok = access[0];
        Assert.That(ok.GetProperty("route").GetString(), Is.EqualTo("get/save/meta"));
        Assert.That(ok.GetProperty("playerIdHash").GetString(), Is.EqualTo(LogFields.PlayerIdHash(PipelineHost.Player)));
        Assert.That(ok.GetProperty("deviceId").GetString(), Is.EqualTo(PipelineHost.DeviceId));
        Assert.That(ok.GetProperty("level").GetString(), Is.EqualTo("info"));
        Assert.That(ok.TryGetProperty("code", out _), Is.False);
        Assert.That(DateTimeOffset.TryParse(ok.GetProperty("ts").GetString(), out _));
        Assert.That(access[1].GetProperty("code").GetString(), Is.EqualTo("INSUFFICIENT_FUNDS"));
        Assert.That(access[1].GetProperty("status").GetInt32(), Is.EqualTo(402));
        Assert.That(capture.All, Does.Not.Contain(PipelineHost.Player.ToString("D")), "the player id itself is never logged");
    }

    [Test]
    public async Task An_unmatched_path_is_labelled_unmatched_so_scans_cannot_grow_the_label_set()
    {
        var capture = new LogCapture();
        using var host = new PipelineHost(services: s => s.AddSingleton<ILogSink>(capture));
        using var client = host.Client();
        using var r = await client.GetAsync("/wp-admin/" + Guid.NewGuid());
        await r.Content.ReadAsStringAsync(); // the access line is written before the body completes
        var access = capture.Json.Single(j => j.GetProperty("category").GetString() == typeof(RequestTelemetryMiddleware).FullName);
        Assert.That(access.GetProperty("route").GetString(), Is.EqualTo(LogFields.Unmatched));
    }

    [TestCase("Authorization")]
    [TestCase("refresh_token")]
    [TestCase("AccessToken")]
    [TestCase("receipt")]
    [TestCase("signedPayload")]
    [TestCase("purchaseToken")]
    [TestCase("deviceKeyHmac")]
    [TestCase("HmacKey")]
    [TestCase("DataKey")]
    [TestCase("DeviceKey")]
    [TestCase("displayName")]
    [TestCase("X-Api-Key")]
    public void Secret_names_are_redacted_whatever_the_value(string name) =>
        Assert.That(LogRedactor.Value(name, "anything at all"), Is.EqualTo(LogRedactor.Redacted));

    [TestCase("RequestId")]
    [TestCase("Rows")]
    [TestCase("Route")]
    [TestCase("ownership")]
    [TestCase("ConfigVersion")]
    public void Ordinary_names_keep_their_value(string name) =>
        Assert.That(LogRedactor.Value(name, "kept"), Is.EqualTo("kept"));

    [TestCase("ClientIp", "198.51.100.23", "198.51.100.0/24")]
    [TestCase("ip", "198.51.100.23, 10.0.0.9", "198.51.100.0/24, 10.0.0.0/24")]
    [TestCase("RemoteIpAddress", "2001:db8:1234:5678::42", "2001:db8:1234::/48")]
    [TestCase("X-Forwarded-For", "::ffff:192.0.2.200", "192.0.2.0/24")]
    [TestCase("peer_ip", "not an ip", LogRedactor.Redacted)]
    public void Ip_names_are_cut_to_their_network(string name, string value, string expected) =>
        Assert.That(LogRedactor.Value(name, value), Is.EqualTo(expected));

    [Test]
    public void An_IPAddress_value_is_cut_whatever_its_name() =>
        Assert.That(LogRedactor.Value("Peer", IPAddress.Parse("192.0.2.200")), Is.EqualTo("192.0.2.0/24"));

    [TestCase("content 2026.11.03.1 is active")]
    [TestCase("sha256 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 mismatch")]
    [TestCase("player 6b1f2c40-0000-4000-8000-000000000001")]
    [TestCase("at HeroCat.Server.Features.Summon.SummonService.PullAsync(SummonPullRequest request)")]
    [TestCase("loading /app/db/migrations/0004_analytics_events_partitions.sql")]
    [TestCase("at 12:34:56 the job ran 3 passes")]
    [TestCase("request 0123456789abcdef0123456789abcdef answered 409")]
    public void Ordinary_text_passes_unchanged(string text) =>
        Assert.That(LogRedactor.Scrub(text), Is.EqualTo(text));

    [TestCase("Authorization: Bearer abc.def.ghi", "Authorization: [redacted]")]
    [TestCase("refresh?token=s3cr3t&x=1", "refresh?token=[redacted]&x=1")]
    [TestCase("{\"receipt\":\"x y\",\"n\":1}", "{\"receipt\":\"[redacted]\",\"n\":1}")]
    [TestCase("key -----BEGIN PRIVATE KEY-----\nMIIabc\n-----END PRIVATE KEY----- done", "key [redacted] done")]
    [TestCase("peer 10.1.2.3 and 300.1.2.3", "peer 10.1.2.0/24 and 300.1.2.3")]
    public void Secrets_in_free_text_are_scrubbed(string text, string expected) =>
        Assert.That(LogRedactor.Scrub(text), Is.EqualTo(expected));

    [Test]
    public void Scrubbing_is_idempotent()
    {
        string once = LogRedactor.Scrub("peer 10.1.2.3 token=" + GoogleToken + " " + Jwt);
        Assert.That(LogRedactor.Scrub(once), Is.EqualTo(once));
    }

    [Test]
    public void The_message_is_rendered_from_redacted_values_not_the_callers_formatted_string()
    {
        var capture = new LogCapture();
        using var provider = new StructuredLoggerProvider(capture);
        provider.CreateLogger("t").LogInformation("refresh with {RefreshToken} for {Rows} rows", "rt-plain-secret", 3);
        var line = JsonDocument.Parse(capture.Lines.Single()).RootElement;
        Assert.That(line.GetProperty("msg").GetString(), Is.EqualTo("refresh with [redacted] for 3 rows"));
        Assert.That(line.GetProperty("props").GetProperty("refreshToken").GetString(), Is.EqualTo(LogRedactor.Redacted));
        Assert.That(line.GetProperty("props").GetProperty("rows").GetInt32(), Is.EqualTo(3));
        Assert.That(capture.All, Does.Not.Contain("rt-plain-secret"));
    }

    [Test]
    public void Request_fields_given_as_arguments_are_top_level()
    {
        var capture = new LogCapture();
        using var provider = new StructuredLoggerProvider(capture);
        provider.CreateLogger("t").LogWarning("{Route} failed with {Code}", "put/save", "SAVE_CONFLICT");
        var line = JsonDocument.Parse(capture.Lines.Single()).RootElement;
        Assert.That(line.GetProperty("route").GetString(), Is.EqualTo("put/save"));
        Assert.That(line.GetProperty("code").GetString(), Is.EqualTo("SAVE_CONFLICT"));
        Assert.That(line.GetProperty("level").GetString(), Is.EqualTo("warn"));
        Assert.That(line.TryGetProperty("props", out _), Is.False);
    }
}
