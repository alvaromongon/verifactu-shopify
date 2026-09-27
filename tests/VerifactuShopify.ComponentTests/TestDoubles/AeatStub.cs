using System.Text.RegularExpressions;
using System.Xml.Linq;

using VeriFactu.Config;
using VeriFactu.Xml;
using VeriFactu.Xml.Factu.Consulta;
using VeriFactu.Xml.Factu.Consulta.Respuesta;
using VeriFactu.Xml.Soap;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

using SistemaInformatico = VeriFactu.Xml.Factu.SistemaInformatico;

namespace VerifactuShopify.ComponentTests.TestDoubles;

// The AEAT VERI*FACTU web service. VeriFactu posts to Settings.Current.VeriFactuEndPointPrefix, so
// pointing it here keeps every call on this machine; the responses follow real pre-production ones.
public sealed partial class AeatStub : IDisposable
{
    const string Consulta = "ConsultaFactuSistemaFacturacion";
    const string Alta = "RegFactuSistemaFacturacion";

    readonly WireMockServer _server = WireMockServer.Start();

    public AeatStub()
    {
        Settings.Current.VeriFactuEndPointPrefix = $"{_server.Url}/wlpl/TIKE-CONT/ws/SistemaFacturacion/VerifactuSOAP";
        GivenRecords();
        GivenAltasAccepted();
    }

    // Registros the AEAT returns for every period queried.
    public AeatStub GivenRecords(SistemaInformatico? sistema = null, params RegistroRespuestaConsultaFactuSistemaFacturacion[] registros) =>
        GivenConsulta(Page(sistema, registros, next: null));

    // Registros in two pages: the second one is asked for with the key the first one returns.
    public AeatStub GivenPagedRecords(
        SistemaInformatico sistema, RegistroRespuestaConsultaFactuSistemaFacturacion first, RegistroRespuestaConsultaFactuSistemaFacturacion second)
    {
        var key = new ClavePaginacion { IDEmisorFactura = first.IDFactura.IDEmisorFactura, NumSerieFactura = first.IDFactura.NumSerieFactura, FechaExpedicionFactura = first.IDFactura.FechaExpedicionFactura };
        // Among equally ranked mappings WireMock uses the latest one that matches.
        GivenConsulta(Page(sistema, [first], key));
        _server.Given(Request.Create().UsingPost().WithBody(body =>
                body?.Contains(Consulta, StringComparison.Ordinal) == true && body.Contains("ClavePaginacion", StringComparison.Ordinal)))
            .RespondWith(Response.Create().WithHeader("Content-Type", "text/xml").WithBody(Page(sistema, [second], next: null)));
        return this;
    }

    // A SOAP fault, as the AEAT answers a malformed request.
    public AeatStub GivenFault(string message)
    {
        _server.Given(Request.Create().UsingPost().WithBody(body => body?.Contains(Consulta, StringComparison.Ordinal) == true))
            .RespondWith(Response.Create().WithHeader("Content-Type", "text/xml").WithBody($"""
                <?xml version="1.0" encoding="UTF-8"?>
                <env:Envelope xmlns:env="http://schemas.xmlsoap.org/soap/envelope/"><env:Body><env:Fault><faultcode>env:Client</faultcode><faultstring>{message}</faultstring></env:Fault></env:Body></env:Envelope>
                """));
        return this;
    }

    AeatStub GivenConsulta(string response)
    {
        _server.Given(Request.Create().UsingPost().WithBody(body => body?.Contains(Consulta, StringComparison.Ordinal) == true))
            .RespondWith(Response.Create().WithHeader("Content-Type", "text/xml").WithBody(response));
        return this;
    }

    static string Page(SistemaInformatico? sistema, RegistroRespuestaConsultaFactuSistemaFacturacion[] registros, ClavePaginacion? next)
    {
        var respuesta = new RespuestaConsultaFactuSistemaFacturacion
        {
            IndicadorPaginacion = next is null ? "N" : "S",
            ResultadoConsulta = registros.Length == 0 ? "SinDatos" : "ConDatos",
            RegistroRespuestaConsultaFactuSistemaFacturacion = registros,
            ClavePaginacion = next,
        };
        var xml = XDocument.Parse(new XmlParser().GetString(new Envelope { Body = new Body { Registro = respuesta } }, Namespaces.Items));

        // The AEAT sends each registro's SIF, which the library leaves out (mdiago/VeriFactu#292).
        XNamespace tikLrrc = Namespaces.NamespaceTikLRRC;
        XNamespace sf = Namespaces.NamespaceSF;
        foreach (var registro in xml.Descendants(tikLrrc + "RegistroRespuestaConsultaFactuSistemaFacturacion"))
        {
            registro.Add(new XElement(tikLrrc + "SistemaInformatico",
                new XElement(sf + "NombreRazon", sistema!.NombreRazon), new XElement(sf + "NIF", sistema.NIF),
                new XElement(sf + "IdSistemaInformatico", sistema.IdSistemaInformatico),
                new XElement(sf + "Version", sistema.Version), new XElement(sf + "NumeroInstalacion", sistema.NumeroInstalacion)));
        }

        return xml.ToString();
    }

    public AeatStub GivenAltasAccepted() => GivenAltas(numSerie => $"""
        <tikR:CSV>A-TESTCSV0000001</tikR:CSV>{Datos}<tikR:EstadoEnvio>Correcto</tikR:EstadoEnvio>
        {Linea(numSerie, "<tikR:EstadoRegistro>Correcto</tikR:EstadoRegistro>")}
        """);

    public AeatStub GivenAltasRejected(string code, string description) => GivenAltas(numSerie => $"""
        {Datos}<tikR:EstadoEnvio>Incorrecto</tikR:EstadoEnvio>
        {Linea(numSerie, $"<tikR:EstadoRegistro>Incorrecto</tikR:EstadoRegistro><tikR:CodigoErrorRegistro>{code}</tikR:CodigoErrorRegistro><tikR:DescripcionErrorRegistro>{description}</tikR:DescripcionErrorRegistro>")}
        """);

    public AeatStub GivenAltasFail()
    {
        _server.Given(Request.Create().UsingPost().WithBody(body => body?.Contains(Alta, StringComparison.Ordinal) == true))
            .RespondWith(Response.Create().WithStatusCode(503));
        return this;
    }

    public IReadOnlyList<string> Altas() =>
        [.. _server.LogEntries.Select(entry => entry.RequestMessage!.Body ?? "").Where(body => body.Contains(Alta, StringComparison.Ordinal))];

    public int Consultas() =>
        _server.LogEntries.Count(entry => (entry.RequestMessage!.Body ?? "").Contains(Consulta, StringComparison.Ordinal));

    const string Datos = """
        <tikR:DatosPresentacion><tik:NIFPresentador>12345678Z</tik:NIFPresentador><tik:TimestampPresentacion>2026-09-23T10:00:00+02:00</tik:TimestampPresentacion></tikR:DatosPresentacion><tikR:Cabecera><tik:ObligadoEmision><tik:NombreRazon>EMISOR DE PRUEBA</tik:NombreRazon><tik:NIF>12345678Z</tik:NIF></tik:ObligadoEmision></tikR:Cabecera><tikR:TiempoEsperaEnvio>60</tikR:TiempoEsperaEnvio>
        """;

    static string Linea(string numSerie, string estado) => $"""
        <tikR:RespuestaLinea><tikR:IDFactura><tik:IDEmisorFactura>12345678Z</tik:IDEmisorFactura><tik:NumSerieFactura>{numSerie}</tik:NumSerieFactura><tik:FechaExpedicionFactura>23-09-2026</tik:FechaExpedicionFactura></tikR:IDFactura><tikR:Operacion><tik:TipoOperacion>Alta</tik:TipoOperacion></tikR:Operacion>{estado}</tikR:RespuestaLinea>
        """;

    AeatStub GivenAltas(Func<string, string> body)
    {
        _server.Given(Request.Create().UsingPost().WithBody(request => request?.Contains(Alta, StringComparison.Ordinal) == true))
            .RespondWith(Response.Create().WithCallback(request => new WireMock.ResponseMessage
            {
                StatusCode = 200,
                Headers = new Dictionary<string, WireMock.Types.WireMockList<string>> { ["Content-Type"] = new("text/xml") },
                BodyData = new WireMock.Util.BodyData
                {
                    DetectedBodyType = WireMock.Types.BodyType.String,
                    BodyAsString = $"""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <env:Envelope xmlns:env="http://schemas.xmlsoap.org/soap/envelope/"><env:Header></env:Header><env:Body Id="Body"><tikR:RespuestaRegFactuSistemaFacturacion xmlns:tikR="https://www2.agenciatributaria.gob.es/static_files/common/internet/dep/aplicaciones/es/aeat/tike/cont/ws/RespuestaSuministro.xsd" xmlns:tik="https://www2.agenciatributaria.gob.es/static_files/common/internet/dep/aplicaciones/es/aeat/tike/cont/ws/SuministroInformacion.xsd">{body(NumSerie().Match(request.Body ?? "").Groups[1].Value)}</tikR:RespuestaRegFactuSistemaFacturacion></env:Body></env:Envelope>
                        """,
                },
            }));
        return this;
    }

    [GeneratedRegex("NumSerieFactura>([^<]+)<")]
    private static partial Regex NumSerie();

    public void Dispose() => _server.Dispose();
}
