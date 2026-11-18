using System.Net;
using System.Runtime.InteropServices;

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
/// <remarks>
/// Conditional writes go through a second client that does not retry; the constructor is private so the only
/// way in is <see cref="Create"/> and that client cannot be left out. When a write succeeded but its response
/// was lost, a retry would be answered with 412 and reported as <see cref="WriteOutcome.ConditionNotMet"/>
/// although this caller wrote the object. Without retries that case surfaces as an exception, an ambiguous
/// result the caller (the lock) has to resolve by reading the object back.
/// </remarks>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    readonly IAmazonS3 _client;
    readonly IAmazonS3 _conditionalClient;
    readonly string _bucket;
    readonly Action<string> _log;

    S3ObjectStore(IAmazonS3 client, IAmazonS3 conditionalClient, string bucket, Action<string> log)
    {
        _client = client;
        _conditionalClient = conditionalClient;
        _bucket = bucket;
        _log = log;
    }

    /// <summary>Releases the two clients.</summary>
    public void Dispose()
    {
        _client.Dispose();
        _conditionalClient.Dispose();
    }

    /// <summary>
    /// Builds a store. Without <paramref name="credentials"/> it uses the SDK's default chain
    /// (environment, profile, role...), which is what a deployment does. A read that finds a 404 other than
    /// <c>NoSuchKey</c> is taken as a missing object and reported to <paramref name="log"/> (standard error
    /// when omitted), so a provider answering something else is noticed.
    /// </summary>
    public static S3ObjectStore Create(S3ObjectStoreOptions options, AWSCredentials? credentials = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var config = NewConfig(options);
        var conditionalConfig = NewConfig(options);
        conditionalConfig.MaxErrorRetry = 0;

        return new S3ObjectStore(NewClient(credentials, config), NewClient(credentials, conditionalConfig), options.Bucket, log ?? Console.Error.WriteLine);
    }

    static AmazonS3Client NewClient(AWSCredentials? credentials, AmazonS3Config config) =>
        credentials is null ? new AmazonS3Client(config) : new AmazonS3Client(credentials, config);

    static AmazonS3Config NewConfig(S3ObjectStoreOptions options)
    {
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
            config.AuthenticationRegion = options.Region is { Length: > 0 } signingRegion ? signingRegion : "us-east-1";
        }
        else if (options.Region is { Length: > 0 } region)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        return config;
    }

    public async Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.GetObjectAsync(_bucket, key, cancellationToken);
            using var content = new MemoryStream();
            await response.ResponseStream.CopyToAsync(content, cancellationToken);
            return new StoredObject(content.ToArray(), response.ETag);
        }
        catch (AmazonS3Exception error) when (IsMissingKey(error))
        {
            if (error.ErrorCode != NoSuchKey)
            {
                _log($"S3: la lectura de '{key}' respondió 404 con el código '{error.ErrorCode}' (RequestId {error.RequestId}); se toma como objeto inexistente.");
            }

            return null;
        }
    }

    public async Task<string> PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var response = await PutAsync(_client, NewPut(key, content), cancellationToken);
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
        ETagCondition.Require(eTag);
        var request = NewPut(key, content);
        request.IfMatch = eTag;
        return await PutConditionalAsync(request, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        await _client.DeleteObjectAsync(_bucket, key, cancellationToken);

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, cancellationToken);
            keys.AddRange((response.S3Objects ?? []).Select(entry => entry.Key));
            request.ContinuationToken = response.NextContinuationToken;
            if (response.IsTruncated == true && string.IsNullOrEmpty(response.NextContinuationToken))
            {
                throw new InvalidOperationException("The provider reported a truncated listing without a continuation token.");
            }
        }
        while (response.IsTruncated == true);

        // The service lists in UTF-8 binary order; compatible providers do not all promise it. Sort the same
        // way everywhere, by UTF-16 code unit: it differs from UTF-8 byte order only for characters
        // outside the Basic Multilingual Plane, and callers must not depend on that difference.
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    PutObjectRequest NewPut(string key, ReadOnlyMemory<byte> content) => new()
    {
        BucketName = _bucket,
        Key = key,
        // The request owns its stream and disposes it when the call ends. It reads the caller's buffer in
        // place when it can (a successful TryGetArray always has an array): the content is not modified while
        // the call runs.
        InputStream = MemoryMarshal.TryGetArray(content, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(content.ToArray()),
    };

    static async Task<PutObjectResponse> PutAsync(IAmazonS3 target, PutObjectRequest request, CancellationToken cancellationToken)
    {
        using (request.InputStream)
        {
            return await target.PutObjectAsync(request, cancellationToken);
        }
    }

    async Task<WriteOutcome> PutConditionalAsync(PutObjectRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await PutAsync(_conditionalClient, request, cancellationToken);
            return WriteOutcome.Success(response.ETag);
        }
        catch (AmazonS3Exception error) when (ConditionNotMet(error, request))
        {
            return WriteOutcome.ConditionNotMet;
        }
    }

    // 412 is the failed condition. A concurrent conditional write to the same key is answered with
    // 409 ConditionalRequestConflict: somebody else is writing it, which for a lock is the same thing.
    // Replacing "if unchanged" an object that no longer exists means it changed, so it fails closed as a
    // lost condition whatever 404 code the provider uses for it (see IsMissingKey).
    static bool ConditionNotMet(AmazonS3Exception error, PutObjectRequest request) =>
        error.StatusCode == HttpStatusCode.PreconditionFailed
        || (error.StatusCode == HttpStatusCode.Conflict && error.ErrorCode == "ConditionalRequestConflict")
        || (IsMissingKey(error) && request.IfMatch is not null);

    // A missing key. Compatible providers answer it with other codes, or with no body, so any 404 counts
    // except a missing bucket (NoSuchBucket), a configuration error that must surface.
    static bool IsMissingKey(AmazonS3Exception error) =>
        error.StatusCode == HttpStatusCode.NotFound && error.ErrorCode != "NoSuchBucket";

    const string NoSuchKey = "NoSuchKey";
}
