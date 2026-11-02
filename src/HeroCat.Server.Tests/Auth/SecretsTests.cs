using System.Security.Cryptography;
using HeroCat.Server.Features.Auth;
using HeroCat.Server.Hosting;
using HeroCat.Server.Infrastructure.Secrets;
using HeroCat.Server.Pipeline;
using HeroCat.Server.Tests.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace HeroCat.Server.Tests.Auth;

/// <summary>
/// Keys from Secrets Manager and the KMS data-key envelope (specs/08 section 9.3, issue #272), against in-memory fakes of
/// both AWS services: the configuration provider, the signing-key rotation without restart, and no secret in errors or logs.
/// </summary>
[TestFixture]
public sealed class SecretsTests
{
    const string SigningId = "herocat/test/jwt-signing-keys";
    const string RecoveryId = "herocat/test/support-code-key";
    const string DataId = "herocat/test/data-key-wrapped";

    sealed class FakeSecrets : ISecretSource
    {
        public readonly Dictionary<string, string> Values = new();
        public Exception? Fail;
        public int Reads;

        public Task<string> GetSecretAsync(string secretId, CancellationToken ct)
        {
            Reads++;
            if (Fail != null) throw Fail;
            return Values.TryGetValue(secretId, out var v) ? Task.FromResult(v) : throw new KeyNotFoundException("ResourceNotFoundException " + secretId);
        }
    }

    /// <summary>"Wraps" by XOR with a fixed pad and checks the encryption context, as KMS would refuse a wrong one.</summary>
    sealed class FakeKms : IDataKeyUnwrapper
    {
        public static readonly byte[] Pad = Enumerable.Range(0, 32).Select(i => (byte)(0xA5 ^ i)).ToArray();
        public int Calls;
        public string? KeyId;
        public IReadOnlyDictionary<string, string>? Context;
        public string ExpectEnv = "test";

        public static byte[] Wrap(byte[] key) => key.Select((b, i) => (byte)(b ^ Pad[i % Pad.Length])).ToArray();

        public Task<byte[]> UnwrapAsync(byte[] wrapped, string keyId, IReadOnlyDictionary<string, string> encryptionContext, CancellationToken ct)
        {
            Calls++;
            KeyId = keyId;
            Context = encryptionContext;
            if (encryptionContext[DataKeyEnvelope.EnvContextKey] != ExpectEnv) throw new CryptographicException("InvalidCiphertextException");
            return Task.FromResult(Wrap(wrapped));
        }
    }

    sealed class ListLogger : ILogger, ILoggerProvider
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception == null ? "" : " " + exception));
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
    }

    static string Pem(RSA rsa) => rsa.ExportPkcs8PrivateKeyPem();

    static string SigningJson(params (string Kid, RSA Rsa)[] keys) =>
        System.Text.Json.JsonSerializer.Serialize(keys.Select(k => new { kid = k.Kid, pem = Pem(k.Rsa) }));

    readonly byte[] _recovery = RandomNumberGenerator.GetBytes(32);
    readonly byte[] _data = RandomNumberGenerator.GetBytes(32);
    RSA _k1 = null!, _k2 = null!;
    FakeSecrets _secrets = null!;
    FakeKms _kms = null!;

    [SetUp]
    public void SetUp()
    {
        _k1 = RSA.Create(2048);
        _k2 = RSA.Create(2048);
        _secrets = new FakeSecrets();
        _secrets.Values[SigningId] = SigningJson(("k1", _k1));
        _secrets.Values[RecoveryId] = Convert.ToBase64String(_recovery);
        _secrets.Values[DataId] = Convert.ToBase64String(FakeKms.Wrap(_data)) + "\n";
        _kms = new FakeKms();
    }

    [TearDown]
    public void TearDown()
    {
        _k1.Dispose();
        _k2.Dispose();
    }

    static Dictionary<string, string?> AwsSettings(int refreshSeconds = 300) => new()
    {
        ["HeroCat:Env"] = "test",
        ["HeroCat:Secrets:Provider"] = "aws",
        ["HeroCat:Secrets:SigningKeysSecretId"] = SigningId,
        ["HeroCat:Secrets:RecoveryKeySecretId"] = RecoveryId,
        ["HeroCat:Secrets:DataKeySecretId"] = DataId,
        ["HeroCat:Secrets:RefreshSeconds"] = refreshSeconds.ToString(),
        ["HeroCat:Keys:DataKeyKmsKeyId"] = "alias/herocat-test-data",
    };

    ConfigurationManager Configuration(Dictionary<string, string?>? settings = null)
    {
        var cm = new ConfigurationManager();
        cm.AddInMemoryCollection(settings ?? AwsSettings());
        cm.AddHeroCatSecrets(_secrets);
        return cm;
    }

    ServiceProvider Services(ConfigurationManager cm, ILoggerProvider? logs = null)
    {
        var s = new ServiceCollection();
        s.AddLogging(b => { if (logs != null) b.AddProvider(logs); });
        s.AddSingleton<IConfiguration>(cm);
        s.AddOptions<ServerOptions>().Bind(cm.GetSection(ServerOptions.Section));
        s.AddSingleton<IDataKeyUnwrapper>(_kms);
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton(sp => new Lazy<ServerKeyRing>(() => ServerKeyRing.Create(sp)));
        s.AddSingleton<SigningKeyRotationService>();
        return s.BuildServiceProvider();
    }

    [Test]
    public void The_provider_reads_the_named_secrets_into_HeroCat_Keys_and_KMS_unwraps_the_data_key_once()
    {
        var cm = Configuration();
        Assert.That(cm["HeroCat:Keys:SigningKeys:0:Kid"], Is.EqualTo("k1"));
        Assert.That(cm["HeroCat:Keys:RecoveryKey"], Is.EqualTo(Convert.ToBase64String(_recovery)));

        using var sp = Services(cm);
        var lazy = sp.GetRequiredService<Lazy<ServerKeyRing>>();
        var ring = lazy.Value;
        _ = lazy.Value;
        Assert.That(ring.Signer.Kid, Is.EqualTo("k1"));
        Assert.That(ring.Signer.Rsa.ExportSubjectPublicKeyInfo(), Is.EqualTo(_k1.ExportSubjectPublicKeyInfo()));
        Assert.That(ring.RecoveryKey, Is.EqualTo(_recovery));
        Assert.That(ring.DataKey, Is.EqualTo(_data));
        Assert.That(_kms.Calls, Is.EqualTo(1), "Decrypt once per process");
        Assert.That(_kms.KeyId, Is.EqualTo("alias/herocat-test-data"));
        Assert.That(_kms.Context, Is.EquivalentTo(new Dictionary<string, string> { ["herocat:purpose"] = "data-key", ["herocat:env"] = "test" }));
        Assert.That(_secrets.Reads, Is.EqualTo(3));
    }

    [Test]
    public void Without_the_aws_provider_nothing_is_added_and_the_ephemeral_path_is_unchanged()
    {
        var cm = new ConfigurationManager();
        cm.AddInMemoryCollection(new Dictionary<string, string?> { ["HeroCat:Keys:AllowEphemeral"] = "true" });
        cm.AddHeroCatSecrets(_secrets);
        Assert.That(cm.SecretsProvider(), Is.Null);
        Assert.That(_secrets.Reads, Is.Zero);
        using var sp = Services(cm);
        var ring = sp.GetRequiredService<Lazy<ServerKeyRing>>().Value;
        Assert.That(ring.Signer.Kid, Does.StartWith("ephemeral-"));
        Assert.That(_kms.Calls, Is.Zero);

        var bad = new ConfigurationManager();
        bad.AddInMemoryCollection(new Dictionary<string, string?> { ["HeroCat:Secrets:Provider"] = "vault" });
        Assert.Throws<InvalidOperationException>(() => bad.AddHeroCatSecrets(_secrets));
    }

    [Test]
    public void A_failed_or_malformed_secret_fails_the_start_naming_the_secret_but_never_its_value()
    {
        _secrets.Fail = new Exception("AccessDenied SECRET-VALUE-XYZ");
        var e = Assert.Throws<InvalidOperationException>(() => Configuration())!;
        Assert.That(e.Message, Does.Contain(SigningId).And.Not.Contain("SECRET-VALUE-XYZ"));
        Assert.That(e.InnerException, Is.Null);

        _secrets.Fail = null;
        _secrets.Values[SigningId] = "{\"kid\":\"k1\",\"pem\":\"SECRET-VALUE-XYZ\"";
        e = Assert.Throws<InvalidOperationException>(() => Configuration())!;
        Assert.That(e.Message, Does.Contain(SigningId).And.Not.Contain("SECRET-VALUE-XYZ"));

        _secrets.Values[SigningId] = "[{\"kid\":\"k1\"}]";
        Assert.Throws<InvalidOperationException>(() => Configuration());
    }

    [Test]
    public void The_data_key_envelope_is_refused_when_ambiguous_unwrappable_or_the_wrong_size()
    {
        var log = NullLogger.Instance;
        string wrapped = Convert.ToBase64String(FakeKms.Wrap(_data));
        var signing = new List<SigningKeyOptions> { new() { Kid = "k1", Pem = Pem(_k1) } };
        string recovery = Convert.ToBase64String(_recovery);

        Assert.Throws<InvalidOperationException>(() => ServerKeyRing.FromOptions(
            new KeysOptions { SigningKeys = signing, RecoveryKey = recovery, DataKeyWrapped = wrapped, DataKey = recovery }, log, _kms, "test"), "both");
        Assert.Throws<InvalidOperationException>(() => ServerKeyRing.FromOptions(
            new KeysOptions { SigningKeys = signing, RecoveryKey = recovery, DataKeyWrapped = wrapped }, log, null, "test"), "no KMS configured");
        Assert.Throws<InvalidOperationException>(() => ServerKeyRing.FromOptions(
            new KeysOptions { SigningKeys = signing, RecoveryKey = recovery, DataKeyWrapped = Convert.ToBase64String(new byte[16]) }, log, _kms, "test"), "16 bytes");
        var wrongEnv = Assert.Throws<InvalidOperationException>(() => ServerKeyRing.FromOptions(
            new KeysOptions { SigningKeys = signing, RecoveryKey = recovery, DataKeyWrapped = wrapped }, log, _kms, "prod"), "another env's envelope")!;
        Assert.That(wrongEnv.Message, Does.Contain("CryptographicException"));
        Assert.That(ServerKeyRing.FromOptions(new KeysOptions { SigningKeys = signing, RecoveryKey = recovery, DataKeyWrapped = wrapped }, log, _kms, "test").DataKey,
                    Is.EqualTo(_data));
    }

    [Test]
    public async Task A_rotated_signing_secret_reaches_the_running_key_ring_without_a_restart()
    {
        var cm = Configuration();
        using var sp = Services(cm);
        var ring = sp.GetRequiredService<Lazy<ServerKeyRing>>().Value;
        var svc = sp.GetRequiredService<SigningKeyRotationService>();
        await svc.StartAsync(CancellationToken.None);
        try
        {
            var clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1_793_670_900));
            var tokens = new AccessTokenService(sp.GetRequiredService<Lazy<ServerKeyRing>>(), clock,
                                                Options.Create(new ServerOptions { Limits = { AccessTokenTtlSeconds = 3600 } }));
            string byK1 = tokens.Issue(Guid.NewGuid(), "dev", 0).Token;
            var provider = cm.SecretsProvider()!;

            Assert.That(await provider.RefreshSigningKeysAsync(CancellationToken.None), Is.False, "unchanged secret, no reload");

            // Phase 1 of a rotation: the new key verifies everywhere before it signs anywhere.
            _secrets.Values[SigningId] = SigningJson(("k1", _k1), ("k2", _k2));
            await svc.RefreshOnceAsync(provider, CancellationToken.None);
            Assert.That(ring.Signing.Select(k => k.Kid), Is.EqualTo(new[] { "k1", "k2" }));

            // Phase 2: the new key signs; the outgoing one still verifies.
            _secrets.Values[SigningId] = SigningJson(("k2", _k2), ("k1", _k1));
            await svc.RefreshOnceAsync(provider, CancellationToken.None);
            Assert.That(ring.Signer.Kid, Is.EqualTo("k2"));
            Assert.That(tokens.Validate(byK1).Status, Is.EqualTo(AccessTokenStatus.Valid));
            Assert.That(tokens.Issue(Guid.NewGuid(), "dev", 0).Token, Does.Not.Contain(byK1.Split('.')[0]), "a new kid in the header");

            // Phase 3: the outgoing key retires.
            _secrets.Values[SigningId] = SigningJson(("k2", _k2));
            await svc.RefreshOnceAsync(provider, CancellationToken.None);
            Assert.That(tokens.Validate(byK1).Status, Is.EqualTo(AccessTokenStatus.Invalid));
            Assert.That(ring.DataKey, Is.EqualTo(_data), "the data key never changes in a running process");
            Assert.That(_kms.Calls, Is.EqualTo(1));
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task A_failed_or_bad_refresh_keeps_the_current_keys_and_logs_no_secret()
    {
        var logs = new ListLogger();
        var cm = Configuration();
        using var sp = Services(cm, logs);
        var ring = sp.GetRequiredService<Lazy<ServerKeyRing>>().Value;
        var svc = sp.GetRequiredService<SigningKeyRotationService>();
        await svc.StartAsync(CancellationToken.None);
        try
        {
            var provider = cm.SecretsProvider()!;
            _secrets.Fail = new Exception("Throttled SECRET-VALUE-XYZ");
            await svc.RefreshOnceAsync(provider, CancellationToken.None);
            Assert.That(ring.Signer.Kid, Is.EqualTo("k1"));
            Assert.That(cm["HeroCat:Keys:SigningKeys:0:Kid"], Is.EqualTo("k1"));

            _secrets.Fail = null;
            _secrets.Values[SigningId] = "[{\"kid\":\"k9\",\"pem\":\"-----BEGIN PRIVATE KEY-----SECRET-VALUE-XYZ\"}]";
            await svc.RefreshOnceAsync(provider, CancellationToken.None);
            Assert.That(ring.Signer.Kid, Is.EqualTo("k1"), "an unusable PEM keeps the current set");

            // A key added with a new kid then shows up in the log by kid only.
            _secrets.Values[SigningId] = SigningJson(("k1", _k1), ("k2", _k2));
            await svc.RefreshOnceAsync(provider, CancellationToken.None);
            Assert.That(ring.Signing, Has.Count.EqualTo(2));

            string all = string.Join("\n", logs.Lines);
            Assert.That(all, Does.Contain(SigningId).And.Contain("k2"));
            Assert.That(all, Does.Not.Contain("SECRET-VALUE-XYZ").And.Not.Contain("PRIVATE KEY"));
            Assert.That(all, Does.Not.Contain(Convert.ToBase64String(_recovery)).And.Not.Contain(Convert.ToBase64String(_data)));
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public void A_configuration_change_without_signing_keys_keeps_an_ephemeral_or_injected_ring()
    {
        var injected = ServerKeyRing.Ephemeral();
        var cm = new ConfigurationManager();
        cm.AddInMemoryCollection(new Dictionary<string, string?> { ["HeroCat:Keys:AllowEphemeral"] = "true" });
        var s = new ServiceCollection();
        s.AddOptions<ServerOptions>().Bind(cm.GetSection(ServerOptions.Section));
        using var sp = s.BuildServiceProvider();
        var svc = new SigningKeyRotationService(cm, sp.GetRequiredService<IOptionsMonitor<ServerOptions>>(), new Lazy<ServerKeyRing>(injected),
                                                TimeProvider.System, NullLogger<SigningKeyRotationService>.Instance);
        string kid = injected.Signer.Kid;
        Assert.That(svc.Apply(new KeysOptions { AllowEphemeral = true }), Is.False);
        Assert.That(injected.Signer.Kid, Is.EqualTo(kid));
    }
}
