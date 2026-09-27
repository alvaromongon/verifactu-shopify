using System.Text.Json;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace VerifactuShopify.ComponentTests.TestDoubles;

// Shopify Admin API: the client credentials token and the two GraphQL queries the sync runs.
public sealed class ShopifyStub : IDisposable
{
    public const string Shop = "tienda-de-prueba.myshopify.com";

    // A JSON null in the anonymous response objects, which need a typed value.
    const string? JsonNull = null;

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TimeSpan _latency;

    // latency: added to every response, to measure a run as it would go against the real API.
    public ShopifyStub(TimeSpan latency = default)
    {
        _latency = latency;
        GivenToken("read_orders");
    }

    public HttpClient CreateClient() => new(new RedirectToStubHandler(new Uri(_server.Url!)));

    public ShopifyStub GivenToken(string scope)
    {
        _server.Given(Request.Create().WithPath("/admin/oauth/access_token").UsingPost())
            .RespondWith(Json(new { access_token = "shpat_test", scope }));
        return this;
    }

    public ShopifyStub GivenTokenStatus(int status)
    {
        _server.Given(Request.Create().WithPath("/admin/oauth/access_token").UsingPost())
            .RespondWith(Respond().WithStatusCode(status));
        return this;
    }

    // Paid orders returned by the status query: (id, name, paidAt).
    public ShopifyStub GivenPaidOrders(params (string Id, string Name, DateTimeOffset PaidAt)[] orders) =>
        GivenGraphql("OrderStatuses", StatusesPage([.. orders.Select(order => (order.Id, order.PaidAt))], nextCursor: null, orders));

    static object StatusesPage(
        (string Id, DateTimeOffset PaidAt)[] orders, string? nextCursor, (string Id, string Name, DateTimeOffset PaidAt)[]? named = null) => new
        {
            data = new
            {
                orders = new
                {
                    pageInfo = new { hasNextPage = nextCursor is not null, endCursor = nextCursor },
                    nodes = orders.Select((order, i) => new
                    {
                        legacyResourceId = order.Id,
                        name = named?[i].Name ?? $"#{order.Id}",
                        test = false,
                        cancelledAt = JsonNull,
                        displayFinancialStatus = "PAID",
                        transactions = new[] { new { kind = "SALE", status = "SUCCESS", processedAt = order.PaidAt.ToString("O") } },
                    }),
                },
            },
        };

    public ShopifyStub GivenOrder(string id, string orderJson) =>
        GivenGraphql($"gid://shopify/Order/{id}", new { data = new { order = JsonDocument.Parse(orderJson).RootElement } });

    public ShopifyStub GivenGraphqlErrors(string operation, object errors) =>
        GivenGraphql(operation, new { errors });

    public ShopifyStub GivenMissingOrder(string id) =>
        GivenGraphql($"gid://shopify/Order/{id}", new { data = new { order = JsonNull } });

    public ShopifyStub GivenGraphqlStatus(int status)
    {
        _server.Given(Request.Create().WithPath("/admin/api/*/graphql.json").UsingPost())
            .RespondWith(Respond().WithStatusCode(status));
        return this;
    }

    // Paid orders in two pages of the status query.
    public ShopifyStub GivenPagedPaidOrders(string firstId, string secondId, DateTimeOffset paidAt)
    {
        GivenGraphql("OrderStatuses", StatusesPage([(firstId, paidAt)], nextCursor: "cursor-1"));
        return GivenGraphql("cursor-1", StatusesPage([(secondId, paidAt)], nextCursor: null));
    }

    public int Requests(string pathOrBodyFragment) =>
        _server.LogEntries.Count(entry =>
            entry.RequestMessage!.Path.Contains(pathOrBodyFragment, StringComparison.Ordinal) ||
            (entry.RequestMessage.Body ?? "").Contains(pathOrBodyFragment, StringComparison.Ordinal));

    ShopifyStub GivenGraphql(string bodyFragment, object response)
    {
        _server.Given(Request.Create().WithPath($"/admin/api/*/graphql.json").UsingPost()
                .WithBody(body => body?.Contains(bodyFragment, StringComparison.Ordinal) == true))
            .RespondWith(Json(response));
        return this;
    }

    // System.Text.Json, as Shopify's JSON is read with it and the orders are JsonElements.
    IResponseBuilder Json(object body) =>
        Respond().WithHeader("Content-Type", "application/json").WithBody(JsonSerializer.Serialize(body));

    // WireMock rejects a zero delay.
    IResponseBuilder Respond() => _latency > TimeSpan.Zero ? Response.Create().WithDelay(_latency) : Response.Create();

    public void Dispose() => _server.Dispose();
}
