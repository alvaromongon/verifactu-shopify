using Amazon.Runtime;
using Amazon.S3;

using VerifactuShopify.ComponentTests.TestDoubles;
using VerifactuShopify.Storage;

namespace VerifactuShopify.ComponentTests.Storage;

// The contract against the S3 implementation pointed at the S3 stub, plus what only an HTTP
// implementation has: the headers it sends and how it reads the errors.
// The store is built the way production builds it (Create), so the contract also covers its real
// configuration: checksums only when required and a client that does not retry conditional writes.
public sealed class S3ObjectStoreTests : ObjectStoreContractTests, IDisposable
{
    readonly S3Stub _s3 = new();
    readonly S3ObjectStore _store;

    public S3ObjectStoreTests() =>
        _store = NewStore(new S3ObjectStoreOptions(S3Stub.Bucket, ServiceUrl: _s3.Url, ForcePathStyle: true));

    protected override IObjectStore Store => _store;

    public void Dispose()
    {
        _store.Dispose();
        _s3.Dispose();
    }

    static S3ObjectStore NewStore(S3ObjectStoreOptions options) =>
        S3ObjectStore.Create(options, new BasicAWSCredentials("test-access-key", "test-secret-key"));

    [Fact]
    public async Task Create_if_absent_sends_if_none_match_star()
    {
        await Store.PutIfAbsentAsync("lock", Bytes("owner"), Ct);

        var put = Assert.Single(_s3.Requests, request => request.Method == "PUT");
        Assert.Equal("*", put.Headers["If-None-Match"]);
        Assert.DoesNotContain("If-Match", put.Headers.Keys);
    }

    [Fact]
    public async Task Replace_if_unchanged_sends_the_etag_in_if_match()
    {
        var eTag = await Store.PutAsync("lock", Bytes("v1"), Ct);

        await Store.PutIfUnchangedAsync("lock", Bytes("v2"), eTag, Ct);

        var put = _s3.Requests.Last(request => request.Method == "PUT");
        Assert.Equal(eTag, put.Headers["If-Match"]);
        Assert.DoesNotContain("If-None-Match", put.Headers.Keys);
    }

    [Fact]
    public async Task A_plain_write_sends_no_condition()
    {
        await Store.PutAsync("one.txt", Bytes("x"), Ct);

        var put = Assert.Single(_s3.Requests, request => request.Method == "PUT");
        Assert.DoesNotContain("If-Match", put.Headers.Keys);
        Assert.DoesNotContain("If-None-Match", put.Headers.Keys);
    }

    [Fact]
    public async Task A_412_is_reported_as_a_condition_not_met()
    {
        await Store.PutAsync("lock", Bytes("v1"), Ct);

        var outcome = await Store.PutIfAbsentAsync("lock", Bytes("v2"), Ct);

        Assert.Equal(WriteOutcome.ConditionNotMet, outcome);
        Assert.Contains(_s3.Requests, request => request.Method == "PUT");
    }

    [Fact]
    public async Task An_outage_is_an_error_not_a_condition_not_met()
    {
        _s3.FailWith(503, "ServiceUnavailable");

        await Assert.ThrowsAsync<AmazonS3Exception>(() => Store.PutIfAbsentAsync("lock", Bytes("v"), Ct));
    }

    [Fact]
    public async Task Refused_credentials_are_an_error_not_a_condition_not_met()
    {
        _s3.FailWith(403, "AccessDenied");

        var error = await Assert.ThrowsAsync<AmazonS3Exception>(() => Store.PutIfUnchangedAsync("lock", Bytes("v"), "\"abc\"", Ct));

        Assert.Equal("AccessDenied", error.ErrorCode);
    }

    [Fact]
    public async Task A_read_during_an_outage_is_an_error_not_a_missing_object()
    {
        _s3.FailWith(500, "InternalError");

        await Assert.ThrowsAsync<AmazonS3Exception>(() => Store.GetAsync("one.txt", Ct));
    }

    [Fact]
    public async Task A_store_built_from_options_talks_to_a_compatible_endpoint_in_path_style_without_checksum_trailers()
    {
        await Store.PutAsync("one.txt", Bytes("x"), Ct);

        var put = Assert.Single(_s3.Requests, request => request.Method == "PUT");
        Assert.Equal($"/{S3Stub.Bucket}/one.txt", put.Path);
        Assert.DoesNotContain("x-amz-trailer", put.Headers.Keys);
        Assert.DoesNotContain("x-amz-sdk-checksum-algorithm", put.Headers.Keys);
    }

    [Fact]
    public async Task A_missing_bucket_on_a_read_is_an_error_not_a_missing_object()
    {
        _s3.FailWith(404, "NoSuchBucket");

        await Assert.ThrowsAsync<AmazonS3Exception>(() => Store.GetAsync("one.txt", Ct));
    }

    [Theory]
    [InlineData("NotFound")]
    [InlineData("")]
    public async Task A_404_with_another_code_or_no_body_reads_as_a_missing_object(string code)
    {
        _s3.FailWith(404, code);

        Assert.Null(await Store.GetAsync("one.txt", Ct));
    }

    [Fact]
    public async Task A_missing_bucket_on_a_conditional_write_is_an_error_not_a_condition_not_met()
    {
        _s3.FailWith(404, "NoSuchBucket");

        var error = await Assert.ThrowsAsync<AmazonS3Exception>(() => Store.PutIfUnchangedAsync("lock", Bytes("v"), "\"abc\"", Ct));

        Assert.Equal("NoSuchBucket", error.ErrorCode);
    }

    [Fact]
    public async Task A_conditional_write_is_not_retried_by_the_sdk_so_a_lost_response_is_never_replayed()
    {
        _s3.FailWith(503, "ServiceUnavailable");

        await Assert.ThrowsAsync<AmazonS3Exception>(() => Store.PutIfAbsentAsync("lock", Bytes("v"), Ct));

        Assert.Single(_s3.Requests, request => request.Method == "PUT");
    }

    [Fact]
    public async Task An_empty_region_is_treated_as_unset()
    {
        using var store = NewStore(new S3ObjectStoreOptions(S3Stub.Bucket, ServiceUrl: _s3.Url, Region: "", ForcePathStyle: true));

        await store.PutAsync("one.txt", Bytes("x"), Ct);

        var put = Assert.Single(_s3.Requests, request => request.Method == "PUT");
        Assert.Contains("/us-east-1/s3/", put.Headers["Authorization"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_listing_is_returned_in_ordinal_order_whatever_the_provider_sends()
    {
        await Store.PutAsync("b.txt", Bytes("x"), Ct);
        await Store.PutAsync("a.txt", Bytes("x"), Ct);
        _s3.ListInReverseOrder();

        var keys = await Store.ListAsync(string.Empty, Ct);

        Assert.Equal(["a.txt", "b.txt"], keys);
    }

    [Fact]
    public async Task A_truncated_listing_without_a_continuation_token_is_an_error()
    {
        await Store.PutAsync("a.txt", Bytes("x"), Ct);
        _s3.ListTruncatedWithoutToken();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ListAsync(string.Empty, Ct));
    }

    [Fact]
    public async Task Disposing_the_store_releases_both_clients()
    {
        using var store = NewStore(new S3ObjectStoreOptions(S3Stub.Bucket, ServiceUrl: _s3.Url, ForcePathStyle: true));

        store.Dispose();
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.PutAsync("one.txt", Bytes("x"), Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.PutIfAbsentAsync("lock", Bytes("x"), Ct));
    }

    [Fact]
    public void Options_are_required()
    {
        Assert.Throws<ArgumentNullException>(() => S3ObjectStore.Create(null!));
    }
}
