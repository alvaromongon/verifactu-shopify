using System.Globalization;
using Microsoft.Extensions.Configuration;
using VeriFactu.Business;
using VeriFactu.Business.Operations;
using VeriFactu.Config;
using VeriFactu.Xml.Factu.Alta;
using VerifactuShopify;

// M1: herramienta para probar la librería contra preproducción de la AEAT.
const string SellerNifKey = "VeriFactu:Emisor:NIF";
const string SellerNameKey = "VeriFactu:Emisor:Nombre";
const string DateFormat = "dd-MM-yyyy";

// Antes de tocar nada de VeriFactu: la cultura decide cómo se lee la cadena de bloques.
CultureSetup.Configure();

// Las variables de entorno van después para que el despliegue pueda inyectar el certificado
// sobre lo que haya en user-secrets (VeriFactu__CertificatePath, VeriFactu__CertificatePasswordPath).
var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().AddEnvironmentVariables().Build();

try
{
    return args switch
    {
        ["certificado"] => CheckCertificate(),
        ["enviar-f2"] => SendF2(),
        ["anular", var invoiceId, var invoiceDate] =>
            Cancel(invoiceId, DateTime.ParseExact(invoiceDate, DateFormat, CultureInfo.InvariantCulture)),
        ["consultar", var year, var month] => Query(year, month),
        _ => PrintUsage(),
    };
}
catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or FormatException)
{
    // WebException también es InvalidOperationException: sin la interna no se ve por qué falló el TLS.
    for (var e = ex; e is not null; e = e.InnerException)
        Console.Error.WriteLine(e.Message);
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
        Console.WriteLine($"Anular:  dotnet run --project src/VerifactuShopify -- anular {invoice.InvoiceID} {invoice.InvoiceDate.ToString(DateFormat, CultureInfo.InvariantCulture)}");
    return sent ? 0 : 1;
}

int Cancel(string invoiceId, DateTime invoiceDate)
{
    PrepareSend();
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

void PrepareSend()
{
    // M1 solo prueba contra preproducción: un Settings.xml local podría apuntar a producción.
    var endpoint = Settings.Current.VeriFactuEndPointPrefix;
    if (!endpoint.StartsWith("https://prewww", StringComparison.Ordinal))
        throw new InvalidOperationException($"El endpoint configurado no es de preproducción: {endpoint}");

    // Sin using: la librería lo usa en el envío, a través de Wsd.Certificate.
    var certificate = CertificateSetup.Configure(configuration);
    SistemaInformaticoSetup.Configure(configuration);

    Console.WriteLine($"Endpoint:    {endpoint}");
    Console.WriteLine($"Certificado: {certificate.Subject}");
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
