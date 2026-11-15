using System.Net;

using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace VerifactuShopify.Storage;

/// <summary>Where the bucket is. Credentials are not here: they come from the SDK's standard chain.</summary>
/// <param name="Bucket">Bucket name.</param>
/// <param name="ServiceUrl">Endpoint of an S3-compatible provider (MinIO, Cloudflare R2...); <c>null</c> for AWS.</param>
/// <param name="Region">Region of the bucket; <c>null</c> to let the SDK resolve it (environment, profile).</param>
/// <param name="ForcePathStyle">Address the bucket in the path instead of the host name, as most compatible providers need.</param>
public sealed record S3ObjectStoreOptions(string Bucket, string? ServiceUrl = null, string? Region = null, bool ForcePathStyle = false);

/// <summary>
/// <see cref="IObjectStore"/> on S3 and compatible providers. The conditions travel as the standard
/// <c>If-None-Match</c> and <c>If-Match</c> headers, so the same code works wherever they are honoured.
/// </summary>
public sealed class S3ObjectStore(IAmazonS3 client, string bucket) : IObjectStore
{
    /// <summary>
    /// Builds a store. Without <paramref name="credentials"/> it uses the SDK's default chain
    /// (environment, profile, role...), which is what a deployment does.
    /// </summary>
    public static S3ObjectStore Create(S3ObjectStoreOptions options, AWSCredentials? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            // Checksums only where the service requires them: compatible providers reject the trailer
            // the SDK adds by default.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (options.ServiceUrl is { Length: > 0 } serviceUrl)
        {
            config.ServiceURL = serviceUrl;
            // A custom endpoint has no region to resolve: sign for the one given, or a neutral one.
            config.AuthenticationRegion = options.Region ?? "us-east-1";
        }
        else if (options.Region is { Length: > 0 } region)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        return new S3ObjectStore(credentials is null ? new AmazonS3Client(config) : new AmazonS3Client(credentials, config), options.Bucket);
    }

    public async Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.GetObjectAsync(bucket, key, cancellationToken);
            using var content = new MemoryStream();
            await response.ResponseStream.CopyToAsync(content, cancellationToken);
            return new StoredObject(content.ToArray(), response.ETag);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<string> PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var response = await PutAsync(NewPut(key, content), cancellationToken);
        return response.ETag;
    }

    public async Task<WriteOutcome> PutIfAbsentAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var request = NewPut(key, content);
        request.IfNoneMatch = "*";
        return await PutConditionalAsync(request, cancellationToken);
    }

    public async Task<WriteOutcome> PutIfUnchangedAsync(string key, ReadOnlyMemory<byte> content, string eTag, CancellationToken cancellationToken = default)
    {
        var request = NewPut(key, content);
        request.IfMatch = eTag;
        return await PutConditionalAsync(request, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        await client.DeleteObjectAsync(bucket, key, cancellationToken);

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await client.ListObjectsV2Async(request, cancellationToken);
            keys.AddRange((response.S3Objects ?? []).Select(entry => entry.Key));
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        return keys;
    }

    PutObjectRequest NewPut(string key, ReadOnlyMemory<byte> content) => new()
    {
        BucketName = bucket,
        Key = key,
        // The request owns its stream and disposes it when the call ends.
        InputStream = new MemoryStream(content.ToArray()),
    };

    async Task<PutObjectResponse> PutAsync(PutObjectRequest request, CancellationToken cancellationToken)
    {
        using (request.InputStream)
        {
            return await client.PutObjectAsync(request, cancellationToken);
        }
    }

    async Task<WriteOutcome> PutConditionalAsync(PutObjectRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await PutAsync(request, cancellationToken);
            return WriteOutcome.Success(response.ETag);
        }
        catch (AmazonS3Exception error) when (ConditionNotMet(error, request))
        {
            return WriteOutcome.ConditionNotMet;
        }
    }

    // 412 is the failed condition. A concurrent conditional write to the same key is answered with
    // 409 ConditionalRequestConflict: somebody else is writing it, which for a lock is the same thing.
    // Replacing "if unchanged" an object that no longer exists (404) means it changed.
    static bool ConditionNotMet(AmazonS3Exception error, PutObjectRequest request) =>
        error.StatusCode == HttpStatusCode.PreconditionFailed
        || (error.StatusCode == HttpStatusCode.Conflict && error.ErrorCode == "ConditionalRequestConflict")
        || (error.StatusCode == HttpStatusCode.NotFound && request.IfMatch is not null);
}
