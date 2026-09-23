namespace VerifactuShopify;

// A VAT line Shopify already computed for a product or shipping line. RatePercentage matches
// VeriFactu.Business.TaxItem.TaxRate: a plain percentage (21, not 0.21).
public sealed record ShopifyTaxLine(decimal RatePercentage, decimal Amount);

// A single line of a Shopify order that carries its own price and VAT: a product line or the
// shipping line. Lines without a product or variant (manually added at checkout, or the
// product was later deleted) are just as valid here as ones tied to the catalog - the mapper
// only ever reads title, price and tax lines, never the catalog reference.
public sealed record ShopifyOrderLine(
    string Title,
    int Quantity,
    decimal OriginalUnitPrice,
    decimal DiscountAllocated,
    IReadOnlyList<ShopifyTaxLine> TaxLines);

// The subset of a Shopify order this connector needs to build an invoice. Deliberately its own
// type, decoupled from Shopify's GraphQL client: fetching the real order is a separate concern
// from mapping it to an invoice (see issue #3). BuyerNif is null today for every order - no
// checkout field collects it yet - but the mapper already supports it for when one does.
// Id is the order's legacyResourceId, which never changes; Name is what the merchant sees
// (#1001), which the shop can reformat. Both go into the invoice description (OrderReference).
public sealed record ShopifyOrder(
    string Id,
    string Name,
    DateTime InvoiceDate,
    bool TaxesIncluded,
    string? BuyerNif,
    string? BuyerName,
    IReadOnlyList<ShopifyOrderLine> Lines,
    ShopifyOrderLine? Shipping);
