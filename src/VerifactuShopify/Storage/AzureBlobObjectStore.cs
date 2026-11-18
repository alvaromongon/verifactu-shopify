using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace VerifactuShopify.Storage;

/// <summary>Where the container is. Credentials are not here: they come from the SDK's standard chain.</summary>
/// <param name="ServiceUri">Endpoint of the storage account (<c>https://{account}.blob.core.windows.net</c>), or of an emulator.</param>
/// <param name="Container">Container name.</param>
public sealed record AzureBlobObjectStoreOptions(Uri ServiceUri, string Container);

/// <summary>
/// <see cref="IObjectStore"/> on Azure Blob Storage. The conditions travel as the standard
/// <c>If-None-Match</c> and <c>If-Match</c> headers; no blob lease is ever taken, so a lock expires by its
/// own content and not by the service.
/// </summary>
/// <remarks>
/// Conditional writes go through a second container client that does not retry; the constructor is private
/// so the only way in is <see cref="Create(AzureBlobObjectStoreOptions, TokenCredential?)"/> and that client
/// cannot be left out. When a write succeeded but its response was lost, a retry would be answered with a
/// failed condition and reported as <see cref="WriteOutcome.ConditionNotMet"/> although this caller wrote
/// the blob. Without retries that case surfaces as an exception, an ambiguous result the caller (the lock)
/// has to resolve by reading the blob back. The clients hold no resources that need disposing.
/// </remarks>
public sealed class AzureBlobObjectStore : IObjectStore
{
    readonly BlobContainerClient _container;
    readonly BlobContainerClient _conditionalContainer;

    AzureBlobObjectStore(BlobContainerClient container, BlobContainerClient conditionalContainer)
    {
        _container = container;
        _conditionalContainer = conditionalContainer;
    }

    /// <summary>
    /// Builds a store. Without <paramref name="credential"/> it uses the SDK's default chain (environment,
    /// managed identity, developer sign-in...), which is what a deployment does.
    /// </summary>
    public static AzureBlobObjectStore Create(AzureBlobObjectStoreOptions options, TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        credential ??= DefaultCredential();
        return Create(options, (uri, clientOptions) => new BlobContainerClient(uri, credential, clientOptions));
    }

    /// <summary>The credential a deployment uses: the SDK's default chain.</summary>
    public static TokenCredential DefaultCredential() => new DefaultAzureCredential();

    /// <summary>Builds a store that signs with an account key: for emulators and tests, never from configuration.</summary>
    public static AzureBlobObjectStore Create(AzureBlobObjectStoreOptions options, StorageSharedKeyCredential credential)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);

        return Create(options, (uri, clientOptions) => new BlobContainerClient(uri, credential, clientOptions));
    }

    static AzureBlobObjectStore Create(AzureBlobObjectStoreOptions options, Func<Uri, BlobClientOptions, BlobContainerClient> newClient)
    {
        var uri = new BlobUriBuilder(options.ServiceUri) { BlobContainerName = options.Container }.ToUri();
        var conditionalOptions = new BlobClientOptions();
        conditionalOptions.Retry.MaxRetries = 0;

        return new AzureBlobObjectStore(newClient(uri, new BlobClientOptions()), newClient(uri, conditionalOptions));
    }

    public async Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _container.GetBlobClient(key).DownloadContentAsync(cancellationToken);
            return new StoredObject(response.Value.Content.ToMemory(), response.Value.Details.ETag.ToString());
        }
        catch (RequestFailedException error) when (error.Status == 404 && error.ErrorCode != BlobErrorCode.ContainerNotFound)
        {
            // A missing blob. Any 404 counts except a missing container (ContainerNotFound), a
            // configuration error that must surface.
            return null;
        }
    }

    public async Task<string> PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var response = await _container.GetBlobClient(key).UploadAsync(BinaryData.FromBytes(content), overwrite: true, cancellationToken);
        return response.Value.ETag.ToString();
    }

    public Task<WriteOutcome> PutIfAbsentAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        PutConditionalAsync(key, content, new BlobRequestConditions { IfNoneMatch = ETag.All }, cancellationToken);

    public Task<WriteOutcome> PutIfUnchangedAsync(string key, ReadOnlyMemory<byte> content, string eTag, CancellationToken cancellationToken = default)
    {
        ETagCondition.Require(eTag);
        return PutConditionalAsync(key, content, new BlobRequestConditions { IfMatch = new ETag(eTag) }, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        // Not DeleteIfExists: it answers false for a missing container too, hiding a configuration error.
        try
        {
            await _container.GetBlobClient(key).DeleteAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException error) when (error.Status == 404 && error.ErrorCode == BlobErrorCode.BlobNotFound)
        {
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var keys = new List<string>();
        await foreach (var blob in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken))
        {
            keys.Add(blob.Name);
        }

        // The service lists in UTF-8 binary order; sort the same way as the other implementations, by
        // UTF-16 code unit: it differs only for characters outside the Basic Multilingual Plane, and
        // callers must not depend on that difference.
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    async Task<WriteOutcome> PutConditionalAsync(string key, ReadOnlyMemory<byte> content, BlobRequestConditions conditions, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _conditionalContainer.GetBlobClient(key).UploadAsync(
                BinaryData.FromBytes(content), new BlobUploadOptions { Conditions = conditions }, cancellationToken);
            return WriteOutcome.Success(response.Value.ETag.ToString());
        }
        catch (RequestFailedException error) when (ConditionNotMet(error, conditions))
        {
            return WriteOutcome.ConditionNotMet;
        }
    }

    // 412 is the failed condition. Creating "if absent" a blob that exists is answered with 409
    // BlobAlreadyExists. Replacing "if unchanged" a blob that no longer exists (404 BlobNotFound) means it
    // changed; any other 404 (ContainerNotFound) is a configuration error.
    static bool ConditionNotMet(RequestFailedException error, BlobRequestConditions conditions) =>
        error.Status == 412
        || (error.Status == 409 && error.ErrorCode == BlobErrorCode.BlobAlreadyExists && conditions.IfNoneMatch is not null)
        || (error.Status == 404 && error.ErrorCode == BlobErrorCode.BlobNotFound && conditions.IfMatch is not null);
}
