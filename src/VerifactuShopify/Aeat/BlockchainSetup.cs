using System.Globalization;
using System.Xml.Linq;

using VeriFactu.Blockchain;
using VeriFactu.Business.Operations;
using VeriFactu.Common.Exceptions;
using VeriFactu.Config;
using VeriFactu.Xml;
using VeriFactu.Xml.Factu;
using VeriFactu.Xml.Factu.Consulta;
using VeriFactu.Xml.Factu.Consulta.Respuesta;
using VeriFactu.Xml.Factu.Fault;
using VeriFactu.Xml.Soap;

using PeriodoImputacion = VeriFactu.Xml.Factu.Consulta.PeriodoImputacion;

namespace VerifactuShopify.Aeat;

// Last registro of a seller's chain, as the AEAT has it.
public sealed record ChainHead(
    string Huella, string IdEmisor, string NumSerie, string FechaExpedicion, DateTimeOffset GeneratedAt);

// The AEAT is the source of truth for the chain (#12): the deployment keeps no state, so every
// run reads the last registro from the AEAT and continues the chain from it.
public static class BlockchainSetup
{
    // The head is always in one of these two months: the connector only cancels invoices from the
    // current or the previous month, and everything else carries today's date. The query can't filter
    // by generation date.
    public static IEnumerable<PeriodoImputacion> Periods(DateTimeOffset now)
    {
        yield return Period(now.AddMonths(-1));
        yield return Period(now);
    }

    public static PeriodoImputacion Period(DateTimeOffset month) =>
        new() { Ejercicio = $"{month:yyyy}", Periodo = $"{month:MM}" };

    // The query returns the last registro of each invoice (alta or anulación), so the head is the
    // most recent one. With two generated in the same second, it's the one pointing to the other.
    public static ChainHead? FindHead(IEnumerable<RegistroRespuestaConsultaFactuSistemaFacturacion> registros)
    {
        var candidates = registros
            .Where(r => r.EstadoRegistro?.EstadoReg != "Incorrecto" && r.DatosRegistroFacturacion?.Huella is not null)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var latest = candidates.Max(GeneratedAt);
        var tied = candidates.Where(r => GeneratedAt(r) == latest).ToList();
        var referenced = tied.Select(r => r.DatosRegistroFacturacion.Encadenamiento?.RegistroAnterior?.Huella).ToHashSet();
        var heads = tied.Where(r => !referenced.Contains(r.DatosRegistroFacturacion.Huella)).ToList();
        if (heads.Count != 1)
        {
            throw new InvalidOperationException(
                $"No se puede determinar el último registro de la cadena: {heads.Count} candidatos generados a las {latest:O}.");
        }

        var head = heads[0];
        return new ChainHead(head.DatosRegistroFacturacion.Huella, head.IDFactura.IDEmisorFactura,
            head.IDFactura.NumSerieFactura, head.IDFactura.FechaExpedicionFactura, latest);
    }

    public static ChainHead? QueryHead(string sellerNif, string sellerName, SistemaInformatico sistema, DateTimeOffset now) =>
        FindHead(QueryRecords(sellerNif, sellerName, sistema, Periods(now)));

    // Every registro this SIF sent for the seller in those periods, as the AEAT has it: the last
    // registro of each invoice (alta or anulación). Besides the chain head, the sync reads from it
    // which orders were already invoiced and the last invoice number used.
    public static List<RegistroRespuestaConsultaFactuSistemaFacturacion> QueryRecords(
        string sellerNif, string sellerName, SistemaInformatico sistema, IEnumerable<PeriodoImputacion> periods)
    {
        var query = new InvoiceQuery(sellerNif, sellerName);
        var registros = new List<RegistroRespuestaConsultaFactuSistemaFacturacion>();
        foreach (var period in periods)
        {
            ClavePaginacion? next = null;
            do
            {
                var consulta = query.GetRegistro();
                consulta.FiltroConsulta.PeriodoImputacion = period;
                consulta.FiltroConsulta.ClavePaginacion = next;
                // The same NIF may invoice from other systems (Shopify POS, for instance), each with its
                // own chain. The query's SIF filter is no use: VeriFactu 1.0.66 serializes its fields in
                // the wrong namespace and the AEAT answers 4102 (mdiago/VeriFactu#292). The SIF of each
                // registro is requested and filtered here instead.
                consulta.DatosAdicionalesRespuesta = new DatosAdicionalesRespuesta { MostrarSistemaInformatico = "S" };

                var (respuesta, sifs) = Send(consulta);
                var page = respuesta.RegistroRespuestaConsultaFactuSistemaFacturacion ?? [];
                if (page.Length != sifs.Count)
                {
                    throw new InvalidOperationException(
                        $"La AEAT devolvió {page.Length} registros y {sifs.Count} sistemas informáticos: no se puede saber a qué cadena pertenece cada uno.");
                }

                registros.AddRange(page.Where((_, i) => IsFrom(sistema, sifs[i])));
                next = respuesta.IndicadorPaginacion == "S" ? respuesta.ClavePaginacion : null;
            }
            while (next is not null);
        }

        return registros;
    }

    // Same as InvoiceQuery.GetDocuments, but also reading the SIF of each registro: VeriFactu 1.0.66
    // expects it in the response namespace and the AEAT sends its fields in the SuministroInformacion
    // one, so the library leaves it empty (mdiago/VeriFactu#292).
    static (RespuestaConsultaFactuSistemaFacturacion, List<XElement?>) Send(ConsultaFactuSistemaFacturacion consulta)
    {
        var xml = new XmlParser().GetBytes(new Envelope { Body = new Body { Registro = consulta } }, Namespaces.Items);
        var response = InvoiceActionMessage.SendXmlBytes(xml, "?op=ConsultaFactuSistemaFacturacion");

        var registro = Envelope.FromXml(response).Body.Registro;
        if (registro is Fault fault)
        {
            throw new FaultException(fault);
        }

        XNamespace respuesta = Namespaces.NamespaceTikLRRC;
        var sifs = XDocument.Parse(response)
            .Descendants(respuesta + "RegistroRespuestaConsultaFactuSistemaFacturacion")
            .Select(r => r.Descendants(respuesta + "SistemaInformatico").FirstOrDefault())
            .ToList();
        return ((RespuestaConsultaFactuSistemaFacturacion)registro, sifs);
    }

    // A SIF is identified by producer, id and installation number; the version doesn't count.
    public static bool IsFrom(SistemaInformatico sistema, XElement? sif)
    {
        XNamespace sf = Namespaces.NamespaceSF;
        var nif = sif?.Element(sf + "NIF")?.Value;
        var id = sif?.Element(sf + "IdSistemaInformatico")?.Value;
        var instalacion = sif?.Element(sf + "NumeroInstalacion")?.Value;

        // Without a SIF, dropping the registro would make the chain look empty and start another.
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(instalacion))
        {
            throw new InvalidOperationException("La AEAT no devolvió el sistema informático de un registro: no se puede saber a qué cadena pertenece.");
        }

        return nif == sistema.NIF && id == sistema.IdSistemaInformatico && instalacion == sistema.NumeroInstalacion;
    }

    // Points the seller's chain in the library at the AEAT head. Call it before creating any
    // InvoiceEntry: the library reads the chain from disk only once, when it starts. With no public way
    // to set the head, its internal file is written (proposed in mdiago/VeriFactu#293).
    public static void Load(string sellerNif, ChainHead? head)
    {
        var blockchainPath = Settings.Current.BlockchainPath;
        var varFile = VarFileName(blockchainPath, sellerNif);

        // A chain was already on disk (local development): it was loaded on start and is only checked.
        if (File.Exists(varFile))
        {
            var local = Blockchain.Get(sellerNif).Current?.Huella;
            if (local != head?.Huella)
            {
                throw new InvalidOperationException(
                    $"La cadena local ({local}) no coincide con la de la AEAT ({head?.Huella ?? "vacía"}). No se envía nada.");
            }

            return;
        }

        // The AEAT has nothing: the next registro will be the first.
        if (head is null)
        {
            return;
        }

        // LoadBlockchainsFromDisk fails with any seller folder already loaded, even without a chain.
        // Being stateless, the data folder starts empty.
        var existing = Directory.GetDirectories(blockchainPath).Select(Path.GetFileName).ToList();
        if (existing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Para cargar la cadena desde la AEAT, {blockchainPath} tiene que estar vacía. Contiene: {string.Join(", ", existing)}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(varFile)!);
        File.WriteAllText(varFile, VarFileLine(head));
        Blockchain.LoadBlockchainsFromDisk();

        var loaded = Blockchain.Get(sellerNif).Current?.Huella;
        if (loaded != head.Huella)
        {
            throw new InvalidOperationException(
                $"La librería no cargó la cabeza de la AEAT ({head.Huella}), sino {loaded ?? "nada"}: ¿ha cambiado el formato de {varFile}?");
        }
    }

    static DateTimeOffset GeneratedAt(RegistroRespuestaConsultaFactuSistemaFacturacion registro) =>
        DateTimeOffset.Parse(registro.DatosRegistroFacturacion.FechaHoraHusoGenRegistro, CultureInfo.InvariantCulture);

    static string VarFileName(string blockchainPath, string sellerNif) =>
        Path.Combine(blockchainPath, sellerNif, $"_{sellerNif}.csv");

    // Internal format of VeriFactu 1.0.66 (Blockchain.WriteVar): id;timestamp;hash;date;NIF;number.
    // The timestamp uses the process culture, which is the one the library reads it with. The id only
    // numbers the local files; the AEAT never sees it.
    static string VarFileLine(ChainHead head) =>
        string.Join(';', "1", head.GeneratedAt.LocalDateTime.ToString(CultureInfo.CurrentCulture),
            head.Huella, head.FechaExpedicion, head.IdEmisor, head.NumSerie);
}
