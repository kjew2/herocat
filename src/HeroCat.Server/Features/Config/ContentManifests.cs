using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HeroCat.Server.Pipeline;

namespace HeroCat.Server.Features.Config;

/// <summary>Fetches a content manifest from the CDN (specs/09 bundle manifest). Null when it is not there or unreachable.</summary>
public interface IContentManifestFetcher
{
    Task<byte[]?> FetchAsync(string url, CancellationToken ct);
}

/// <summary>An HTTP GET of the manifest, capped at <see cref="MaxBytes"/>.</summary>
public sealed class HttpContentManifestFetcher : IContentManifestFetcher
{
    public const string ClientName = "content-manifest";
    public const int MaxBytes = 4 * 1024 * 1024;

    readonly IHttpClientFactory _http;
    readonly ILogger<HttpContentManifestFetcher> _log;

    public HttpContentManifestFetcher(IHttpClientFactory http, ILogger<HttpContentManifestFetcher> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<byte[]?> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient(ClientName);
            using var r = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode)
            {
                _log.LogWarning("content manifest {Url}: HTTP {Status}", url, (int)r.StatusCode);
                return null;
            }
            await using var body = await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + n > MaxBytes) throw new InvalidDataException($"manifest larger than {MaxBytes} bytes");
                buffer.Write(chunk, 0, n);
            }
            return buffer.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException && !ct.IsCancellationRequested)
        {
            _log.LogWarning(e, "content manifest {Url} unreachable", url);
            return null;
        }
    }
}

/// <summary>
/// The activation check of specs/08 section 6.2: every content pointer of the payload (<c>content</c>, and
/// <c>defaultContent</c> when present) names a manifest on the CDN whose bytes hash to its <c>sha256</c>, so a config
/// can never point at a bundle that is not there. A mismatch or a missing manifest is <c>400 CONTENT_HASH_MISMATCH</c>.
/// </summary>
public sealed class ContentManifestVerifier
{
    readonly IContentManifestFetcher _fetcher;

    public ContentManifestVerifier(IContentManifestFetcher fetcher) => _fetcher = fetcher;

    public async Task<ApiException?> VerifyAsync(JsonObject payload, CancellationToken ct)
    {
        foreach (string field in new[] { "content", "defaultContent" })
        {
            if (payload[field] is not JsonObject c) continue;
            string url = (string?)c["url"] ?? "";
            string expected = ((string?)c["sha256"] ?? "").ToLowerInvariant();
            byte[]? bytes = await _fetcher.FetchAsync(url, ct).ConfigureAwait(false);
            string? actual = bytes == null ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (actual != expected)
                return new ApiException(400, ConfigErrors.CONTENT_HASH_MISMATCH,
                    actual == null ? $"{field}: no manifest at {url}." : $"{field}: the manifest at {url} does not match sha256.",
                    new { field, url, expected, actual });
        }
        return null;
    }
}
