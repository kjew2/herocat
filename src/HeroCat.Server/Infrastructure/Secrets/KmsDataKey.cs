using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using HeroCat.Server.Hosting;

namespace HeroCat.Server.Infrastructure.Secrets;

/// <summary>Unwraps a KMS-wrapped data key (KMS <c>Decrypt</c>). Behind an interface so tests run without AWS.</summary>
public interface IDataKeyUnwrapper
{
    /// <param name="keyId">The KMS key the envelope must belong to, or empty for any.</param>
    Task<byte[]> UnwrapAsync(byte[] wrapped, string keyId, IReadOnlyDictionary<string, string> encryptionContext, CancellationToken ct);
}

/// <summary>The data key's KMS envelope (specs/08 section 9.3, issue #272).</summary>
public static class DataKeyEnvelope
{
    public const string PurposeContextKey = "herocat:purpose";
    public const string PurposeContextValue = "data-key";
    public const string EnvContextKey = "herocat:env";

    /// <summary>
    /// The encryption context the envelope is made with and unwrapped under: it binds a wrapped key to its purpose and
    /// environment, so a stage envelope does not unwrap in prod even under a shared KMS key.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Context(string env) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [PurposeContextKey] = PurposeContextValue,
        [EnvContextKey] = env,
    };
}

/// <summary>Not configured for KMS: a wrapped data key cannot be used (a local run sets <c>DataKey</c> or uses ephemeral keys).</summary>
public sealed class NoDataKeyUnwrapper : IDataKeyUnwrapper
{
    public Task<byte[]> UnwrapAsync(byte[] wrapped, string keyId, IReadOnlyDictionary<string, string> encryptionContext, CancellationToken ct) =>
        throw new InvalidOperationException("HeroCat:Keys:DataKeyWrapped is set but HeroCat:Secrets:Provider is not aws, so there is no KMS to unwrap it");
}

/// <summary>The AWS KMS <see cref="IDataKeyUnwrapper"/> (credentials from the ECS task role).</summary>
public sealed class AwsKmsDataKeyUnwrapper : IDataKeyUnwrapper, IDisposable
{
    readonly Lazy<AmazonKeyManagementServiceClient> _client;

    public AwsKmsDataKeyUnwrapper(SecretsOptions o) =>
        _client = new Lazy<AmazonKeyManagementServiceClient>(() => new AmazonKeyManagementServiceClient(Config(o)));

    internal static AmazonKeyManagementServiceConfig Config(SecretsOptions o)
    {
        var c = new AmazonKeyManagementServiceConfig();
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

    public async Task<byte[]> UnwrapAsync(byte[] wrapped, string keyId, IReadOnlyDictionary<string, string> encryptionContext, CancellationToken ct)
    {
        using var blob = new MemoryStream(wrapped, writable: false);
        var request = new DecryptRequest
        {
            CiphertextBlob = blob,
            EncryptionContext = new Dictionary<string, string>(encryptionContext),
        };
        if (keyId.Length > 0) request.KeyId = keyId;
        var r = await _client.Value.DecryptAsync(request, ct).ConfigureAwait(false);
        return r.Plaintext.ToArray();
    }

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }
}
