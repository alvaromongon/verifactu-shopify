using System.Text.Json;
using VeriFactu.Xml.Factu.Alta;

namespace VerifactuShopify.Tests;

// Responses below are real ones from RR Ibéricos for ShopifyClient's queries. Nothing about the
// customer is ever requested, so there is nothing to anonymize.
public class ShopifyOrderParserTests
{
    // Order #1289: one product at 10% and a paid shipping line at 21%.
    const string Order1289 = """
        {"legacyResourceId":"12901469978959","name":"#1289","taxesIncluded":true,
         "lineItems":{"pageInfo":{"hasNextPage":false},"nodes":[
           {"title":"5x100gr sobres de Jamón/Paleta de bellota 100% ibérico","quantity":1,"currentQuantity":1,
            "originalUnitPriceSet":{"shopMoney":{"amount":"65.6"}},"discountAllocations":[],
            "taxLines":[{"ratePercentage":10,"priceSet":{"shopMoney":{"amount":"5.96"}}}]}]},
         "shippingLines":{"nodes":[
           {"title":"Tarifa fija 11,9€ para pedidos de menos de 120€","originalPriceSet":{"shopMoney":{"amount":"11.9"}},
            "discountAllocations":[],"taxLines":[{"ratePercentage":21,"priceSet":{"shopMoney":{"amount":"2.07"}}}]}]}}
        """;

    // Order #1290: free shipping, which Shopify sends as a zero line with a zero 21% tax line.
    const string Order1290 = """
        {"legacyResourceId":"12903660061007","name":"#1290","taxesIncluded":true,
         "lineItems":{"pageInfo":{"hasNextPage":false},"nodes":[
           {"title":"Paleta de bellota 100% ibérica de 5-5,5 kg","quantity":1,"currentQuantity":1,
            "originalUnitPriceSet":{"shopMoney":{"amount":"151.6"}},"discountAllocations":[],
            "taxLines":[{"ratePercentage":10,"priceSet":{"shopMoney":{"amount":"13.78"}}}]}]},
         "shippingLines":{"nodes":[
           {"title":"Envío Gratis","originalPriceSet":{"shopMoney":{"amount":"0.0"}},
            "discountAllocations":[],"taxLines":[{"ratePercentage":21,"priceSet":{"shopMoney":{"amount":"0.0"}}}]}]}}
        """;

    static readonly DateTime Today = new(2026, 9, 23);
    static readonly InvoiceMappingSettings Mapping = new("B12345678", "EMISOR DE PRUEBA", SimplifiedInvoiceLimit: 400m);

    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static JsonElement Edit(string json, Action<System.Text.Json.Nodes.JsonNode> edit)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        edit(node);
        return Json(node.ToJsonString());
    }

    [Fact]
    public void Real_order_maps_to_an_F2_with_product_and_shipping_rates()
    {
        var order = ShopifyOrderParser.ParseOrder(Json(Order1289), Today);
        var invoice = OrderInvoiceMapper.Map(order, "PRE-2026-000001", Mapping);

        Assert.Equal("12901469978959", order.Id);
        Assert.Equal("#1289", order.Name);
        Assert.Equal(Today, invoice.InvoiceDate);
        Assert.Equal(TipoFactura.F2, invoice.InvoiceType);
        Assert.Collection(invoice.TaxItems,
            item => { Assert.Equal(10m, item.TaxRate); Assert.Equal(59.64m, item.TaxBase); Assert.Equal(5.96m, item.TaxAmount); },
            item => { Assert.Equal(21m, item.TaxRate); Assert.Equal(9.83m, item.TaxBase); Assert.Equal(2.07m, item.TaxAmount); });
    }

    [Fact]
    public void Free_shipping_adds_nothing_to_the_invoice()
    {
        var order = ShopifyOrderParser.ParseOrder(Json(Order1290), Today);
        var invoice = OrderInvoiceMapper.Map(order, "PRE-2026-000001", Mapping);

        Assert.Null(order.Shipping);
        var item = Assert.Single(invoice.TaxItems);
        Assert.Equal(10m, item.TaxRate);
    }

    [Fact]
    public void Discount_allocations_are_added_up_per_line()
    {
        var json = Edit(Order1289, order => order["lineItems"]!["nodes"]![0]!["discountAllocations"] = System.Text.Json.Nodes.JsonNode.Parse(
            """[{"allocatedAmountSet":{"shopMoney":{"amount":"5.0"}}},{"allocatedAmountSet":{"shopMoney":{"amount":"1.5"}}}]"""));

        Assert.Equal(6.5m, ShopifyOrderParser.ParseOrder(json, Today).Lines[0].DiscountAllocated);
    }

    [Fact]
    public void Order_edited_after_checkout_is_not_invoiceable()
    {
        var json = Edit(Order1289, order => order["lineItems"]!["nodes"]![0]!["currentQuantity"] = 0);

        Assert.Throws<OrderNotInvoiceableException>(() => ShopifyOrderParser.ParseOrder(json, Today));
    }

    [Fact]
    public void Order_with_more_lines_than_one_page_is_not_invoiceable()
    {
        var json = Edit(Order1289, order => order["lineItems"]!["pageInfo"]!["hasNextPage"] = true);

        Assert.Throws<OrderNotInvoiceableException>(() => ShopifyOrderParser.ParseOrder(json, Today));
    }

    [Fact]
    public void Order_with_two_paid_shipping_lines_is_not_invoiceable()
    {
        var json = Edit(Order1289, order =>
        {
            var shipping = order["shippingLines"]!["nodes"]!.AsArray();
            shipping.Add(shipping[0]!.DeepClone());
        });

        Assert.Throws<OrderNotInvoiceableException>(() => ShopifyOrderParser.ParseOrder(json, Today));
    }

    [Fact]
    public void Paid_at_is_the_last_successful_sale_or_capture()
    {
        var status = ShopifyOrderParser.ParseStatus(Json("""
            {"legacyResourceId":"12901469978959","name":"#1289","test":false,"cancelledAt":null,"displayFinancialStatus":"PAID",
             "transactions":[
               {"kind":"AUTHORIZATION","status":"SUCCESS","processedAt":"2026-09-12T18:00:00Z"},
               {"kind":"CAPTURE","status":"FAILURE","processedAt":"2026-09-12T18:30:00Z"},
               {"kind":"CAPTURE","status":"SUCCESS","processedAt":"2026-09-12T19:07:41Z"},
               {"kind":"REFUND","status":"SUCCESS","processedAt":"2026-09-13T10:00:00Z"}]}
            """));

        Assert.Equal(new DateTimeOffset(2026, 9, 12, 19, 7, 41, TimeSpan.Zero), status.PaidAt);
        Assert.Equal("PAID", status.FinancialStatus);
        Assert.Null(status.CancelledAt);
    }

    [Fact]
    public void Order_without_a_successful_payment_has_no_paid_at()
    {
        var status = ShopifyOrderParser.ParseStatus(Json("""
            {"legacyResourceId":"1","name":"#1","test":true,"cancelledAt":"2026-09-12T19:07:41Z","displayFinancialStatus":"PENDING",
             "transactions":[{"kind":"SALE","status":"PENDING","processedAt":"2026-09-12T18:00:00Z"}]}
            """));

        Assert.Null(status.PaidAt);
        Assert.True(status.Test);
        Assert.NotNull(status.CancelledAt);
    }
}
