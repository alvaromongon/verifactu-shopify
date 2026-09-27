using System.Globalization;

using Microsoft.Extensions.Configuration;

using VeriFactu.Blockchain;
using VeriFactu.Business;
using VeriFactu.Business.Operations;
using VeriFactu.Config;
using VeriFactu.Xml.Factu.Alta;

using VerifactuShopify.Aeat;
using VerifactuShopify.Configuration;
using VerifactuShopify.Sync;

// M1: tool to try the library against the AEAT pre-production environment.
const string SellerNifKey = VerifactuShopify.Configuration.ConfigurationExtensions.SellerNifKey;
const string SellerNameKey = VerifactuShopify.Configuration.ConfigurationExtensions.SellerNameKey;
const string DateFormat = "dd-MM-yyyy";

// Before touching anything in VeriFactu: the culture decides how the blockchain is read.
CultureSetup.Configure();

// Environment variables go last so the deployment can inject the certificate over whatever is in
// user-secrets (VeriFactu__CertificatePath, VeriFactu__CertificatePasswordPath).
var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables().Build();

try
{
    return args switch
    {
        ["certificado"] => CheckCertificate(),
        ["enviar-f2"] => SendF2(),
        ["anular", var invoiceId, var invoiceDate] =>
            Cancel(invoiceId, DateTime.ParseExact(invoiceDate, DateFormat, CultureInfo.InvariantCulture)),
        ["consultar", var year, var month] => Query(year, month),
        ["cadena"] => ShowChain(),
        ["sincronizar"] => await Sync(),
        _ => PrintUsage(),
    };
}
catch (Exception ex) when (ex is InvalidOperationException
                            or FileNotFoundException
                            or FormatException
                            or HttpRequestException)
{
    // WebException is an InvalidOperationException too: without the inner one, the TLS failure reason is lost.
    for (var e = ex; e is not null; e = e.InnerException)
    {
        Console.Error.WriteLine(e.Message);
    }

    return 1;
}

int PrintUsage()
{
    Console.Error.WriteLine("""
        Uso: dotnet run --project src/VerifactuShopify -- <comando>
          certificado                    comprueba el certificado configurado, sin enviar nada
          enviar-f2                      envía una factura simplificada de prueba a preproducción
          anular <numserie> <dd-mm-aaaa>  anula un registro enviado a preproducción
          consultar <aaaa> <mm>          lista lo que la AEAT tiene del emisor en ese periodo, sin enviar nada
          cadena                         muestra el último registro de la cadena según la AEAT y el local, sin enviar nada
          sincronizar                    envía a preproducción los pedidos pagados de Shopify que aún no tengan registro
                                         (sale con 2 si quedan pedidos para revisar a mano)
        """);
    return 1;
}

int CheckCertificate()
{
    var certificate = CertificateSetup.Configure(configuration);
    Console.WriteLine($"Titular: {certificate.Subject}");
    Console.WriteLine($"Emisor:  {certificate.Issuer}");
    Console.WriteLine($"Caduca:  {certificate.NotAfter:yyyy-MM-dd}");
    return 0;
}

int SendF2()
{
    PrepareSend();
    LoadChainFromAeat();
    var invoice = new Invoice($"M1-F2-{DateTime.Now:yyyyMMddHHmmss}", DateTime.Today, configuration.GetRequired(SellerNifKey))
    {
        InvoiceType = TipoFactura.F2,
        SellerName = configuration.GetRequired(SellerNameKey),
        Text = "Prueba M1 verifactu-shopify",
        TaxItems = [new TaxItem { TaxRate = 21, TaxBase = 100, TaxAmount = 21 }],
    };

    var entry = new InvoiceEntry(invoice);
    entry.Save();

    var sent = PrintResult(entry);
    Console.WriteLine($"Número:  {invoice.InvoiceID}");
    Console.WriteLine($"URL QR:  {invoice.GetRegistroAlta().GetUrlValidate()}");
    if (sent)
    {
        Console.WriteLine($"Anular:  dotnet run --project src/VerifactuShopify -- anular {invoice.InvoiceID} {invoice.InvoiceDate.ToString(DateFormat, CultureInfo.InvariantCulture)}");
    }

    return sent ? 0 : 1;
}

int Cancel(string invoiceId, DateTime invoiceDate)
{
    PrepareSend();
    LoadChainFromAeat();
    var invoice = new Invoice(invoiceId, invoiceDate, configuration.GetRequired(SellerNifKey))
    {
        SellerName = configuration.GetRequired(SellerNameKey),
    };

    var cancellation = new InvoiceCancellation(invoice);
    cancellation.Save();

    return PrintResult(cancellation) ? 0 : 1;
}

int Query(string year, string month)
{
    PrepareSend();
    var query = new InvoiceQuery(configuration.GetRequired(SellerNifKey), configuration.GetRequired(SellerNameKey));
    var response = query.GetSales(year, month);

    Console.WriteLine($"Resultado: {response.ResultadoConsulta} (paginación: {response.IndicadorPaginacion})");
    foreach (var registro in response.RegistroRespuestaConsultaFactuSistemaFacturacion ?? [])
    {
        var datos = registro.DatosRegistroFacturacion;
        var anterior = datos?.Encadenamiento?.RegistroAnterior;
        Console.WriteLine();
        Console.WriteLine($"Factura:      {registro.IDFactura.NumSerieFactura} {registro.IDFactura.FechaExpedicionFactura}");
        Console.WriteLine($"Estado:       {registro.EstadoRegistro?.EstadoReg} (modificado {registro.EstadoRegistro?.TimestampUltimaModificacion})");
        Console.WriteLine($"Presentado:   {registro.DatosPresentacion?.TimestampPresentacion}");
        Console.WriteLine($"Generado:     {datos?.FechaHoraHusoGenRegistro}");
        Console.WriteLine($"Huella:       {datos?.Huella}");
        Console.WriteLine(anterior is null
            ? $"Anterior:     primer registro = {datos?.Encadenamiento?.PrimerRegistro}"
            : $"Anterior:     {anterior.NumSerieFactura} {anterior.Huella}");
    }
    return 0;
}

int ShowChain()
{
    PrepareSend();
    var sellerNif = configuration.GetRequired(SellerNifKey);
    var head = BlockchainSetup.QueryHead(sellerNif, configuration.GetRequired(SellerNameKey),
        Settings.Current.SistemaInformatico, DateTimeOffset.Now);
    Console.WriteLine(head is null
        ? "AEAT:        sin registros de este SIF en el mes actual ni en el anterior"
        : $"AEAT:        {head.NumSerie} {head.FechaExpedicion} {head.Huella} (generado {head.GeneratedAt:O})");

    // Leave the local chain alone if it doesn't exist: Blockchain.Get would create its folder.
    var local = File.Exists(Path.Combine(Settings.Current.BlockchainPath, sellerNif, $"_{sellerNif}.csv"))
        ? Blockchain.Get(sellerNif).Current
        : null;
    Console.WriteLine(local is null
        ? "Local:       sin cadena"
        : $"Local:       {local.IDFactura.NumSerieFactura} {local.IDFactura.FechaExpedicionFactura} {local.Huella}");
    return head?.Huella == local?.Huella ? 0 : 1;
}

async Task<int> Sync()
{
    PrepareSend();
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    return await OrderSync.RunAsync(configuration, Settings.Current.SistemaInformatico, http, DateTimeOffset.Now);
}

void PrepareSend()
{
    // M1 only tests against pre-production: a local Settings.xml could point to production.
    var endpoint = Settings.Current.VeriFactuEndPointPrefix;
    if (!endpoint.StartsWith("https://prewww", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"El endpoint configurado no es de preproducción: {endpoint}");
    }

    // No using: the library uses it when sending, through Wsd.Certificate.
    var certificate = CertificateSetup.Configure(configuration);
    SistemaInformaticoSetup.Configure(configuration);

    Console.WriteLine($"Endpoint:    {endpoint}");
    Console.WriteLine($"Certificado: {certificate.Subject}");
    // Registros are dated with the process local time, which must be the one of the territory they're
    // issued from (art. 7.e of Orden HAC/1177/2024): in a container, TZ=Europe/Madrid.
    Console.WriteLine($"Huso:        {TimeZoneInfo.Local.Id} ({DateTimeOffset.Now:zzz})");
}

// Before creating any registro: the AEAT is the source of truth for the chain (#12).
void LoadChainFromAeat()
{
    var sellerNif = configuration.GetRequired(SellerNifKey);
    var head = BlockchainSetup.QueryHead(sellerNif, configuration.GetRequired(SellerNameKey),
        Settings.Current.SistemaInformatico, DateTimeOffset.Now);
    BlockchainSetup.Load(sellerNif, head);
    Console.WriteLine(head is null
        ? "Cadena:      vacía en la AEAT, el siguiente será el primer registro"
        : $"Cadena:      {head.NumSerie} {head.Huella}");
}

static bool PrintResult(InvoiceEntry entry)
{
    Console.WriteLine($"Estado:  {entry.Status}");
    if (string.IsNullOrEmpty(entry.CSV))
    {
        Console.WriteLine($"Error:   {entry.ErrorCode}: {entry.ErrorDescription}");
        return false;
    }

    Console.WriteLine($"CSV:     {entry.CSV}");
    return true;
}
