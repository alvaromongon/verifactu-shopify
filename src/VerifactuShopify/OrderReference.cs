using System.Text.RegularExpressions;

namespace VerifactuShopify;

// Ties a registro to the Shopify order it came from, through the start of DescripcionOperacion:
// "Pedido #1001 (5812345678901): ...". The AEAT returns that description when queried, so the
// AEAT alone tells which orders were already invoiced and the deployment keeps no state (#4).
// RefExterna would be the natural field, but VeriFactu 1.0.66 always overwrites it with its own
// blockchain link id (mdiago/VeriFactu#294).
public static partial class OrderReference
{
    public static string Describe(ShopifyOrder order) => $"Pedido {order.Name} ({order.Id})";

    // Only the id counts: the order name can be reformatted by the shop.
    public static string? FindOrderId(string? description) =>
        description is not null && Pattern().Match(description) is { Success: true } match
            ? match.Groups["id"].Value
            : null;

    [GeneratedRegex(@"^Pedido .*? \((?<id>\d+)\)(:|$)")]
    private static partial Regex Pattern();
}
