using System.Globalization;

namespace VerifactuShopify.ComponentTests.TestDoubles;

// Shape of a real order returned by ShopifyClient's order query: one product at 10% and a paid
// shipping line at 21%.
public static class Orders
{
    public static string Json(string id, string name, decimal price = 65.6m) => $$$$"""
        {"legacyResourceId":"{{{{id}}}}","name":"{{{{name}}}}","taxesIncluded":true,
         "lineItems":{"pageInfo":{"hasNextPage":false},"nodes":[
           {"title":"Jamón de bellota 100% ibérico","quantity":1,"currentQuantity":1,
            "originalUnitPriceSet":{"shopMoney":{"amount":"{{{{price.ToString(CultureInfo.InvariantCulture)}}}}"}},"discountAllocations":[],
            "taxLines":[{"ratePercentage":10,"priceSet":{"shopMoney":{"amount":"{{{{Math.Round(price - (price / 1.1m), 2).ToString(CultureInfo.InvariantCulture)}}}}"}}}]}]},
         "shippingLines":{"nodes":[
           {"title":"Tarifa fija","originalPriceSet":{"shopMoney":{"amount":"11.9"}},
            "discountAllocations":[],"taxLines":[{"ratePercentage":21,"priceSet":{"shopMoney":{"amount":"2.07"}}}]}]}}
        """;

    // An order whose line quantity changed after checkout, which the parser refuses to invoice.
    public static string Edited(string id, string name) =>
        Json(id, name).Replace("\"currentQuantity\":1", "\"currentQuantity\":0", StringComparison.Ordinal);
}
