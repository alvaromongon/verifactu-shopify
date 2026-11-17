using Azure;
using Azure.Storage;

using VerifactuShopify.ComponentTests.TestDoubles;
using VerifactuShopify.Storage;

namespace VerifactuShopify.ComponentTests.Storage;

// The contract against the Blob implementation pointed at the Blob stub, plus what only an HTTP
// implementation has: the headers it sends and how it reads the errors.
// The store is built the way production builds it (Create), so the contract also covers its real
// configuration: a client that does not retry conditional writes.
public sealed class AzureBlobObjectStoreTests : ObjectStoreContractTests, IDisposable
{
    readonly AzureBlobStub _blob = new();
    readonly AzureBlobObjectStore _store;

    public AzureBlobObjectStoreTests() => _store = NewStore();

    protected override IObjectStore Store => _store;

    public void Dispose() => _blob.Dispose();

    // The bearer-token credential of production is refused by the SDK over plain HTTP, so the tests sign
    // with a shared key: only the credential differs, the rest of the construction is the same.
    AzureBlobObjectStore NewStore() =>
        AzureBlobObjectStore.Create(
            new AzureBlobObjectStoreOptions(_blob.ServiceUri, AzureBlobStub.Container),
            new StorageSharedKeyCredential(AzureBlobStub.Account, Convert.ToBase64String("test-key"u8)));

    [Fact]
    public async Task Create_if_absent_sends_if_none_match_star()
    {
        await Store.PutIfAbsentAsync("lock", Bytes("owner"), Ct);

        var put = Assert.Single(_blob.Requests, request => request.Method == "PUT");
        Assert.Equal("*", put.Headers["If-None-Match"]);
        Assert.DoesNotContain("If-Match", put.Headers.Keys);
    }

    [Fact]
    public async Task Replace_if_unchanged_sends_the_etag_in_if_match()
    {
        var eTag = await Store.PutAsync("lock", Bytes("v1"), Ct);

        await Store.PutIfUnchangedAsync("lock", Bytes("v2"), eTag, Ct);

        var put = _blob.Requests.Last(request => request.Method == "PUT");
        Assert.Equal(eTag, put.Headers["If-Match"]);
        Assert.DoesNotContain("If-None-Match", put.Headers.Keys);
    }

    [Fact]
    public async Task A_plain_write_sends_no_condition()
    {
        await Store.PutAsync("one.txt", Bytes("x"), Ct);

        var put = Assert.Single(_blob.Requests, request => request.Method == "PUT");
        Assert.DoesNotContain("If-Match", put.Headers.Keys);
        Assert.DoesNotContain("If-None-Match", put.Headers.Keys);
    }

    [Fact]
    public async Task No_lease_is_ever_taken()
    {
        await Store.PutIfAbsentAsync("lock", Bytes("owner"), Ct);
        await Store.PutIfUnchangedAsync("lock", Bytes("owner"), (await Store.GetAsync("lock", Ct))!.ETag, Ct);

        Assert.All(_blob.Requests, request => Assert.DoesNotContain("x-ms-lease-id", request.Headers.Keys));
        Assert.DoesNotContain(_blob.Requests, request => request.Headers.ContainsKey("x-ms-lease-action"));
    }

    [Fact]
    public async Task Writing_over_an_existing_blob_is_a_condition_not_met_and_reaches_the_service()
    {
        await Store.PutAsync("lock", Bytes("v1"), Ct);

        var outcome = await Store.PutIfAbsentAsync("lock", Bytes("v2"), Ct);

        Assert.Equal(WriteOutcome.ConditionNotMet, outcome);
        Assert.Equal(2, _blob.Requests.Count(request => request.Method == "PUT"));
    }

    [Fact]
    public async Task A_412_on_create_if_absent_is_also_a_condition_not_met()
    {
        _blob.FailWith(412, "ConditionNotMet");

        Assert.Equal(WriteOutcome.ConditionNotMet, await Store.PutIfAbsentAsync("lock", Bytes("v"), Ct));
    }

    [Fact]
    public async Task A_missing_blob_on_a_conditional_replace_is_a_condition_not_met()
    {
        _blob.FailWith(404, "BlobNotFound");

        Assert.Equal(WriteOutcome.ConditionNotMet, await Store.PutIfUnchangedAsync("lock", Bytes("v"), "\"abc\"", Ct));
    }

    [Fact]
    public async Task An_outage_is_an_error_not_a_condition_not_met()
    {
        _blob.FailWith(503, "ServerBusy");

        await Assert.ThrowsAsync<RequestFailedException>(() => Store.PutIfAbsentAsync("lock", Bytes("v"), Ct));
    }

    [Fact]
    public async Task Refused_credentials_are_an_error_not_a_condition_not_met()
    {
        _blob.FailWith(403, "AuthorizationFailure");

        var error = await Assert.ThrowsAsync<RequestFailedException>(() => Store.PutIfUnchangedAsync("lock", Bytes("v"), "\"abc\"", Ct));

        Assert.Equal("AuthorizationFailure", error.ErrorCode);
    }

    [Fact]
    public async Task Refused_credentials_on_a_create_if_absent_are_an_error_not_a_condition_not_met()
    {
        _blob.FailWith(403, "AuthorizationFailure");

        await Assert.ThrowsAsync<RequestFailedException>(() => Store.PutIfAbsentAsync("lock", Bytes("v"), Ct));
    }

    [Fact]
    public async Task A_missing_container_on_a_read_is_an_error_not_a_missing_object()
    {
        _blob.FailWith(404, "ContainerNotFound");

        await Assert.ThrowsAsync<RequestFailedException>(() => Store.GetAsync("one.txt", Ct));
    }

    [Fact]
    public async Task A_missing_container_on_a_conditional_write_is_an_error_not_a_condition_not_met()
    {
        _blob.FailWith(404, "ContainerNotFound");

        var error = await Assert.ThrowsAsync<RequestFailedException>(() => Store.PutIfUnchangedAsync("lock", Bytes("v"), "\"abc\"", Ct));

        Assert.Equal("ContainerNotFound", error.ErrorCode);
    }

    [Fact]
    public async Task A_conditional_write_is_not_retried_by_the_sdk_so_a_lost_response_is_never_replayed()
    {
        _blob.FailWith(503, "ServerBusy");

        await Assert.ThrowsAsync<RequestFailedException>(() => Store.PutIfAbsentAsync("lock", Bytes("v"), Ct));
        await Assert.ThrowsAsync<RequestFailedException>(() => Store.PutIfUnchangedAsync("lock", Bytes("v"), "\"abc\"", Ct));

        Assert.Equal(2, _blob.Requests.Count(request => request.Method == "PUT"));
    }

    [Fact]
    public async Task Blobs_are_addressed_by_container_and_name_under_the_account_endpoint()
    {
        await Store.PutAsync("a/one.txt", Bytes("x"), Ct);

        var put = Assert.Single(_blob.Requests, request => request.Method == "PUT");
        Assert.Equal($"/{AzureBlobStub.Account}/{AzureBlobStub.Container}/a/one.txt", put.Path);
        Assert.Equal("BlockBlob", put.Headers["x-ms-blob-type"]);
    }

    [Fact]
    public async Task A_listing_is_returned_in_ordinal_order_whatever_the_provider_sends()
    {
        await Store.PutAsync("b.txt", Bytes("x"), Ct);
        await Store.PutAsync("a.txt", Bytes("x"), Ct);
        _blob.ListInReverseOrder();

        var keys = await Store.ListAsync(string.Empty, Ct);

        Assert.Equal(["a.txt", "b.txt"], keys);
    }

    [Fact]
    public void Options_are_required()
    {
        Assert.Throws<ArgumentNullException>(() => AzureBlobObjectStore.Create(null!));
    }

    [Fact]
    public void The_production_credential_is_the_sdk_default_chain()
    {
        var store = AzureBlobObjectStore.Create(new AzureBlobObjectStoreOptions(new Uri("https://account.blob.core.windows.net"), AzureBlobStub.Container));

        Assert.NotNull(store);
    }
}
