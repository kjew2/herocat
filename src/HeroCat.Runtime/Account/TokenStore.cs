using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HeroCat.Runtime.Account
{
    /// <summary>
    /// Platform secure storage (02 section 8.4: <c>device_key</c> and <c>tokens.json</c>; iOS Keychain, Android
    /// Keystore-wrapped storage, both excluded from OS backup). The native implementations are Unity-only (issue #151);
    /// engine-free code and tests use <see cref="MemorySecureStore"/> or <see cref="FileSecureStore"/>. Implementations
    /// are thread-safe: the AuthClient writes tokens from the thread pool.
    /// </summary>
    public interface ISecureStore
    {
        /// <summary>The bytes stored under <paramref name="key"/>, or null when absent or unreadable.</summary>
        byte[] Get(string key);

        /// <summary>Stores <paramref name="value"/> durably before returning. Throws IOException when it cannot.</summary>
        void Set(string key, byte[] value);

        void Delete(string key);
    }

    /// <summary>In-memory <see cref="ISecureStore"/> for tests and the bot harness.</summary>
    public sealed class MemorySecureStore : ISecureStore
    {
        readonly Dictionary<string, byte[]> _items = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>Test hook: while true, Set throws IOException and changes nothing.</summary>
        public bool FailWrites { get; set; }

        public int Count
        {
            get { lock (_items) return _items.Count; }
        }

        public byte[] Get(string key)
        {
            lock (_items) return _items.TryGetValue(key, out var v) ? (byte[])v.Clone() : null;
        }

        public void Set(string key, byte[] value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (FailWrites) throw new IOException("MemorySecureStore.FailWrites is set");
            lock (_items) _items[key] = (byte[])value.Clone();
        }

        public void Delete(string key)
        {
            lock (_items) _items.Remove(key);
        }
    }

    /// <summary>
    /// Plain files under one directory (atomic tmp + rename). NOT secure: the test double and the stand-in for editor and
    /// desktop builds until the platform stores of issue #151 replace it on device.
    /// </summary>
    public sealed class FileSecureStore : ISecureStore
    {
        readonly string _dir;
        readonly object _lock = new object();

        public FileSecureStore(string directory)
        {
            _dir = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        string PathOf(string key)
        {
            if (string.IsNullOrEmpty(key) || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || key.StartsWith(".", StringComparison.Ordinal))
                throw new ArgumentException("not a plain file name: " + key, nameof(key));
            return Path.Combine(_dir, key);
        }

        public byte[] Get(string key)
        {
            var p = PathOf(key);
            lock (_lock)
            {
                try { return File.Exists(p) ? File.ReadAllBytes(p) : null; }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
        }

        public void Set(string key, byte[] value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var p = PathOf(key);
            lock (_lock)
            {
                Directory.CreateDirectory(_dir);
                var tmp = p + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(value, 0, value.Length);
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
        }

        public void Delete(string key)
        {
            var p = PathOf(key);
            lock (_lock)
            {
                if (File.Exists(p)) File.Delete(p);
            }
        }
    }

    /// <summary>
    /// What <c>tokens.json</c> holds (02 section 8.4): the account this install is signed in to and its tokens (08
    /// section 2.1). <see cref="PlayerId"/> without tokens is a cached account kept for offline play (01 section 8.1
    /// step 3). <see cref="PendingGuestRequestId"/> is the <c>X-Request-Id</c> of a <c>POST /auth/guest</c> that has not
    /// yet returned: it is written before the call, so a response lost to a timeout or a kill is replayed by the server
    /// (08 section 1.1) on the next attempt instead of answering <c>DEVICE_ALREADY_BOUND</c>.
    /// </summary>
    public sealed record AuthTokens
    {
        public Guid PlayerId { get; init; }
        public string AccessToken { get; init; }
        public string RefreshToken { get; init; }

        /// <summary>The access token's <c>exp</c>, unix seconds (server time).</summary>
        public long AccessExpiresAt { get; init; }

        public string PendingGuestRequestId { get; init; }

        public bool HasSession => !string.IsNullOrEmpty(RefreshToken);
    }

    /// <summary>The tokens' persistence (01 section 5.1's <c>ITokenStore</c>). Thread-safe.</summary>
    public interface ITokenStore
    {
        /// <summary>The stored record, or null when nothing (or nothing readable) is stored.</summary>
        AuthTokens Load();

        /// <summary>Replaces the record durably before returning. Throws IOException when it cannot.</summary>
        void Save(AuthTokens tokens);

        /// <summary>Forgets the account and its tokens (logout, account deletion).</summary>
        void Clear();
    }

    /// <summary><see cref="ITokenStore"/> as <c>tokens.json</c> in an <see cref="ISecureStore"/> (02 section 8.4).</summary>
    public sealed class SecureTokenStore : ITokenStore
    {
        public const string Key = "tokens.json";
        static readonly Encoding Utf8 = new UTF8Encoding(false);
        readonly ISecureStore _store;

        public SecureTokenStore(ISecureStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public AuthTokens Load()
        {
            var bytes = _store.Get(Key);
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                if (!(JToken.Parse(Utf8.GetString(bytes)) is JObject o)) return null;
                Guid.TryParse((string)o["playerId"], out var player);
                return new AuthTokens
                {
                    PlayerId = player,
                    AccessToken = (string)o["accessToken"],
                    RefreshToken = (string)o["refreshToken"],
                    AccessExpiresAt = o["accessExpiresAt"]?.Type == JTokenType.Integer ? (long)o["accessExpiresAt"] : 0,
                    PendingGuestRequestId = (string)o["pendingGuestRequestId"],
                };
            }
            catch (Exception e) when (e is JsonException || e is FormatException || e is InvalidCastException || e is ArgumentException)
            {
                return null;   // an unreadable file is no account: boot signs in again
            }
        }

        public void Save(AuthTokens t)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            var o = new JObject
            {
                ["v"] = 1,
                ["playerId"] = t.PlayerId == Guid.Empty ? null : t.PlayerId.ToString("D"),
                ["accessToken"] = t.AccessToken,
                ["refreshToken"] = t.RefreshToken,
                ["accessExpiresAt"] = t.AccessExpiresAt,
                ["pendingGuestRequestId"] = t.PendingGuestRequestId,
            };
            _store.Set(Key, Utf8.GetBytes(o.ToString(Formatting.None)));
        }

        public void Clear() => _store.Delete(Key);
    }

    /// <summary>
    /// The save HMAC key (02 sections 8.4 and 10): <see cref="KeyBytes"/> random bytes, created at first launch and kept
    /// in secure storage as <c>device_key</c>. <c>POST /auth/guest</c> registers it.
    /// </summary>
    public static class DeviceKey
    {
        public const string Key = "device_key";
        public const int KeyBytes = 32;

        /// <summary>The stored key, or a fresh one stored now. A stored value of the wrong length is replaced.</summary>
        public static byte[] LoadOrCreate(ISecureStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            var k = store.Get(Key);
            if (k != null && k.Length == KeyBytes) return k;
            k = new byte[KeyBytes];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(k);
            try { store.Set(Key, k); }
            catch (IOException) { }   // usable for this session; the next launch makes another (and re-registers it)
            return k;
        }
    }
}
