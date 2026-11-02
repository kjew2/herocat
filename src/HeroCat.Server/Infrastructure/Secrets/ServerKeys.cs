using System.Security.Cryptography;
using HeroCat.Server.Hosting;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Infrastructure.Secrets;

/// <summary>An RS256 signing key and its JWT <c>kid</c>.</summary>
public sealed record SigningKey(string Kid, RSA Rsa);

/// <summary>
/// The process's key material (specs/08 section 9.3), loaded once from <see cref="KeysOptions"/>: the JWT signing keys
/// (first signs, all verify), the support-code key and the data key. With <see cref="KeysOptions.AllowEphemeral"/> a
/// missing key is generated in memory; otherwise a missing key fails loudly on first use. The signing keys alone can be
/// replaced in place (<see cref="ReplaceSigning"/>, by <see cref="SigningKeyRotationService"/>) so a rotation reaches a
/// running process; the support-code and data keys stay as loaded (issue #272).
/// </summary>
public sealed class ServerKeyRing
{
    public const int DataKeyBytes = 32;

    IReadOnlyList<SigningKey> _signing;

    public ServerKeyRing(IReadOnlyList<SigningKey> signing, byte[] recoveryKey, byte[] dataKey)
    {
        CheckSigning(signing);
        if (recoveryKey.Length < 32) throw new ArgumentException("the recovery key needs at least 32 bytes", nameof(recoveryKey));
        if (dataKey.Length != DataKeyBytes) throw new ArgumentException($"the data key is {DataKeyBytes} bytes", nameof(dataKey));
        _signing = signing;
        RecoveryKey = recoveryKey;
        DataKey = dataKey;
    }

    static void CheckSigning(IReadOnlyList<SigningKey> signing)
    {
        if (signing.Count == 0) throw new ArgumentException("at least one signing key", nameof(signing));
        if (signing.Select(k => k.Kid).Distinct(StringComparer.Ordinal).Count() != signing.Count) throw new ArgumentException("duplicate kid", nameof(signing));
    }

    /// <summary>A snapshot: read it once per operation.</summary>
    public IReadOnlyList<SigningKey> Signing => Volatile.Read(ref _signing);
    public SigningKey Signer => Signing[0];
    public byte[] RecoveryKey { get; }
    public byte[] DataKey { get; }

    public SigningKey? Find(string kid) => Signing.FirstOrDefault(k => k.Kid == kid);

    /// <summary>Swaps in a new signing set (a rotation): the first signs from now on, every one verifies.</summary>
    public void ReplaceSigning(IReadOnlyList<SigningKey> signing)
    {
        CheckSigning(signing);
        Volatile.Write(ref _signing, signing);
    }

    /// <summary>A ring of fresh random keys (tests, and <see cref="KeysOptions.AllowEphemeral"/>).</summary>
    public static ServerKeyRing Ephemeral(int signingKeys = 1) => new(
        Enumerable.Range(0, signingKeys).Select(i => new SigningKey("ephemeral-" + i + "-" + Guid.NewGuid().ToString("N")[..8], RSA.Create(2048))).ToList(),
        RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(DataKeyBytes));

    public static ServerKeyRing FromOptions(KeysOptions o, ILogger log, IDataKeyUnwrapper? kms = null, string env = "dev")
    {
        var signing = SigningFromOptions(o);
        if (signing.Count == 0)
        {
            Ephemeral(o, "HeroCat:Keys:SigningKeys", log);
            signing.Add(new SigningKey("ephemeral-" + Guid.NewGuid().ToString("N")[..8], RSA.Create(2048)));
        }

        byte[] recovery = Bytes(o.RecoveryKey, o.RecoveryKeyFile, "HeroCat:Keys:RecoveryKey", o, log, 32);
        byte[] data = o.DataKeyWrapped.Length > 0
            ? Unwrap(o, kms ?? new NoDataKeyUnwrapper(), env)
            : Bytes(o.DataKey, o.DataKeyFile, "HeroCat:Keys:DataKey", o, log, DataKeyBytes);
        return new ServerKeyRing(signing, recovery, data);
    }

    /// <summary>The configured signing keys, without an ephemeral fallback (empty when none is configured).</summary>
    public static List<SigningKey> SigningFromOptions(KeysOptions o)
    {
        var signing = new List<SigningKey>();
        foreach (var k in o.SigningKeys)
        {
            if (string.IsNullOrWhiteSpace(k.Kid)) throw new InvalidOperationException("HeroCat:Keys:SigningKeys: every key needs a Kid");
            string pem = Read(k.Pem, k.PemFile, $"HeroCat:Keys:SigningKeys[{k.Kid}]");
            var rsa = RSA.Create();
            try { rsa.ImportFromPem(pem); }
            catch (Exception e) when (e is ArgumentException or CryptographicException)
            {
                rsa.Dispose();
                throw new InvalidOperationException($"HeroCat:Keys:SigningKeys[{k.Kid}] is not an RSA private key PEM");
            }
            signing.Add(new SigningKey(k.Kid, rsa));
        }
        return signing;
    }

    /// <summary>The KMS envelope: <c>Decrypt</c> once, when the ring is built (issue #272).</summary>
    static byte[] Unwrap(KeysOptions o, IDataKeyUnwrapper kms, string env)
    {
        if (o.DataKey.Length > 0 || o.DataKeyFile.Length > 0)
            throw new InvalidOperationException("HeroCat:Keys: set DataKeyWrapped or DataKey/DataKeyFile, not both");
        byte[] wrapped;
        try { wrapped = Convert.FromBase64String(o.DataKeyWrapped.Trim()); }
        catch (FormatException) { throw new InvalidOperationException("HeroCat:Keys:DataKeyWrapped is not base64"); }
        byte[] key;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            key = kms.UnwrapAsync(wrapped, o.DataKeyKmsKeyId, DataKeyEnvelope.Context(env), cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            throw new InvalidOperationException($"HeroCat:Keys:DataKeyWrapped: KMS Decrypt failed ({e.GetType().Name})");
        }
        if (key.Length != DataKeyBytes)
            throw new InvalidOperationException($"HeroCat:Keys:DataKeyWrapped does not unwrap to {DataKeyBytes} bytes (generate it with --key-spec AES_256)");
        return key;
    }

    static byte[] Bytes(string inline, string file, string name, KeysOptions o, ILogger log, int generate)
    {
        string text = inline.Length > 0 || file.Length > 0 ? Read(inline, file, name) : "";
        if (text.Length > 0) return Convert.FromBase64String(text.Trim());
        Ephemeral(o, name, log);
        return RandomNumberGenerator.GetBytes(generate);
    }

    static string Read(string inline, string file, string name)
    {
        if (inline.Length > 0) return inline;
        if (file.Length > 0) return File.ReadAllText(file);
        throw new InvalidOperationException($"{name} is not configured");
    }

    static void Ephemeral(KeysOptions o, string name, ILogger log)
    {
        if (!o.AllowEphemeral)
            throw new InvalidOperationException($"{name} is not configured (set it, or HeroCat:Keys:AllowEphemeral for a local run)");
        log.LogWarning("{Key} is not configured; using an ephemeral in-memory key (local runs and tests only)", name);
    }

    /// <summary>The DI factory: built on first use from the current options (the data key unwrapped by KMS when wrapped).</summary>
    public static ServerKeyRing Create(IServiceProvider sp)
    {
        var o = sp.GetRequiredService<IOptionsMonitor<ServerOptions>>().CurrentValue;
        return FromOptions(o.Keys, sp.GetRequiredService<ILoggerFactory>().CreateLogger<ServerKeyRing>(),
                           sp.GetService<IDataKeyUnwrapper>(), o.Env);
    }
}

/// <summary>
/// Authenticated encryption of small secrets at rest under the data key (specs/08 section 9.3: device HMAC keys in
/// <c>devices.hmac_key_enc</c>; idempotency bodies that carry tokens). AES-256-GCM; the associated data binds a
/// ciphertext to its row so it cannot be moved to another. Format: version byte 1, 12-byte nonce, 16-byte tag, ciphertext.
/// In AWS the data key itself arrives KMS-wrapped (<see cref="KeysOptions.DataKeyWrapped"/>, issue #272).
/// </summary>
public sealed class SecretProtector
{
    const byte Version = 1;
    const int NonceBytes = 12;
    const int TagBytes = 16;

    readonly Lazy<ServerKeyRing> _keys;

    public SecretProtector(ServerKeyRing keys) : this(new Lazy<ServerKeyRing>(keys)) { }

    /// <summary>Lazy, so a process without keys starts (and serves <c>/health</c>) and fails only where a key is used.</summary>
    public SecretProtector(Lazy<ServerKeyRing> keys) => _keys = keys;

    byte[] Key => _keys.Value.DataKey;

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var output = new byte[1 + NonceBytes + TagBytes + plaintext.Length];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(Key, TagBytes);
        aes.Encrypt(nonce, plaintext, output.AsSpan(1 + NonceBytes + TagBytes), output.AsSpan(1 + NonceBytes, TagBytes), associatedData);
        return output;
    }

    /// <exception cref="CryptographicException">Tampered, wrong key or wrong associated data.</exception>
    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes, ReadOnlySpan<byte> associatedData)
    {
        if (protectedBytes.Length < 1 + NonceBytes + TagBytes || protectedBytes[0] != Version)
            throw new CryptographicException("not a protected secret");
        var plaintext = new byte[protectedBytes.Length - 1 - NonceBytes - TagBytes];
        using var aes = new AesGcm(Key, TagBytes);
        aes.Decrypt(protectedBytes.Slice(1, NonceBytes), protectedBytes[(1 + NonceBytes + TagBytes)..], protectedBytes.Slice(1 + NonceBytes, TagBytes),
                    plaintext, associatedData);
        return plaintext;
    }
}
