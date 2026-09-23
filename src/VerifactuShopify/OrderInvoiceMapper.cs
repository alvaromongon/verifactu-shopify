using VeriFactu.Business;
using VeriFactu.Xml.Factu;
using VeriFactu.Xml.Factu.Alta;

namespace VerifactuShopify;

// Seller data and the simplified-invoice amount limit: everything the mapper needs besides the
// order itself. Kept separate from ShopifyOrder because it's the same for every order, not
// something read from Shopify. SimplifiedInvoiceLimit is configurable because the 400€/3.000€
// question is still open with the tax advisor.
public sealed record InvoiceMappingSettings(string SellerNif, string SellerName, decimal SimplifiedInvoiceLimit);

// Thrown when an order can't be turned into a VeriFactu invoice automatically and needs manual
// review instead - e.g. Shopify didn't compute VAT for a line, or the order is above the
// simplified invoice limit without a buyer NIF.
public sealed class OrderNotInvoiceableException(string message) : Exception(message);

// Maps a Shopify order into a VeriFactu invoice (F1 or F2). Pure logic, no network calls.
// Doesn't decide the invoice number (that's the AEAT-backed numbering component) or whether
// the order is due for invoicing yet (that's a deliberately separate, swappable rule) - see
// the design discussion on issue #3.
public static class OrderInvoiceMapper
{
    public static Invoice Map(ShopifyOrder order, string invoiceId, InvoiceMappingSettings settings)
    {
        var taxItems = BuildTaxItems(order);
        var total = taxItems.Sum(item => item.TaxBase + item.TaxAmount);

        var invoice = new Invoice(invoiceId, order.InvoiceDate, settings.SellerNif)
        {
            SellerName = settings.SellerName,
            Text = DescribeLines(order.Lines),
            TaxItems = taxItems,
        };

        if (order.BuyerNif is not null)
        {
            invoice.InvoiceType = TipoFactura.F1;
            invoice.BuyerID = order.BuyerNif;
            invoice.BuyerIDType = IDType.NIF_IVA;
            invoice.BuyerName = order.BuyerName
                ?? throw new OrderNotInvoiceableException($"Order {order.Id} has a buyer NIF but no buyer name.");
        }
        else if (total <= settings.SimplifiedInvoiceLimit)
        {
            invoice.InvoiceType = TipoFactura.F2;
        }
        else
        {
            throw new OrderNotInvoiceableException(
                $"Order {order.Id} totals {total}, above the simplified invoice limit of {settings.SimplifiedInvoiceLimit}, and has no buyer NIF.");
        }

        return invoice;
    }

    static List<TaxItem> BuildTaxItems(ShopifyOrder order)
    {
        var lines = order.Shipping is null ? order.Lines : [.. order.Lines, order.Shipping];
        if (lines.Count == 0)
            throw new OrderNotInvoiceableException($"Order {order.Id} has no lines to invoice.");

        return lines
            .Select(line => BuildContribution(order, line))
            .GroupBy(contribution => contribution.Rate)
            .Select(group => new TaxItem
            {
                TaxRate = group.Key,
                TaxBase = group.Sum(contribution => contribution.Base),
                TaxAmount = group.Sum(contribution => contribution.TaxAmount),
            })
            .OrderBy(item => item.TaxRate)
            .ToList();
    }

    // Assumes exactly one active VAT rate per line, true of every real order seen so far -
    // Spanish IVA doesn't stack the way e.g. federal/state sales tax can. A line with zero or
    // several tax lines is rejected rather than guessed at.
    static (decimal Rate, decimal Base, decimal TaxAmount) BuildContribution(ShopifyOrder order, ShopifyOrderLine line)
    {
        if (line.TaxLines.Count != 1)
            throw new OrderNotInvoiceableException(
                $"Order {order.Id}, line \"{line.Title}\" has {line.TaxLines.Count} tax lines, expected exactly one.");

        var taxLine = line.TaxLines[0];
        var grossTotal = line.OriginalUnitPrice * line.Quantity - line.DiscountAllocated;
        var baseAmount = order.TaxesIncluded ? grossTotal - taxLine.Amount : grossTotal;
        return (taxLine.RatePercentage, baseAmount, taxLine.Amount);
    }

    static string DescribeLines(IReadOnlyList<ShopifyOrderLine> lines) =>
        string.Join(", ", lines.Select(line => line.Title));
}
