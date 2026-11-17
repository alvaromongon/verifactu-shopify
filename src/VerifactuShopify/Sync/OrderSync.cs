using System.Globalization;

using Microsoft.Extensions.Configuration;

using VeriFactu.Business;
using VeriFactu.Xml.Factu;
using VeriFactu.Xml.Factu.Consulta.Respuesta;

using VerifactuShopify.Aeat;
using VerifactuShopify.Configuration;
using VerifactuShopify.Invoicing;
using VerifactuShopify.Shopify;
using VerifactuShopify.Storage;

namespace VerifactuShopify.Sync;

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

    // Long enough for a run with a backlog (see the SLO) between two renewals, short enough that a run
    // that died blocks the next ones for minutes, not hours.
    public static readonly TimeSpan LockTimeToLive = TimeSpan.FromMinutes(10);

    // The lock goes in the bucket, which the host does not link yet (#55): without a store the run
    // goes ahead unlocked, as it did before the lock existed.
    public static async Task<int> RunAsync(
        IConfiguration configuration, SistemaInformatico sistema, HttpClient http, DateTimeOffset now,
        IObjectStore? lockStore = null, TimeProvider? clock = null)
    {
        var sellerNif = configuration.GetRequired(VerifactuShopify.Configuration.ConfigurationExtensions.SellerNifKey);
        if (lockStore is null)
        {
            Console.WriteLine("Cerrojo:     sin almacén configurado, no se comprueba que no haya otra ejecución a la vez");
            return await SyncAsync(configuration, sistema, http, now, held: null);
        }

        // Before anything else: not even the chain is loaded while another run may be sending.
        var attempt = await IssuerLock.TryAcquireAsync(lockStore, sellerNif, LockTimeToLive, clock ?? TimeProvider.System);
        switch (attempt.State)
        {
            case LockState.Held:
                Console.WriteLine($"Cerrojo:     ocupado por otra ejecución hasta {attempt.HeldUntil:O}; esta pasada no envía nada");
                return Success;
            case LockState.Contended:
                Console.WriteLine("Cerrojo:     conflicto al adquirirlo (otra ejecución lo estaba escribiendo); esta pasada no envía nada y la siguiente lo reintenta");
                return Success;
        }

        var held = attempt.Lock!;
        try
        {
            return await SyncAsync(configuration, sistema, http, now, held);
        }
        finally
        {
            await ReleaseAsync(held);
        }
    }

    // A failed release must not hide how the run ended: the lock expires on its own.
    static async Task ReleaseAsync(IssuerLock held)
    {
        try
        {
            await held.ReleaseAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.WriteLine($"Cerrojo:     no se pudo soltar ({error.Message}); caducará solo en {LockTimeToLive.TotalMinutes:0} minutos como máximo");
        }
    }

    static async Task<int> SyncAsync(IConfiguration configuration, SistemaInformatico sistema, HttpClient http, DateTimeOffset now, IssuerLock? held)
    {
        var settings = SyncSettings.From(configuration);
        var sellerNif = configuration.GetRequired(VerifactuShopify.Configuration.ConfigurationExtensions.SellerNifKey);
        var sellerName = configuration.GetRequired(VerifactuShopify.Configuration.ConfigurationExtensions.SellerNameKey);

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
        {
            return Success;
        }

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

            // A lock lost to another run (this one stalled past the expiry): stop before sending anything more.
            if (held is not null && !await held.RenewAsync())
            {
                Console.WriteLine($"{label}: el cerrojo ya es de otra ejecución; esta se detiene sin enviar más.");
                return Failure;
            }

            var entry = new InvoiceEntry(invoice);
            try
            {
                // Records the registro and sends it to the AEAT.
                entry.Save();
            }
            catch
            {
                // The library drops the registro from the chain (BlockchainSetup.Load). Whether the AEAT
                // got it or not, the next run reads it from there and either skips this order or sends it
                // again.
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
    static List<string> SeriesNumbersThisYear(
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
