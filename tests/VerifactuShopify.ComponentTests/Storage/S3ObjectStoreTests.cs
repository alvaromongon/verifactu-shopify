using System.Text;

using Amazon.Runtime;
using Amazon.S3;

using VerifactuShopify.ComponentTests.TestDoubles;
using VerifactuShopify.Storage;

namespace VerifactuShopify.ComponentTests.Storage;

// The contract against the S3 implementation pointed at the S3 stub, plus what only an HTTP
// implementation has: the headers it sends and how it reads the errors.
public sealed class S3ObjectStoreTests : ObjectStoreContractTests, IDisposable
{
    readonly S3Stub _s3 = new();
    readonly IObjectStore _store;

    public S3ObjectStoreTests() =>
        _store = new S3ObjectStore(
            new AmazonS3Client(
                new BasicAWSCredentials("test-access-key", "test-secret-key"),
                new AmazonS3Config { ServiceURL = _s3.Url, ForcePathStyle = true, AuthenticationRegion = "eu-west-1" }),
            S3Stub.Bucket);

    protected override IObjectStore Store => _store;

    public void Dispose() => _s3.Dispose();

    static ReadOnlyMemory<byte> Bytes(string text) => Encoding.UTF8.GetBytes(text);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        var options = new S3ObjectStoreOptions(S3Stub.Bucket, ServiceUrl: _s3.Url, ForcePathStyle: true);
        var store = S3ObjectStore.Create(options, new BasicAWSCredentials("test-access-key", "test-secret-key"));

        await store.PutAsync("one.txt", Bytes("x"), Ct);

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
        var store = S3ObjectStore.Create(
            new S3ObjectStoreOptions(S3Stub.Bucket, ServiceUrl: _s3.Url, ForcePathStyle: true),
            new BasicAWSCredentials("test-access-key", "test-secret-key"));
        _s3.FailWith(503, "ServiceUnavailable");

        await Assert.ThrowsAsync<AmazonS3Exception>(() => store.PutIfAbsentAsync("lock", Bytes("v"), Ct));

        Assert.Single(_s3.Requests, request => request.Method == "PUT");
    }

    [Fact]
    public async Task An_empty_region_is_treated_as_unset()
    {
        var options = new S3ObjectStoreOptions(S3Stub.Bucket, ServiceUrl: _s3.Url, Region: "", ForcePathStyle: true);
        var store = S3ObjectStore.Create(options, new BasicAWSCredentials("test-access-key", "test-secret-key"));

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
    public void Options_are_required()
    {
        Assert.Throws<ArgumentNullException>(() => S3ObjectStore.Create(null!));
    }
}
