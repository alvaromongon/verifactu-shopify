using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VerifactuShopify;

// Minimal client for the GraphQL Admin API: just the two reads the sync needs, over plain
// HttpClient so no SDK sits between the connector and the API.
public sealed class ShopifyClient
{
    // Pinned so a new Shopify release can't change a response under the connector.
    public const string ApiVersion = "2026-07";

    public const string RequiredScope = "read_orders";

    const int MaxThrottledRetries = 3;

    // Cheap enough to run over every order in the window on every run: no lines. Sorted by
    // PROCESSED_AT just so the output reads in order; the sync sorts by payment time anyway.
    const string OrderStatusesQuery = """
        query OrderStatuses($query: String!, $after: String) {
          orders(first: 50, after: $after, query: $query, sortKey: PROCESSED_AT) {
            pageInfo { hasNextPage endCursor }
            nodes {
              legacyResourceId
              name
              test
              cancelledAt
              displayFinancialStatus
              transactions(first: 20) { kind status processedAt }
            }
          }
        }
        """;

    // Only for the orders about to be invoiced. Nothing about the customer: every order is an F2
    // for now (#3), so the app needs no protected customer data beyond the order itself.
    const string OrderQuery = """
        query Order($id: ID!) {
          order(id: $id) {
            legacyResourceId
            name
            taxesIncluded
            lineItems(first: 50) {
              pageInfo { hasNextPage }
              nodes {
                title
                quantity
                currentQuantity
                originalUnitPriceSet { shopMoney { amount } }
                discountAllocations { allocatedAmountSet { shopMoney { amount } } }
                taxLines { ratePercentage priceSet { shopMoney { amount } } }
              }
            }
            shippingLines(first: 5) {
              nodes {
                title
                originalPriceSet { shopMoney { amount } }
                discountAllocations { allocatedAmountSet { shopMoney { amount } } }
                taxLines { ratePercentage priceSet { shopMoney { amount } } }
              }
            }
          }
        }
        """;

    readonly HttpClient _http;
    readonly Uri _graphqlEndpoint;
    readonly string _accessToken;

    ShopifyClient(HttpClient http, string shopDomain, string accessToken)
    {
        _http = http;
        _graphqlEndpoint = new Uri($"https://{shopDomain}/admin/api/{ApiVersion}/graphql.json");
        _accessToken = accessToken;
    }

    // Client credentials grant: the app and the shop are in the same Dev Dashboard organization,
    // so the app trades its own ID and secret for a 24-hour token. A fresh one each run, never
    // stored: nothing to keep between runs (see #4).
    public static async Task<ShopifyClient> ConnectAsync(HttpClient http, string shopDomain, string clientId, string clientSecret)
    {
        using var response = await http.PostAsync(
            new Uri($"https://{shopDomain}/admin/oauth/access_token"),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            }));

        // A 404 is what an unknown shop gets, and the domain people know is usually the public one.
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"Shopify no encuentra la tienda {shopDomain}. Tiene que ser su dominio *.myshopify.com, " +
                "no el público: está en el admin de la tienda, en Configuración > Dominios.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Shopify no dio un token para {shopDomain} ({(int)response.StatusCode}). " +
                "Revisa el client ID y el secreto, y que la app esté instalada en la tienda y sea de su misma organización.");

        var token = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The token only carries the scopes of the released app version the shop approved; without
        // this check a missing scope surfaces later as a bare "Access denied for orders field".
        var scopes = token.GetProperty("scope").GetString()?.Split(',') ?? [];
        if (!scopes.Contains(RequiredScope))
            throw new InvalidOperationException(
                $"La app no tiene el permiso {RequiredScope} en {shopDomain} (tiene: {(scopes is [""] or [] ? "ninguno" : string.Join(", ", scopes))}). " +
                "Añádelo a la versión de la app en el Dev Dashboard, publícala y apruébalo en la tienda.");

        return new ShopifyClient(http, shopDomain, token.GetProperty("access_token").GetString()!);
    }

    // searchQuery uses Shopify's order search syntax, e.g. "financial_status:paid updated_at:>=...".
    public async Task<List<ShopifyOrderStatus>> GetOrderStatusesAsync(string searchQuery)
    {
        var statuses = new List<ShopifyOrderStatus>();
        string? after = null;
        do
        {
            var orders = (await QueryAsync(OrderStatusesQuery, new { query = searchQuery, after })).GetProperty("orders");
            statuses.AddRange(orders.GetProperty("nodes").EnumerateArray().Select(ShopifyOrderParser.ParseStatus));

            var pageInfo = orders.GetProperty("pageInfo");
            after = pageInfo.GetProperty("hasNextPage").GetBoolean() ? pageInfo.GetProperty("endCursor").GetString() : null;
        }
        while (after is not null);

        return statuses;
    }

    public async Task<JsonElement> GetOrderAsync(string legacyResourceId)
    {
        var data = await QueryAsync(OrderQuery, new { id = $"gid://shopify/Order/{legacyResourceId}" });
        var order = data.GetProperty("order");
        return order.ValueKind != JsonValueKind.Null
            ? order
            : throw new InvalidOperationException($"Shopify no devuelve el pedido {legacyResourceId}.");
    }

    async Task<JsonElement> QueryAsync(string query, object variables)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _graphqlEndpoint)
            {
                Content = JsonContent.Create(new { query, variables }),
            };
            request.Headers.Add("X-Shopify-Access-Token", _accessToken);

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"La API de Shopify respondió {(int)response.StatusCode} {response.ReasonPhrase}.");

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!body.TryGetProperty("errors", out var errors))
                return body.GetProperty("data");

            // The query cost bucket refills within seconds; anything else is a bug in the query.
            if (IsThrottled(errors) && attempt < MaxThrottledRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt));
                continue;
            }

            throw new InvalidOperationException($"La API de Shopify devolvió errores: {errors}");
        }
    }

    static bool IsThrottled(JsonElement errors) =>
        errors.ValueKind == JsonValueKind.Array && errors.EnumerateArray().Any(error =>
            error.TryGetProperty("extensions", out var extensions) &&
            extensions.TryGetProperty("code", out var code) &&
            code.GetString() == "THROTTLED");
}
