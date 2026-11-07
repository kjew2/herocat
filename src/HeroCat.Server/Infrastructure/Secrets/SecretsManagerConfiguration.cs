using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using HeroCat.Server.Hosting;

namespace HeroCat.Server.Infrastructure.Secrets;

/// <summary>Reads one secret's current value (AWS Secrets Manager <c>AWSCURRENT</c>). Behind an interface so tests run without AWS.</summary>
public interface ISecretSource
{
    /// <summary>The secret's string (a binary secret comes back base64).</summary>
    Task<string> GetSecretAsync(string secretId, CancellationToken ct);
}

/// <summary>The AWS Secrets Manager <see cref="ISecretSource"/> (credentials from the ECS task role).</summary>
public sealed class AwsSecretSource : ISecretSource, IDisposable
{
    readonly Lazy<AmazonSecretsManagerClient> _client;

    public AwsSecretSource(SecretsOptions o) =>
        _client = new Lazy<AmazonSecretsManagerClient>(() => new AmazonSecretsManagerClient(Config(o)));

    internal static AmazonSecretsManagerConfig Config(SecretsOptions o)
    {
        var c = new AmazonSecretsManagerConfig();
        if (!string.IsNullOrWhiteSpace(o.ServiceUrl))
        {
            c.ServiceURL = o.ServiceUrl;
            c.AuthenticationRegion = o.Region;
        }
        else
        {
            c.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(o.Region);
        }
        return c;
    }

    public async Task<string> GetSecretAsync(string secretId, CancellationToken ct)
    {
        var r = await _client.Value.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId }, ct).ConfigureAwait(false);
        if (r.SecretString != null) return r.SecretString;
        if (r.SecretBinary != null) return Convert.ToBase64String(r.SecretBinary.ToArray());
        throw new InvalidOperationException($"secret '{secretId}' has no value");
    }

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }
}

/// <summary>
/// The configuration source of the named key secrets (specs/08 section 9.3, issue #272): <see cref="SecretsOptions"/>
/// says which secret fills which <c>HeroCat:Keys</c> setting. Added last, so a secret overrides appsettings.
/// </summary>
public sealed class SecretsManagerConfigurationSource : IConfigurationSource
{
    public SecretsManagerConfigurationSource(SecretsOptions options, ISecretSource secrets)
    {
        Options = options;
        Secrets = secrets;
    }

    public SecretsOptions Options { get; }
    public ISecretSource Secrets { get; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new SecretsManagerConfigurationProvider(Options, Secrets);
}

/// <summary>
/// Loads the key secrets into configuration at start (a failure there fails the start) and re-reads the signing-key
/// secret on <see cref="RefreshSigningKeysAsync"/>, raising the configuration reload token when it changed so
/// <c>IOptionsMonitor&lt;ServerOptions&gt;</c> sees the new ring. Never logs or reports a secret value: errors name the
/// secret id only.
/// </summary>
public sealed class SecretsManagerConfigurationProvider : ConfigurationProvider
{
    public const string KeysPrefix = ServerOptions.Section + ":Keys:";
    static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);

    readonly SecretsOptions _options;
    readonly ISecretSource _secrets;
    readonly SemaphoreSlim _refresh = new(1, 1);

    public SecretsManagerConfigurationProvider(SecretsOptions options, ISecretSource secrets)
    {
        _options = options;
        _secrets = secrets;
    }

    public SecretsOptions Options => _options;

    /// <summary>Configuration providers load synchronously; this runs once, at host build.</summary>
    public override void Load()
    {
        using var cts = new CancellationTokenSource(LoadTimeout);
        Data = LoadAllAsync(cts.Token).GetAwaiter().GetResult();
    }

    async Task<IDictionary<string, string?>> LoadAllAsync(CancellationToken ct)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (_options.SigningKeysSecretId.Length > 0)
            AddSigningKeys(data, _options.SigningKeysSecretId, await Fetch(_options.SigningKeysSecretId, ct).ConfigureAwait(false));
        if (_options.RecoveryKeySecretId.Length > 0)
            data[KeysPrefix + nameof(KeysOptions.RecoveryKey)] = (await Fetch(_options.RecoveryKeySecretId, ct).ConfigureAwait(false)).Trim();
        if (_options.DataKeySecretId.Length > 0)
            data[KeysPrefix + nameof(KeysOptions.DataKeyWrapped)] = (await Fetch(_options.DataKeySecretId, ct).ConfigureAwait(false)).Trim();
        return data;
    }

    /// <summary>
    /// Re-reads the signing-key secret. True when it changed (and the reload token fired). Throws on a failed read or a
    /// malformed secret, leaving the loaded values as they were.
    /// </summary>
    public async Task<bool> RefreshSigningKeysAsync(CancellationToken ct)
    {
        if (_options.SigningKeysSecretId.Length == 0) return false;
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fresh = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            AddSigningKeys(fresh, _options.SigningKeysSecretId, await Fetch(_options.SigningKeysSecretId, ct).ConfigureAwait(false));

            string signingPrefix = KeysPrefix + nameof(KeysOptions.SigningKeys) + ":";
            var next = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Data)
                if (!kv.Key.StartsWith(signingPrefix, StringComparison.OrdinalIgnoreCase)) next[kv.Key] = kv.Value;
            foreach (var kv in fresh) next[kv.Key] = kv.Value;

            bool changed = next.Count != Data.Count || next.Any(kv => !Data.TryGetValue(kv.Key, out var v) || v != kv.Value);
            if (!changed) return false;
            Data = next;
            OnReload();
            return true;
        }
        finally
        {
            _refresh.Release();
        }
    }

    async Task<string> Fetch(string secretId, CancellationToken ct)
    {
        try
        {
            return await _secrets.GetSecretAsync(secretId, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The message names the id and the error type only; an SDK message carries no secret value, but keep it out anyway.
            throw new InvalidOperationException($"secret '{secretId}': read failed ({e.GetType().Name})");
        }
    }

    /// <summary>
    /// The signing-key secret: a JSON array of <c>{ "kid", "pem" }</c> (property names case-insensitive), the signer
    /// first, at most a few entries (two during a rotation).
    /// </summary>
    internal static void AddSigningKeys(IDictionary<string, string?> data, string secretId, string json)
    {
        string Malformed() => $"secret '{secretId}': expected a JSON array of {{\"kid\", \"pem\"}} objects";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new InvalidOperationException(Malformed()); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                throw new InvalidOperationException(Malformed());
            int i = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string? kid = null, pem = null;
                if (e.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(Malformed());
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.String) continue;
                    if (p.Name.Equals("kid", StringComparison.OrdinalIgnoreCase)) kid = p.Value.GetString();
                    else if (p.Name.Equals("pem", StringComparison.OrdinalIgnoreCase)) pem = p.Value.GetString();
                }
                if (string.IsNullOrWhiteSpace(kid) || string.IsNullOrWhiteSpace(pem)) throw new InvalidOperationException(Malformed());
                string at = KeysPrefix + nameof(KeysOptions.SigningKeys) + ":" + i + ":";
                data[at + nameof(SigningKeyOptions.Kid)] = kid;
                data[at + nameof(SigningKeyOptions.Pem)] = pem;
                i++;
            }
        }
    }
}

public static class SecretsManagerConfigurationExtensions
{
    /// <summary>
    /// Adds the key-secret provider when <c>HeroCat:Secrets:Provider</c> is <c>aws</c> (read from the configuration built
    /// so far: appsettings, environment, command line). <paramref name="secrets"/> replaces AWS in tests.
    /// </summary>
    public static IConfigurationBuilder AddHeroCatSecrets(this IConfigurationManager configuration, ISecretSource? secrets = null)
    {
        var o = configuration.GetSection(ServerOptions.Section + ":Secrets").Get<SecretsOptions>() ?? new SecretsOptions();
        switch (o.Provider)
        {
            case SecretProviders.None:
                return configuration;
            case SecretProviders.Aws:
                configuration.Add(new SecretsManagerConfigurationSource(o, secrets ?? new AwsSecretSource(o)));
                return configuration;
            default:
                throw new InvalidOperationException($"HeroCat:Secrets:Provider '{o.Provider}': expected {SecretProviders.None} or {SecretProviders.Aws}");
        }
    }

    /// <summary>The key-secret provider of a built configuration, if one was added.</summary>
    public static SecretsManagerConfigurationProvider? SecretsProvider(this IConfiguration configuration) =>
        (configuration as IConfigurationRoot)?.Providers.OfType<SecretsManagerConfigurationProvider>().FirstOrDefault();
}
