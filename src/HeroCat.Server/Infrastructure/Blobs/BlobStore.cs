using System.Collections.Concurrent;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using HeroCat.Server.Hosting;
using Microsoft.Extensions.Options;

namespace HeroCat.Server.Infrastructure.Blobs;

/// <summary>
/// Object storage for save blobs and receipts (specs/08 section 9.1). Feature code sees only this interface:
/// <see cref="S3BlobStore"/> in the environments (and against MinIO with <c>ServiceUrl</c>), <see cref="InMemoryBlobStore"/>
/// in unit tests and local runs.
/// </summary>
public interface IBlobStore
{
    Task PutAsync(string bucket, string key, byte[] body, string contentType, CancellationToken ct = default);

    /// <summary>The object's bytes, or null when it does not exist.</summary>
    Task<byte[]?> GetAsync(string bucket, string key, CancellationToken ct = default);

    Task DeleteAsync(string bucket, string key, CancellationToken ct = default);
}

public sealed class InMemoryBlobStore : IBlobStore
{
    readonly ConcurrentDictionary<(string bucket, string key), byte[]> _objects = new();

    public Task PutAsync(string bucket, string key, byte[] body, string contentType, CancellationToken ct = default)
    {
        _objects[(bucket, key)] = (byte[])body.Clone();
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string bucket, string key, CancellationToken ct = default) =>
        Task.FromResult(_objects.TryGetValue((bucket, key), out var b) ? (byte[]?)b.Clone() : null);

    public Task DeleteAsync(string bucket, string key, CancellationToken ct = default)
    {
        _objects.TryRemove((bucket, key), out _);
        return Task.CompletedTask;
    }
}

/// <summary>AWS S3, or any S3-compatible store (MinIO) when <see cref="BlobOptions.ServiceUrl"/> is set. Credentials come from the default AWS chain.</summary>
public sealed class S3BlobStore : IBlobStore, IDisposable
{
    readonly Lazy<AmazonS3Client> _client;

    public S3BlobStore(IOptions<ServerOptions> options)
    {
        var o = options.Value.Blobs;
        _client = new Lazy<AmazonS3Client>(() => new AmazonS3Client(Config(o)));
    }

    internal static AmazonS3Config Config(BlobOptions o)
    {
        var c = new AmazonS3Config { ForcePathStyle = o.ForcePathStyle };
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

    public async Task PutAsync(string bucket, string key, byte[] body, string contentType, CancellationToken ct = default)
    {
        using var stream = new MemoryStream(body, writable: false);
        await _client.Value.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = key, InputStream = stream, ContentType = contentType,
        }, ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetAsync(string bucket, string key, CancellationToken ct = default)
    {
        try
        {
            using var r = await _client.Value.GetObjectAsync(bucket, key, ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await r.ResponseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteAsync(string bucket, string key, CancellationToken ct = default) =>
        _client.Value.DeleteObjectAsync(bucket, key, ct);

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }
}
