using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HeroCat.Remote.Api;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Errors;
using HeroCat.Runtime.Services;
using HeroCat.Sim.Content;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HeroCat.Remote.Services
{
    /// <summary>
    /// The CDN half of 09 section 10.3 (issue #212): <see cref="IContentStaging"/> over the process's one
    /// <see cref="ApiClient"/> (<see cref="ApiRequest.Cdn"/>) and the device's <see cref="ContentCache"/>.
    /// <list type="number">
    /// <item>Step 4: downloads <c>config.content.url</c> (the manifest) and checks its sha256 against
    /// <c>config.content.sha256</c> (<c>E900</c>), its <c>schema_version</c> (<c>E901</c>), its <c>content_version</c>
    /// against the offered one (<c>E900</c>) and, when a client version is given, <c>min_client_version</c> (<c>E903</c>)
    /// before any table is fetched. Then every file the manifest lists, from the manifest's own directory (10.2), each
    /// checked against its manifest sha256 (<c>E900</c>) before it is written to <c>&lt;version&gt;.tmp/</c>. A tampered
    /// file therefore never reaches the disk, let alone <see cref="ContentDb.Build"/>.</item>
    /// <item>Step 5 is <see cref="ContentUpdater"/>'s: it re-reads the staged copy from disk, builds it and calls
    /// <see cref="Discard"/> (the temp dir is deleted) or <see cref="MarkPending"/>.</item>
    /// <item>Step 6: <see cref="MarkPending"/> renames <c>&lt;version&gt;.tmp</c> to <c>&lt;version&gt;</c> and prunes to
    /// CONTENT_CACHE_KEEP (<see cref="ContentCache.Install"/>).</item>
    /// </list>
    /// Content failures throw <see cref="ContentBuildException"/> with the 09 section 8 code, so ContentUpdater's failure
    /// report (<c>content_update_failed</c>) carries it; a failed fetch throws <see cref="ServiceException"/> with the
    /// ApiClient's code (after its 5.2 retries), which ContentUpdater retries at the next config when it is retryable
    /// (issue #316). Runs on the thread pool.
    /// </summary>
    public sealed class CdnContentStaging : IContentStaging
    {
        readonly ApiClient _client;
        readonly ContentCache _cache;
        readonly string _clientVersion;

        /// <param name="clientVersion">This build's semver for the early E903 check; null leaves it to ContentUpdater.</param>
        public CdnContentStaging(ApiClient client, ContentCache cache, string clientVersion = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _clientVersion = clientVersion;
        }

        public ContentCache Cache => _cache;

        public async Task<IContentSource> DownloadAsync(ContentPointer bundle, CancellationToken ct)
        {
            if (bundle == null) throw new ArgumentNullException(nameof(bundle));
            if (!ContentVersion.IsValid(bundle.Version)) throw Error("E900", "manifest.json", "offered content_version '" + bundle.Version + "' is malformed");
            if (!Uri.TryCreate(bundle.Url, UriKind.Absolute, out var manifestUri) || manifestUri.Scheme != Uri.UriSchemeHttps)
                throw Error("E900", "manifest.json", "content url is not an https URL: " + bundle.Url);

            // Step 4a: the manifest, pinned by config.content.sha256.
            byte[] manifestBytes = await FetchAsync(manifestUri, ct).ConfigureAwait(false);
            string actual = Sha256Hex(manifestBytes);
            if (!string.Equals(actual, bundle.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw Error("E900", "manifest.json", $"manifest.json: sha256 {actual} does not match config {bundle.Sha256}");
            var files = ReadManifest(manifestBytes, bundle.Version, _clientVersion);

            // Step 4b: every listed file, verified before it is written.
            string tmp = _cache.BeginStaging(bundle.Version);
            foreach (var f in files)
            {
                byte[] data = await FetchAsync(new Uri(manifestUri, f.Key), ct).ConfigureAwait(false);
                string sha = Sha256Hex(data);
                if (!string.Equals(sha, f.Value, StringComparison.Ordinal))
                    throw Error("E900", f.Key, $"{f.Key}: sha256 {sha} does not match manifest {f.Value}");
                File.WriteAllBytes(Path.Combine(tmp, f.Key), data);
            }
            File.WriteAllBytes(Path.Combine(tmp, "manifest.json"), manifestBytes);   // last, like the CDN upload (10.2)
            return new FileContentSource(tmp);
        }

        public void MarkPending(string version) => _cache.Install(version);

        public void Discard(string version) => _cache.Discard(version);

        async Task<byte[]> FetchAsync(Uri url, CancellationToken ct)
        {
            var r = await _client.SendAsync<byte[]>(ApiRequest.Cdn(url.AbsoluteUri), ct).ConfigureAwait(false);
            return r.EnsureOk();
        }

        /// <summary>
        /// The manifest checks that need no table: schema_version (E901), content_version (E900), min_client_version
        /// (E903). Returns file name -> sha256 for every table and locale file. A file name that is not a plain
        /// <c>*.json</c> name in the manifest's directory (a path) is refused (E900): the cache writes it as a path.
        /// </summary>
        internal static Dictionary<string, string> ReadManifest(byte[] bytes, string offeredVersion, string clientVersion)
        {
            JObject m;
            try { m = JObject.Parse(new UTF8Encoding(false).GetString(bytes)); }
            catch (JsonException e) { throw Error("E901", "manifest.json", "manifest does not parse: " + e.Message); }
            var schema = m["schema_version"];
            if (schema == null || schema.Type != JTokenType.Integer || schema.Value<long>() != ContentLoader.SupportedSchemaVersion)
                throw Error("E901", "manifest.json",
                    $"manifest schema_version {schema} not supported (supports {ContentLoader.SupportedSchemaVersion})");
            string version = (string)(m["content_version"] as JValue);
            if (version != offeredVersion)
                throw Error("E900", "manifest.json", $"manifest content_version {version} is not the offered {offeredVersion}");
            string min = (string)(m["min_client_version"] as JValue);
            if (clientVersion != null && !string.IsNullOrEmpty(min) && ContentLoader.CompareSemver(min, clientVersion) > 0)
                throw Error("E903", "manifest.json", $"content requires client >= {min}, this client is {clientVersion}");

            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in new[] { "tables", "localization" })
            {
                if (!(m[group] is JObject entries)) continue;
                foreach (var kv in entries)
                {
                    string file = (string)(kv.Value?["file"] as JValue);
                    string sha = (string)(kv.Value?["sha256"] as JValue);
                    if (!IsPlainJsonName(file) || string.IsNullOrEmpty(sha))
                        throw Error("E900", "manifest.json", $"manifest entry {group}.{kv.Key} names no plain file and sha256: '{file}'");
                    files[file] = sha;
                }
            }
            return files;
        }

        static bool IsPlainJsonName(string f) =>
            !string.IsNullOrEmpty(f) && f != "manifest.json" && f.EndsWith(".json", StringComparison.Ordinal) && !f.StartsWith(".", StringComparison.Ordinal) &&
            f.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && f.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        static ContentBuildException Error(string code, string location, string message) =>
            new ContentBuildException(new[] { new ContentError(code, location, message) });

        static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(data);
                var sb = new StringBuilder(64);
                foreach (var b in h) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
