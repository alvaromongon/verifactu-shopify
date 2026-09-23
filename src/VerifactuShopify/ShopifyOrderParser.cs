using System.Globalization;
using System.Text.Json;

namespace VerifactuShopify;

// Turns the Admin API's JSON for an order into this connector's own types. Kept apart from
// ShopifyClient so it can be tested with real (anonymized) responses and no network. Anything
// the mapper can't be trusted with is rejected here as not invoiceable, never guessed at.
public static class ShopifyOrderParser
{
    // A node of ShopifyClient.OrderStatusesQuery.
    public static ShopifyOrderStatus ParseStatus(JsonElement node) => new(
        Id: node.GetProperty("legacyResourceId").GetString()!,
        Name: node.GetProperty("name").GetString()!,
        FinancialStatus: node.GetProperty("displayFinancialStatus").GetString() ?? "",
        Test: node.GetProperty("test").GetBoolean(),
        CancelledAt: ParseDate(node.GetProperty("cancelledAt")),
        PaidAt: node.GetProperty("transactions").EnumerateArray()
            .Where(t => t.GetProperty("status").GetString() == "SUCCESS" &&
                        t.GetProperty("kind").GetString() is "SALE" or "CAPTURE")
            .Select(t => ParseDate(t.GetProperty("processedAt")))
            .Max());

    // The "order" object of ShopifyClient.OrderQuery. The order is invoiced on invoiceDate, the
    // day it's sent: the chain lookup assumes every registro carries the date it was generated.
    public static ShopifyOrder ParseOrder(JsonElement order, DateTime invoiceDate)
    {
        var id = order.GetProperty("legacyResourceId").GetString()!;
        var lineItems = order.GetProperty("lineItems");
        if (lineItems.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean())
            throw new OrderNotInvoiceableException($"Order {id} has more lines than a single request returns.");

        var lines = lineItems.GetProperty("nodes").EnumerateArray().Select(line =>
        {
            // Edited after checkout: Shopify's taxes and discounts may no longer match what is left.
            var quantity = line.GetProperty("quantity").GetInt32();
            if (line.GetProperty("currentQuantity").GetInt32() != quantity)
                throw new OrderNotInvoiceableException(
                    $"Order {id}, line \"{line.GetProperty("title").GetString()}\" was edited after checkout.");

            return ParseLine(line, quantity, line.GetProperty("originalUnitPriceSet"));
        }).ToList();

        var shippingLines = order.GetProperty("shippingLines").GetProperty("nodes").EnumerateArray()
            .Select(line => ParseLine(line, 1, line.GetProperty("originalPriceSet")))
            // Free shipping comes as a zero-priced line with a zero tax line: nothing to declare,
            // and it would otherwise add an empty 21% item to the invoice's VAT breakdown.
            .Where(line => line.OriginalUnitPrice != 0)
            .ToList();
        if (shippingLines.Count > 1)
            throw new OrderNotInvoiceableException($"Order {id} has {shippingLines.Count} shipping lines, expected at most one.");

        return new ShopifyOrder(
            Id: id,
            Name: order.GetProperty("name").GetString()!,
            InvoiceDate: invoiceDate,
            TaxesIncluded: order.GetProperty("taxesIncluded").GetBoolean(),
            // No checkout field collects a NIF yet (#3): every order is an F2 for now.
            BuyerNif: null,
            BuyerName: null,
            Lines: lines,
            Shipping: shippingLines.SingleOrDefault());
    }

    static ShopifyOrderLine ParseLine(JsonElement line, int quantity, JsonElement unitPrice) => new(
        Title: line.GetProperty("title").GetString() ?? "",
        Quantity: quantity,
        OriginalUnitPrice: ParseMoney(unitPrice),
        DiscountAllocated: line.GetProperty("discountAllocations").EnumerateArray()
            .Sum(allocation => ParseMoney(allocation.GetProperty("allocatedAmountSet"))),
        TaxLines: line.GetProperty("taxLines").EnumerateArray()
            .Select(tax => new ShopifyTaxLine(
                tax.GetProperty("ratePercentage").GetDecimal(),
                ParseMoney(tax.GetProperty("priceSet"))))
            .ToList());

    // Shop currency, not the one the customer paid in: the invoice is issued in the shop's (EUR).
    static decimal ParseMoney(JsonElement moneyBag) =>
        decimal.Parse(moneyBag.GetProperty("shopMoney").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);

    static DateTimeOffset? ParseDate(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null
            ? null
            : DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture);
}
