using System.Security.Cryptography;
using HeroCat.Server.Hosting;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Infrastructure.Secrets;

/// <summary>
/// Brings a JWT signing-key rotation into the running api without a restart (specs/08 section 9.3, issue #272): every
/// <see cref="SecretsOptions.RefreshSeconds"/> it re-reads the signing-key secret through
/// <see cref="SecretsManagerConfigurationProvider"/>, and on any configuration change of <c>HeroCat:Keys:SigningKeys</c>
/// (<see cref="IOptionsMonitor{TOptions}"/>) it swaps the new set into the <see cref="ServerKeyRing"/>. A change that
/// leaves no configured signing key, or a malformed one, keeps the current set; the support-code and data keys never
/// change in a running process. Logs key ids only.
/// </summary>
public sealed class SigningKeyRotationService : BackgroundService
{
    readonly IConfiguration _configuration;
    readonly IOptionsMonitor<ServerOptions> _options;
    readonly Lazy<ServerKeyRing> _ring;
    readonly TimeProvider _clock;
    readonly ILogger<SigningKeyRotationService> _log;
    readonly object _gate = new();
    IDisposable? _subscription;

    public SigningKeyRotationService(IConfiguration configuration, IOptionsMonitor<ServerOptions> options, Lazy<ServerKeyRing> ring,
                                     TimeProvider clock, ILogger<SigningKeyRotationService> log)
    {
        _configuration = configuration;
        _options = options;
        _ring = ring;
        _clock = clock;
        _log = log;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _options.OnChange(o => Apply(o.Keys));
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var provider = _configuration.SecretsProvider();
        int seconds = provider?.Options.RefreshSeconds ?? 0;
        if (provider == null || seconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await RefreshOnceAsync(provider, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>One refresh tick: a failed read keeps the current keys (and is retried next tick).</summary>
    public async Task RefreshOnceAsync(SecretsManagerConfigurationProvider provider, CancellationToken ct)
    {
        try
        {
            if (await provider.RefreshSigningKeysAsync(ct).ConfigureAwait(false))
                Apply(_options.CurrentValue.Keys);
        }
        catch (InvalidOperationException e)
        {
            _log.LogWarning("Signing-key refresh failed, keeping the current keys: {Reason}", e.Message);
        }
    }

    /// <summary>Swaps the configured signing set into the ring if it differs. True when it did.</summary>
    public bool Apply(KeysOptions keys)
    {
        // Not built yet: the ring reads the current options when it is first used.
        if (!_ring.IsValueCreated) return false;
        lock (_gate)
        {
            List<SigningKey> next;
            try { next = ServerKeyRing.SigningFromOptions(keys); }
            catch (InvalidOperationException e)
            {
                _log.LogError("Signing keys in configuration are unusable, keeping the current keys: {Reason}", e.Message);
                return false;
            }
            if (next.Count == 0) return false;

            var ring = _ring.Value;
            if (Same(ring.Signing, next))
            {
                foreach (var k in next) k.Rsa.Dispose();
                return false;
            }
            try { ring.ReplaceSigning(next); }
            catch (ArgumentException e)
            {
                _log.LogError("Signing keys in configuration are unusable, keeping the current keys: {Reason}", e.Message);
                return false;
            }
            // The outgoing RSA objects are not disposed: a request may still hold one.
            _log.LogInformation("Signing keys replaced: signer {Kid}, active {Kids}", next[0].Kid, string.Join(",", next.Select(k => k.Kid)));
            return true;
        }
    }

    static bool Same(IReadOnlyList<SigningKey> a, IReadOnlyList<SigningKey> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Kid == p.Second.Kid &&
            CryptographicOperations.FixedTimeEquals(p.First.Rsa.ExportSubjectPublicKeyInfo(), p.Second.Rsa.ExportSubjectPublicKeyInfo()));
}
