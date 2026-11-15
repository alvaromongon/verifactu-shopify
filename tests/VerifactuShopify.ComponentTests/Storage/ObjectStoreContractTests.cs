using System.Text;

using VerifactuShopify.Storage;

namespace VerifactuShopify.ComponentTests.Storage;

// The behaviour every IObjectStore must have. Each implementation inherits these tests, so the
// in-memory double and the real one cannot drift apart.
public abstract class ObjectStoreContractTests
{
    protected abstract IObjectStore Store { get; }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ReadOnlyMemory<byte> Bytes(string text) => Encoding.UTF8.GetBytes(text);

    static string Text(StoredObject? stored) => Encoding.UTF8.GetString(stored!.Content.Span);

    [Fact]
    public async Task A_missing_object_reads_as_null()
    {
        Assert.Null(await Store.GetAsync("missing.txt", Ct));
    }

    [Fact]
    public async Task A_written_object_is_read_back_with_its_etag()
    {
        var eTag = await Store.PutAsync("a/one.txt", Bytes("hello"), Ct);

        var stored = await Store.GetAsync("a/one.txt", Ct);

        Assert.Equal("hello", Text(stored));
        Assert.Equal(eTag, stored!.ETag);
    }

    [Fact]
    public async Task Writing_again_replaces_the_object_and_changes_its_etag()
    {
        var first = await Store.PutAsync("one.txt", Bytes("v1"), Ct);

        var second = await Store.PutAsync("one.txt", Bytes("v2"), Ct);

        Assert.NotEqual(first, second);
        Assert.Equal("v2", Text(await Store.GetAsync("one.txt", Ct)));
    }

    [Fact]
    public async Task Create_if_absent_writes_a_new_object()
    {
        var outcome = await Store.PutIfAbsentAsync("lock", Bytes("owner-1"), Ct);

        Assert.True(outcome.Written);
        Assert.Equal((await Store.GetAsync("lock", Ct))!.ETag, outcome.ETag);
    }

    [Fact]
    public async Task Create_if_absent_does_not_touch_an_existing_object()
    {
        await Store.PutAsync("lock", Bytes("owner-1"), Ct);

        var outcome = await Store.PutIfAbsentAsync("lock", Bytes("owner-2"), Ct);

        Assert.False(outcome.Written);
        Assert.Null(outcome.ETag);
        Assert.Equal("owner-1", Text(await Store.GetAsync("lock", Ct)));
    }

    [Fact]
    public async Task Replace_if_unchanged_writes_when_the_etag_matches()
    {
        var eTag = await Store.PutAsync("lock", Bytes("v1"), Ct);

        var outcome = await Store.PutIfUnchangedAsync("lock", Bytes("v2"), eTag, Ct);

        Assert.True(outcome.Written);
        Assert.NotEqual(eTag, outcome.ETag);
        Assert.Equal("v2", Text(await Store.GetAsync("lock", Ct)));
    }

    [Fact]
    public async Task Replace_if_unchanged_does_not_write_when_the_object_changed()
    {
        var stale = await Store.PutAsync("lock", Bytes("v1"), Ct);
        await Store.PutAsync("lock", Bytes("v2"), Ct);

        var outcome = await Store.PutIfUnchangedAsync("lock", Bytes("v3"), stale, Ct);

        Assert.False(outcome.Written);
        Assert.Equal("v2", Text(await Store.GetAsync("lock", Ct)));
    }

    [Fact]
    public async Task Replace_if_unchanged_does_not_create_a_missing_object()
    {
        var outcome = await Store.PutIfUnchangedAsync("lock", Bytes("v1"), "\"0123456789abcdef0123456789abcdef\"", Ct);

        Assert.False(outcome.Written);
        Assert.Null(await Store.GetAsync("lock", Ct));
    }

    [Fact]
    public async Task A_deleted_object_is_gone_and_deleting_a_missing_one_is_fine()
    {
        await Store.PutAsync("one.txt", Bytes("x"), Ct);

        await Store.DeleteAsync("one.txt", Ct);
        await Store.DeleteAsync("one.txt", Ct);

        Assert.Null(await Store.GetAsync("one.txt", Ct));
    }

    [Fact]
    public async Task Listing_returns_only_the_keys_under_the_prefix()
    {
        foreach (var key in new[] { "chain/b", "chain/a", "inbox/c", "chain/sub/d" })
        {
            await Store.PutAsync(key, Bytes(key), Ct);
        }

        var keys = await Store.ListAsync("chain/", Ct);

        Assert.Equal(["chain/a", "chain/b", "chain/sub/d"], keys);
    }

    [Fact]
    public async Task Listing_follows_every_page()
    {
        var expected = Enumerable.Range(0, 25).Select(i => $"many/{i:D2}").ToArray();
        foreach (var key in expected)
        {
            await Store.PutAsync(key, Bytes("x"), Ct);
        }

        Assert.Equal(expected, await Store.ListAsync("many/", Ct));
    }

    [Fact]
    public async Task Keys_with_spaces_and_non_ascii_characters_round_trip()
    {
        const string key = "facturas/2026/factura nº 1 áé.xml";
        await Store.PutAsync(key, Bytes("<x/>"), Ct);

        Assert.Equal("<x/>", Text(await Store.GetAsync(key, Ct)));
        Assert.Equal([key], await Store.ListAsync("facturas/", Ct));
    }
}
