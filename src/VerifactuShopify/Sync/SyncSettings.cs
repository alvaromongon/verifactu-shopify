using System.Globalization;

using Microsoft.Extensions.Configuration;

using VerifactuShopify.Configuration;
using VerifactuShopify.Invoicing;

namespace VerifactuShopify.Sync;

public sealed record SyncSettings(
    string ShopDomain,
    string ClientId,
    string ClientSecret,
    InvoicingRuleSettings Rule,
    InvoiceSeries Series,
    (int Year, int LastUsed)? Seed,
    decimal SimplifiedInvoiceLimit)
{
    public const string ShopDomainKey = "Shopify:Tienda";
    public const string ClientIdKey = "Shopify:ClientId";
    public const string ClientSecretKey = "Shopify:ClientSecret";
    public const string CutoffKey = "Sincronizacion:Desde";
    public const string MarginKey = "Sincronizacion:MargenMinutos";
    public const string PrefixKey = "Facturacion:Prefijo";
    public const string SeedKey = "Facturacion:Semilla";
    public const string SimplifiedInvoiceLimitKey = "Facturacion:LimiteSimplificada";

    public const int DefaultMarginMinutes = 10;
    // Pending the tax advisor's answer (400 € or 3.000 €, see #3): the lower one until then.
    public const decimal DefaultSimplifiedInvoiceLimit = 400m;

    public static SyncSettings From(IConfiguration configuration) => new(
        ShopDomain: configuration.GetRequired(ShopDomainKey),
        ClientId: configuration.GetRequired(ClientIdKey),
        ClientSecret: configuration.GetRequiredSecret(ClientSecretKey),
        Rule: new InvoicingRuleSettings(
            Cutoff: Parse(configuration, CutoffKey, value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture)),
            Margin: TimeSpan.FromMinutes(configuration[MarginKey] is { Length: > 0 }
                ? Parse(configuration, MarginKey, value => int.Parse(value, CultureInfo.InvariantCulture))
                : DefaultMarginMinutes)),
        Series: Parse(configuration, PrefixKey, prefix => new InvoiceSeries(prefix)),
        Seed: configuration[SeedKey] is { Length: > 0 } ? Parse(configuration, SeedKey, ParseSeed) : null,
        SimplifiedInvoiceLimit: configuration[SimplifiedInvoiceLimitKey] is { Length: > 0 }
            ? Parse(configuration, SimplifiedInvoiceLimitKey, value => decimal.Parse(value, CultureInfo.InvariantCulture))
            : DefaultSimplifiedInvoiceLimit);

    public int LastUsedBefore(int year) => Seed is { } seed && seed.Year == year ? seed.LastUsed : 0;

    // "yyyy:n": the last number the shop used that year outside the connector.
    static (int, int) ParseSeed(string value) =>
        value.Split(':') is [var year, var number]
            ? (int.Parse(year, CultureInfo.InvariantCulture), int.Parse(number, CultureInfo.InvariantCulture))
            : throw new FormatException();

    static T Parse<T>(IConfiguration configuration, string key, Func<string, T> parse)
    {
        var value = configuration.GetRequired(key);
        try
        {
            return parse(value);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidOperationException($"'{key}' no es válido: {value}", ex);
        }
    }
}
