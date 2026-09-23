using VeriFactu.Xml.Factu;
using VeriFactu.Xml.Factu.Alta;

namespace VerifactuShopify.Tests;

// Pure mapping logic, no network: an order goes in, an Invoice (or a rejection) comes out.
// Shapes below mirror real, anonymized orders from RR Ibéricos (see issue #3): single-rate
// orders, mixed 10%/21% rates from a taxed shipping line, discounted lines, and lines without
// VAT computed at all.
public class OrderInvoiceMapperTests
{
    static readonly InvoiceMappingSettings Settings = new("B12345678", "EMISOR DE PRUEBA", SimplifiedInvoiceLimit: 400m);

    static ShopifyOrderLine Line(string title, int quantity, decimal originalUnitPrice, decimal discountAllocated, params ShopifyTaxLine[] taxLines) =>
        new(title, quantity, originalUnitPrice, discountAllocated, taxLines);

    static ShopifyOrder Order(IReadOnlyList<ShopifyOrderLine> lines, ShopifyOrderLine? shipping = null, bool taxesIncluded = true, string? buyerNif = null, string? buyerName = null) =>
        new("gid://shopify/Order/1", new DateTime(2026, 9, 15), taxesIncluded, buyerNif, buyerName, lines, shipping);

    [Fact]
    public void Single_line_with_no_buyer_nif_maps_to_F2()
    {
        var order = Order([Line("Jamón de bellota", 1, 110m, 0m, new ShopifyTaxLine(10, 10m))]);

        var invoice = OrderInvoiceMapper.Map(order, "S2026-0001", Settings);

        Assert.Equal(TipoFactura.F2, invoice.InvoiceType);
        var item = Assert.Single(invoice.TaxItems);
        Assert.Equal(10m, item.TaxRate);
        Assert.Equal(100m, item.TaxBase);
        Assert.Equal(10m, item.TaxAmount);
    }

    [Fact]
    public void Shipping_line_contributes_its_own_tax_rate()
    {
        var order = Order(
            [Line("Jamón de bellota", 1, 110m, 0m, new ShopifyTaxLine(10, 10m))],
            shipping: Line("Envío", 1, 12.10m, 0m, new ShopifyTaxLine(21, 2.10m)));

        var invoice = OrderInvoiceMapper.Map(order, "S2026-0001", Settings);

        Assert.Collection(invoice.TaxItems,
            item => { Assert.Equal(10m, item.TaxRate); Assert.Equal(100m, item.TaxBase); Assert.Equal(10m, item.TaxAmount); },
            item => { Assert.Equal(21m, item.TaxRate); Assert.Equal(10m, item.TaxBase); Assert.Equal(2.10m, item.TaxAmount); });
    }

    [Fact]
    public void Multiple_lines_at_the_same_rate_are_aggregated_into_one_tax_item()
    {
        var order = Order([
            Line("Lomo de bellota", 2, 34.65m, 0m, new ShopifyTaxLine(10, 5.73m)),
            Line("Jamón de bellota", 1, 435.60m, 0m, new ShopifyTaxLine(10, 39.60m)),
        ]);

        var invoice = OrderInvoiceMapper.Map(order, "S2026-0001", Settings with { SimplifiedInvoiceLimit = 1000m });

        var item = Assert.Single(invoice.TaxItems);
        Assert.Equal(10m, item.TaxRate);
        Assert.Equal(69.30m + 435.60m - 5.73m - 39.60m, item.TaxBase);
        Assert.Equal(5.73m + 39.60m, item.TaxAmount);
    }

    [Fact]
    public void Discount_reduces_the_taxable_base_when_taxes_are_not_included_in_price()
    {
        var order = Order(
            [Line("Chorizo ibérico", 2, 50m, 10m, new ShopifyTaxLine(10, 9m))],
            taxesIncluded: false);

        var invoice = OrderInvoiceMapper.Map(order, "S2026-0001", Settings);

        var item = Assert.Single(invoice.TaxItems);
        Assert.Equal(90m, item.TaxBase); // (50 * 2) - 10 discount, tax not baked into the price
        Assert.Equal(9m, item.TaxAmount);
    }

    [Fact]
    public void Buyer_nif_present_maps_to_F1_with_buyer_data()
    {
        var order = Order(
            [Line("Jamón de bellota", 1, 110m, 0m, new ShopifyTaxLine(10, 10m))],
            buyerNif: "12345678Z",
            buyerName: "Empresa de Prueba SL");

        var invoice = OrderInvoiceMapper.Map(order, "S2026-0001", Settings);

        Assert.Equal(TipoFactura.F1, invoice.InvoiceType);
        Assert.Equal("12345678Z", invoice.BuyerID);
        Assert.Equal(IDType.NIF_IVA, invoice.BuyerIDType);
        Assert.Equal("Empresa de Prueba SL", invoice.BuyerName);
    }

    [Fact]
    public void Above_the_simplified_limit_without_a_nif_is_rejected()
    {
        var order = Order([Line("Jamón de bellota 9kg", 1, 550m, 0m, new ShopifyTaxLine(10, 50m))]);

        var ex = Assert.Throws<OrderNotInvoiceableException>(() => OrderInvoiceMapper.Map(order, "S2026-0001", Settings));
        Assert.Contains(order.Id, ex.Message);
    }

    [Fact]
    public void Line_without_a_tax_line_is_rejected_instead_of_guessing_a_rate()
    {
        var order = Order([Line("Caña de lomo", 1, 50m, 0m)]);

        Assert.Throws<OrderNotInvoiceableException>(() => OrderInvoiceMapper.Map(order, "S2026-0001", Settings));
    }

    [Fact]
    public void Line_with_more_than_one_tax_line_is_rejected()
    {
        var order = Order([Line("Jamón de bellota", 1, 110m, 0m, new ShopifyTaxLine(10, 10m), new ShopifyTaxLine(21, 0m))]);

        Assert.Throws<OrderNotInvoiceableException>(() => OrderInvoiceMapper.Map(order, "S2026-0001", Settings));
    }

    [Fact]
    public void Order_without_any_lines_is_rejected()
    {
        var order = Order([]);

        Assert.Throws<OrderNotInvoiceableException>(() => OrderInvoiceMapper.Map(order, "S2026-0001", Settings));
    }
}
