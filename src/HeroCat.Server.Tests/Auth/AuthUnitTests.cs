using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeroCat.Server.Features.Auth;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Secrets;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Auth;

/// <summary>The Auth service's pieces that need no database (specs/08 section 2.1, 9.3; issue #195).</summary>
[TestFixture]
public sealed class AuthUnitTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_793_670_900);

    static AccessTokenService Tokens(ServerKeyRing keys, TimeProvider clock) =>
        new(new Lazy<ServerKeyRing>(keys), clock, Options.Create(new ServerOptions { Limits = { AccessTokenTtlSeconds = 3600 } }));

    static JsonElement Part(string token, int i) => JsonDocument.Parse(WebEncoders.Base64UrlDecode(token.Split('.')[i])).RootElement.Clone();

    [Test]
    public void An_access_token_is_RS256_with_the_spec_claims_and_a_one_hour_expiry()
    {
        var clock = new FakeClock(T0);
        var keys = ServerKeyRing.Ephemeral();
        var svc = Tokens(keys, clock);
        var player = Guid.NewGuid();
        var issued = svc.Issue(player, "0f9e8d7c-0000-4000-8000-000000000001", 2);

        var header = Part(issued.Token, 0);
        Assert.That(header.GetProperty("alg").GetString(), Is.EqualTo("RS256"));
        Assert.That(header.GetProperty("kid").GetString(), Is.EqualTo(keys.Signer.Kid));
        var claims = Part(issued.Token, 1);
        Assert.That(claims.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "sub", "dev", "age", "iat", "exp", "jti" }));
        Assert.That(claims.GetProperty("sub").GetString(), Is.EqualTo(player.ToString("D")));
        Assert.That(claims.GetProperty("iat").GetInt64(), Is.EqualTo(T0.ToUnixTimeSeconds()));
        Assert.That(claims.GetProperty("exp").GetInt64(), Is.EqualTo(T0.ToUnixTimeSeconds() + 3600).And.EqualTo(issued.ExpiresAt));

        var ok = svc.Validate(issued.Token);
        Assert.That(ok.Status, Is.EqualTo(AccessTokenStatus.Valid));
        Assert.That(ok.Claims, Is.EqualTo(new AccessTokenClaims(player, "0f9e8d7c-0000-4000-8000-000000000001", 2)));
        Assert.That(svc.Issue(player, "d", 0).Token, Is.Not.EqualTo(svc.Issue(player, "d", 0).Token), "jti differs");

        clock.Advance(TimeSpan.FromSeconds(3599));
        Assert.That(svc.Validate(issued.Token).Status, Is.EqualTo(AccessTokenStatus.Valid));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(svc.Validate(issued.Token).Status, Is.EqualTo(AccessTokenStatus.Expired));
    }

    [Test]
    public void Tampered_unsigned_or_foreign_tokens_are_invalid()
    {
        var clock = new FakeClock(T0);
        var svc = Tokens(ServerKeyRing.Ephemeral(), clock);
        string token = svc.Issue(Guid.NewGuid(), "dev", 0).Token;
        var parts = token.Split('.');

        string forged = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(parts[1])).Replace("\"age\":0", "\"age\":2")));
        string none = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"kid\":\"x\"}"));
        foreach (string bad in new[] { parts[0] + "." + forged + "." + parts[2], none + "." + parts[1] + ".", "a.b", "", "x.y.z",
                                       Tokens(ServerKeyRing.Ephemeral(), clock).Issue(Guid.NewGuid(), "dev", 0).Token })
            Assert.That(svc.Validate(bad).Status, Is.EqualTo(AccessTokenStatus.Invalid), bad);
    }

    [Test]
    public void Two_active_keys_let_tokens_of_the_outgoing_key_validate_through_a_rotation()
    {
        var clock = new FakeClock(T0);
        var old = ServerKeyRing.Ephemeral();
        var next = ServerKeyRing.Ephemeral();
        string issuedByOld = Tokens(old, clock).Issue(Guid.NewGuid(), "dev", 0).Token;

        var rotating = new ServerKeyRing(new[] { next.Signer, old.Signer }, old.RecoveryKey, old.DataKey);
        var svc = Tokens(rotating, clock);
        Assert.That(svc.Validate(issuedByOld).Status, Is.EqualTo(AccessTokenStatus.Valid), "the outgoing key still verifies");
        Assert.That(Part(svc.Issue(Guid.NewGuid(), "dev", 0).Token, 0).GetProperty("kid").GetString(), Is.EqualTo(next.Signer.Kid), "the first key signs");
        Assert.That(Tokens(next, clock).Validate(issuedByOld).Status, Is.EqualTo(AccessTokenStatus.Invalid), "a retired key no longer verifies");
    }

    [Test]
    public void Refresh_tokens_are_256_random_bits_stored_as_sha256()
    {
        string a = RefreshTokens.New(), b = RefreshTokens.New();
        Assert.That(a, Does.StartWith("r1.").And.Not.EqualTo(b));
        Assert.That(WebEncoders.Base64UrlDecode(a[3..]), Has.Length.EqualTo(32));
        Assert.That(RefreshTokens.LooksValid(a), Is.True);
        Assert.That(RefreshTokens.LooksValid("r1.short"), Is.False);
        Assert.That(RefreshTokens.Hash(a), Is.EqualTo(SHA256.HashData(Encoding.UTF8.GetBytes(a))));
    }

    [Test]
    public void The_support_code_is_12_base32_chars_of_the_hmac()
    {
        Assert.That(SupportCodes.Base32(Encoding.ASCII.GetBytes("foobar")), Is.EqualTo("MZXW6YTBOI"), "RFC 4648 vector, no padding");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        var player = Guid.NewGuid();
        string code = SupportCodes.For(player, key);
        Assert.That(code, Does.Match("^[A-Z2-7]{12}$"));
        Assert.That(SupportCodes.For(player, key), Is.EqualTo(code));
        Assert.That(SupportCodes.For(Guid.NewGuid(), key), Is.Not.EqualTo(code));
        Assert.That(code, Is.EqualTo(SupportCodes.Base32(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(player.ToString("D"))))[..12]));
    }

    [Test]
    public void The_secret_protector_round_trips_and_binds_its_associated_data()
    {
        var p = new SecretProtector(ServerKeyRing.Ephemeral());
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        byte[] aad = Encoding.UTF8.GetBytes("device/a");
        byte[] enc = p.Protect(secret, aad);
        Assert.That(enc, Has.Length.EqualTo(1 + 12 + 16 + 32));
        Assert.That(p.Unprotect(enc, aad), Is.EqualTo(secret));
        Assert.That(p.Protect(secret, aad), Is.Not.EqualTo(enc), "fresh nonce");
        Assert.Throws(Is.InstanceOf<CryptographicException>(), () => p.Unprotect(enc, Encoding.UTF8.GetBytes("device/b")));
        enc[^1] ^= 1;
        Assert.Throws(Is.InstanceOf<CryptographicException>(), () => p.Unprotect(enc, aad));
        Assert.Throws(Is.InstanceOf<CryptographicException>(), () => new SecretProtector(ServerKeyRing.Ephemeral()).Unprotect(p.Protect(secret, aad), aad));
    }

    [Test]
    public void Keys_load_from_configuration_and_are_ephemeral_only_when_allowed()
    {
        using var rsa = RSA.Create(2048);
        var o = new KeysOptions
        {
            SigningKeys = { new SigningKeyOptions { Kid = "k2", Pem = rsa.ExportPkcs8PrivateKeyPem() } },
            RecoveryKey = Convert.ToBase64String(new byte[32]),
            DataKey = Convert.ToBase64String(new byte[32]),
        };
        var ring = ServerKeyRing.FromOptions(o, NullLogger.Instance);
        Assert.That(ring.Signer.Kid, Is.EqualTo("k2"));
        Assert.That(ring.Signer.Rsa.ExportSubjectPublicKeyInfo(), Is.EqualTo(rsa.ExportSubjectPublicKeyInfo()));

        Assert.Throws<InvalidOperationException>(() => ServerKeyRing.FromOptions(new KeysOptions(), NullLogger.Instance), "no keys, no ephemeral");
        Assert.Throws<InvalidOperationException>(() => ServerKeyRing.FromOptions(new KeysOptions { SigningKeys = o.SigningKeys, RecoveryKey = o.RecoveryKey }, NullLogger.Instance));
        var eph = ServerKeyRing.FromOptions(new KeysOptions { AllowEphemeral = true }, NullLogger.Instance);
        Assert.That(eph.DataKey, Has.Length.EqualTo(32));
    }

    [Test]
    public void The_default_display_name_is_Cat_hash_and_the_last_4_of_the_player_id() =>
        Assert.That(AuthService.DefaultDisplayName(Guid.Parse("6b1f2c40-0000-4000-8000-00000000ab12")), Is.EqualTo("Cat#AB12"));

    [Test]
    public async Task GET_time_returns_serverTime_without_auth()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var r = await client.GetAsync("/v1/time");
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        long t = body.GetProperty("serverTime").GetInt64();
        Assert.That(body.EnumerateObject().Count(), Is.EqualTo(1));
        Assert.That(t, Is.EqualTo(long.Parse(r.Headers.GetValues(ApiHeaders.ServerTime).Single())).Within(2));
    }

    [Test]
    public async Task A_jwt_device_route_rejects_a_real_token_whose_dev_differs_from_X_Device_Id()
    {
        using var host = new PipelineHost();
        var svc = Tokens(ServerKeyRing.Ephemeral(), host.Clock);
        host.TokenValidatorOverride = svc;
        using var client = host.Client();
        string token = svc.Issue(PipelineHost.Player, PipelineHost.DeviceId, 2).Token;

        using var ok = await client.SendAsync(PipelineHost.Request(HttpMethod.Post, "/v1/summon/pull", token: token));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var other = await client.SendAsync(PipelineHost.Request(HttpMethod.Post, "/v1/summon/pull", token: token, deviceId: "0f9e8d7c-0000-4000-8000-000000000002"));
        Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var b = JsonDocument.Parse(await other.Content.ReadAsStringAsync()).RootElement;
        Assert.That(b.GetProperty("details").GetProperty("reason").GetString(), Is.EqualTo("DEVICE_MISMATCH"));

        host.Clock.Advance(TimeSpan.FromHours(1));
        using var expired = await client.SendAsync(PipelineHost.Request(HttpMethod.Post, "/v1/summon/pull", token: token));
        Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await expired.Content.ReadAsStringAsync(), Does.Contain("TOKEN_EXPIRED"));
    }
}
