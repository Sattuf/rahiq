using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Rahiq.Application.Abstractions;

namespace Rahiq.Infrastructure.Common.Storage;

public sealed record StorageOptions
{
    public const string Section = "Storage";

    /// <summary>"s3" (Cloudflare R2 / MinIO) or "filesystem" (tests, offline development).</summary>
    public string Provider { get; init; } = "filesystem";

    public string Bucket { get; init; } = "rahiq";

    public string? ServiceUrl { get; init; }

    public string? AccessKey { get; init; }

    public string? SecretKey { get; init; }

    /// <summary>CDN base for public assets (product images). Private objects (lab reports, labels) use signed URLs.</summary>
    public string PublicBaseUrl { get; init; } = "http://localhost:5080/media";

    public string FileSystemRoot { get; init; } = "App_Data/blobs";
}

internal sealed class S3BlobStorage : IBlobStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly StorageOptions _options;

    public S3BlobStorage(IOptions<StorageOptions> options)
    {
        _options = options.Value;
        _client = new AmazonS3Client(_options.AccessKey, _options.SecretKey, new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
        });
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            DisablePayloadSigning = true,
        }, cancellationToken);

    public async Task<Stream?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.GetObjectAsync(_options.Bucket, key, cancellationToken);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await _client.DeleteObjectAsync(_options.Bucket, key, cancellationToken);

    public Uri GetUrl(string key, TimeSpan? signedFor = null) => signedFor is { } ttl
        ? new Uri(_client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Expires = DateTime.UtcNow.Add(ttl),
            Verb = HttpVerb.GET,
        }))
        : new Uri($"{_options.PublicBaseUrl.TrimEnd('/')}/{key}");

    public void Dispose() => _client.Dispose();
}

/// <summary>Development and test storage on local disk, served by the API under /media.</summary>
internal sealed class FileSystemBlobStorage(IOptions<StorageOptions> options) : IBlobStorage
{
    private readonly string _root = Path.GetFullPath(options.Value.FileSystemRoot);
    private readonly string _publicBase = options.Value.PublicBaseUrl.TrimEnd('/');

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task<Stream?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    public Uri GetUrl(string key, TimeSpan? signedFor = null) => new($"{_publicBase}/{key}");

    public string PathFor(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        return full;
    }
}
