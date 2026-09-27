using VerifactuShopify.ComponentTests.TestDoubles;
using VerifactuShopify.Shopify;

namespace VerifactuShopify.ComponentTests.Shopify;

// The GraphQL Admin API client against the Shopify stub.
public sealed class ShopifyClientTests : IDisposable
{
    readonly ShopifyStub _shopify = new();
    readonly HttpClient _http;

    public ShopifyClientTests() => _http = _shopify.CreateClient();

    Task<ShopifyClient> ConnectAsync() => ShopifyClient.ConnectAsync(_http, ShopifyStub.Shop, "client-id", "client-secret");

    [Fact]
    public async Task Every_page_of_orders_is_read()
    {
        _shopify.GivenPagedPaidOrders("1", "2", DateTimeOffset.Now);
        var client = await ConnectAsync();

        var statuses = await client.GetOrderStatusesAsync("financial_status:paid");

        Assert.Equal(["1", "2"], statuses.Select(status => status.Id));
    }

    [Fact]
    public async Task Graphql_errors_are_reported()
    {
        _shopify.GivenGraphqlErrors("OrderStatuses", new[] { new { message = "Field foo does not exist on type Order" } });
        var client = await ConnectAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetOrderStatusesAsync("financial_status:paid"));

        Assert.Contains("Field foo does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unavailable_api_is_reported_with_its_status()
    {
        _shopify.GivenGraphqlStatus(503);
        var client = await ConnectAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetOrderAsync("1"));

        Assert.Contains("503", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_order_is_reported()
    {
        _shopify.GivenMissingOrder("1");
        var client = await ConnectAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetOrderAsync("1"));

        Assert.Contains("1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_credentials_are_reported()
    {
        _shopify.GivenTokenStatus(401);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(ConnectAsync);

        Assert.Contains("401", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _http.Dispose();
        _shopify.Dispose();
    }
}
