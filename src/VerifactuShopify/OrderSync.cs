using System.Globalization;
using Microsoft.Extensions.Configuration;
using VeriFactu.Business;
using VeriFactu.Xml.Factu;
using VeriFactu.Xml.Factu.Consulta.Respuesta;

namespace VerifactuShopify;

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
            Margin: TimeSpan.FromMinutes(configuration[MarginKey] is { Length: > 0 } margin
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

// One run of the sync: paid orders from Shopify, what was already sent from the AEAT, and a
// registro for each order still missing one. Nothing is kept between runs (#4, #12).
public static class OrderSync
{
    // Exit codes, so a scheduler can alert on real failures only. Orders waiting for manual review
    // come up again on every run until someone deals with them: reporting them as a failure would
    // leave the job red for days. Any other error (AEAT rejection, Shopify or AEAT unreachable,
    // bad configuration) exits with 1.
    public const int Success = 0;
    public const int Failure = 1;
    public const int NeedsReview = 2;

    public static async Task<int> RunAsync(IConfiguration configuration, SistemaInformatico sistema, HttpClient http, DateTimeOffset now)
    {
        var settings = SyncSettings.From(configuration);
        var sellerNif = configuration.GetRequired(ConfigurationExtensions.SellerNifKey);
        var sellerName = configuration.GetRequired(ConfigurationExtensions.SellerNameKey);

        // AEAT first: the same query gives the chain head, the orders already invoiced and the
        // last invoice number, and a chain mismatch stops the run before touching Shopify.
        var records = BlockchainSetup.QueryRecords(sellerNif, sellerName, sistema, BlockchainSetup.Periods(now));
        var head = BlockchainSetup.FindHead(records);
        BlockchainSetup.Load(sellerNif, head);
        Console.WriteLine(head is null
            ? "Cadena:      vacía en la AEAT, el siguiente será el primer registro"
            : $"Cadena:      {head.NumSerie} {head.Huella}");

        var invoiced = InvoicedOrderIds(records);

        // The window the AEAT was just queried for: an order paid before it can't be checked.
        var windowStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1).AddMonths(-1));
        var since = windowStart > settings.Rule.Cutoff ? windowStart : settings.Rule.Cutoff;

        var shopify = await ShopifyClient.ConnectAsync(http, settings.ShopDomain, settings.ClientId, settings.ClientSecret);
        // updated_at rather than processed_at: an order paid after it was created (a draft order
        // paid later, a manual payment) is updated when it's paid.
        var statuses = await shopify.GetOrderStatusesAsync(
            $"financial_status:paid updated_at:>='{since.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)}'");

        var pending = statuses
            .Where(order => !invoiced.Contains(order.Id))
            .Where(order => InvoicingRule.Decide(order, now, windowStart, settings.Rule) == InvoicingDecision.Invoice)
            .OrderBy(order => order.PaidAt)
            .ToList();
        Console.WriteLine($"Shopify:     {statuses.Count} pagados desde {since:yyyy-MM-dd HH:mm}, {pending.Count} por facturar");
        if (pending.Count == 0)
            return Success;

        var year = now.Year;
        var next = settings.Series.Next(SeriesNumbersThisYear(records, settings.Series, year, sellerNif, sellerName, sistema, now),
            year, settings.LastUsedBefore(year));
        var mapping = new InvoiceMappingSettings(sellerNif, sellerName, settings.SimplifiedInvoiceLimit);

        var needsReview = false;
        foreach (var status in pending)
        {
            var label = $"{status.Name} ({status.Id})";
            Invoice invoice;
            try
            {
                var order = ShopifyOrderParser.ParseOrder(await shopify.GetOrderAsync(status.Id), now.Date);
                invoice = OrderInvoiceMapper.Map(order, settings.Series.Format(year, next), mapping);
            }
            catch (OrderNotInvoiceableException ex)
            {
                // Skipped without spending a number; it comes up again every run until it's fixed.
                Console.WriteLine($"{label}: revisar a mano. {ex.Message}");
                needsReview = true;
                continue;
            }

            var entry = new InvoiceEntry(invoice);
            try
            {
                // Contabiliza y envía a la AEAT el registro.
                entry.Save();
            }
            catch
            {
                // The library drops the registro from the chain. Whether the AEAT got it or not, the
                // next run reads it from there and either skips this order or sends it again.
                Console.WriteLine($"{label}: {invoice.InvoiceID} sin respuesta de la AEAT.");
                throw;
            }

            if (string.IsNullOrEmpty(entry.CSV))
            {
                // A rejection would repeat for every run with this data: stop and let someone look.
                Console.WriteLine($"{label}: {invoice.InvoiceID} rechazada. {entry.ErrorCode}: {entry.ErrorDescription}");
                return Failure;
            }

            Console.WriteLine($"{label}: {invoice.InvoiceID} {entry.Status} CSV {entry.CSV}");
            next++;
        }

        return needsReview ? NeedsReview : Success;
    }

    // Every registro counts, whatever its state: an anulled invoice doesn't bring its order back,
    // and a rejected one needs a look rather than another number.
    public static HashSet<string> InvoicedOrderIds(IEnumerable<RegistroRespuestaConsultaFactuSistemaFacturacion> records) =>
        records
            .Select(record => OrderReference.FindOrderId(record.DatosRegistroFacturacion?.DescripcionOperacion))
            .OfType<string>()
            .ToHashSet();

    // The last number is almost always in the two months already queried. If not (no sales for a
    // while), look further back in the year, month by month, up to the first month that has one:
    // numbers only grow with the issue date, which is what the AEAT groups registros by.
    static IEnumerable<string> SeriesNumbersThisYear(
        List<RegistroRespuestaConsultaFactuSistemaFacturacion> window, InvoiceSeries series, int year,
        string sellerNif, string sellerName, SistemaInformatico sistema, DateTimeOffset now)
    {
        var numbers = window.Select(record => record.IDFactura.NumSerieFactura).ToList();
        for (var month = now.AddMonths(-2); !numbers.Any(number => series.NumberIn(number, year) is not null) && month.Year == year; month = month.AddMonths(-1))
        {
            numbers = BlockchainSetup.QueryRecords(sellerNif, sellerName, sistema, [BlockchainSetup.Period(month)])
                .Select(record => record.IDFactura.NumSerieFactura)
                .ToList();
        }

        return numbers;
    }
}
