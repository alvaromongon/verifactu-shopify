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

namespace VerifactuShopify;

// Último registro de la cadena de un emisor, tal como lo tiene la AEAT.
public sealed record ChainHead(
    string Huella, string IdEmisor, string NumSerie, string FechaExpedicion, DateTimeOffset GeneratedAt);

// La AEAT es la fuente de verdad de la cadena (#12): el despliegue no guarda estado, así que cada
// ejecución lee el último registro de la AEAT y continúa la cadena desde él.
public static class BlockchainSetup
{
    // La cabeza está siempre en uno de estos dos meses: el conector solo anula facturas del mes actual
    // o del anterior, y todo lo demás lleva la fecha del día. La consulta no filtra por fecha de generación.
    public static IEnumerable<PeriodoImputacion> Periods(DateTimeOffset now)
    {
        var previous = now.AddMonths(-1);
        yield return new PeriodoImputacion { Ejercicio = $"{previous:yyyy}", Periodo = $"{previous:MM}" };
        yield return new PeriodoImputacion { Ejercicio = $"{now:yyyy}", Periodo = $"{now:MM}" };
    }

    // La consulta devuelve el último registro de cada factura (alta o anulación), así que la cabeza es
    // el más reciente. Con dos generados en el mismo segundo, es el que apunta al otro.
    public static ChainHead? FindHead(IEnumerable<RegistroRespuestaConsultaFactuSistemaFacturacion> registros)
    {
        var candidates = registros
            .Where(r => r.EstadoRegistro?.EstadoReg != "Incorrecto" && r.DatosRegistroFacturacion?.Huella is not null)
            .ToList();
        if (candidates.Count == 0)
            return null;

        var latest = candidates.Max(GeneratedAt);
        var tied = candidates.Where(r => GeneratedAt(r) == latest).ToList();
        var referenced = tied.Select(r => r.DatosRegistroFacturacion.Encadenamiento?.RegistroAnterior?.Huella).ToHashSet();
        var heads = tied.Where(r => !referenced.Contains(r.DatosRegistroFacturacion.Huella)).ToList();
        if (heads.Count != 1)
            throw new InvalidOperationException(
                $"No se puede determinar el último registro de la cadena: {heads.Count} candidatos generados a las {latest:O}.");

        var head = heads[0];
        return new ChainHead(head.DatosRegistroFacturacion.Huella, head.IDFactura.IDEmisorFactura,
            head.IDFactura.NumSerieFactura, head.IDFactura.FechaExpedicionFactura, latest);
    }

    public static ChainHead? QueryHead(string sellerNif, string sellerName, SistemaInformatico sistema, DateTimeOffset now)
    {
        var query = new InvoiceQuery(sellerNif, sellerName);
        var registros = new List<RegistroRespuestaConsultaFactuSistemaFacturacion>();
        foreach (var period in Periods(now))
        {
            ClavePaginacion? next = null;
            do
            {
                var consulta = query.GetRegistro();
                consulta.FiltroConsulta.PeriodoImputacion = period;
                consulta.FiltroConsulta.ClavePaginacion = next;
                // El mismo NIF puede facturar desde otros sistemas (el TPV de Shopify, por ejemplo), cada uno
                // con su cadena. El filtro por SIF de la consulta no sirve: VeriFactu 1.0.66 serializa sus
                // campos en el espacio de nombres equivocado y la AEAT responde 4102 (mdiago/VeriFactu#292).
                // Se pide el SIF de cada registro y se filtra aquí.
                consulta.DatosAdicionalesRespuesta = new DatosAdicionalesRespuesta { MostrarSistemaInformatico = "S" };

                var (respuesta, sifs) = Send(consulta);
                var page = respuesta.RegistroRespuestaConsultaFactuSistemaFacturacion ?? [];
                if (page.Length != sifs.Count)
                    throw new InvalidOperationException(
                        $"La AEAT devolvió {page.Length} registros y {sifs.Count} sistemas informáticos: no se puede saber a qué cadena pertenece cada uno.");
                registros.AddRange(page.Where((_, i) => IsFrom(sistema, sifs[i])));
                next = respuesta.IndicadorPaginacion == "S" ? respuesta.ClavePaginacion : null;
            }
            while (next is not null);
        }

        return FindHead(registros);
    }

    // Lo mismo que InvoiceQuery.GetDocuments, pero leyendo también el SIF de cada registro: VeriFactu 1.0.66
    // lo espera en el espacio de nombres de la respuesta y la AEAT lo manda con sus campos en el de
    // SuministroInformacion, así que la librería lo deja vacío (mdiago/VeriFactu#292).
    static (RespuestaConsultaFactuSistemaFacturacion, List<XElement?>) Send(ConsultaFactuSistemaFacturacion consulta)
    {
        var xml = new XmlParser().GetBytes(new Envelope { Body = new Body { Registro = consulta } }, Namespaces.Items);
        var response = InvoiceActionMessage.SendXmlBytes(xml, "?op=ConsultaFactuSistemaFacturacion");

        var registro = Envelope.FromXml(response).Body.Registro;
        if (registro is Fault fault)
            throw new FaultException(fault);

        XNamespace respuesta = Namespaces.NamespaceTikLRRC;
        var sifs = XDocument.Parse(response)
            .Descendants(respuesta + "RegistroRespuestaConsultaFactuSistemaFacturacion")
            .Select(r => r.Descendants(respuesta + "SistemaInformatico").FirstOrDefault())
            .ToList();
        return ((RespuestaConsultaFactuSistemaFacturacion)registro, sifs);
    }

    // Un SIF se identifica por productor, id y número de instalación; la versión no cuenta.
    public static bool IsFrom(SistemaInformatico sistema, XElement? sif)
    {
        XNamespace sf = Namespaces.NamespaceSF;
        var nif = sif?.Element(sf + "NIF")?.Value;
        var id = sif?.Element(sf + "IdSistemaInformatico")?.Value;
        var instalacion = sif?.Element(sf + "NumeroInstalacion")?.Value;

        // Sin SIF, descartar el registro haría creer que la cadena está vacía y empezaría otra.
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(instalacion))
            throw new InvalidOperationException("La AEAT no devolvió el sistema informático de un registro: no se puede saber a qué cadena pertenece.");

        return nif == sistema.NIF && id == sistema.IdSistemaInformatico && instalacion == sistema.NumeroInstalacion;
    }

    // Deja la cadena del emisor en la librería apuntando a la cabeza de la AEAT. Hay que llamarlo antes
    // de crear ningún InvoiceEntry: la librería lee la cadena del disco una sola vez, al iniciarse. Sin
    // una forma pública de fijar la cabeza, se escribe su fichero interno (propuesta en mdiago/VeriFactu#293).
    public static void Load(string sellerNif, ChainHead? head)
    {
        var blockchainPath = Settings.Current.BlockchainPath;
        var varFile = VarFileName(blockchainPath, sellerNif);

        // Ya había cadena en disco (desarrollo local): se cargó al iniciarse y solo se comprueba.
        if (File.Exists(varFile))
        {
            var local = Blockchain.Get(sellerNif).Current?.Huella;
            if (local != head?.Huella)
                throw new InvalidOperationException(
                    $"La cadena local ({local}) no coincide con la de la AEAT ({head?.Huella ?? "vacía"}). No se envía nada.");
            return;
        }

        // La AEAT no tiene nada: el siguiente registro será el primero.
        if (head is null)
            return;

        // LoadBlockchainsFromDisk falla con cualquier carpeta de emisor que ya estuviera cargada, aunque no
        // tenga cadena. Sin estado, la carpeta de datos arranca vacía.
        var existing = Directory.GetDirectories(blockchainPath).Select(Path.GetFileName).ToList();
        if (existing.Count > 0)
            throw new InvalidOperationException(
                $"Para cargar la cadena desde la AEAT, {blockchainPath} tiene que estar vacía. Contiene: {string.Join(", ", existing)}.");

        Directory.CreateDirectory(Path.GetDirectoryName(varFile)!);
        File.WriteAllText(varFile, VarFileLine(head));
        Blockchain.LoadBlockchainsFromDisk();

        var loaded = Blockchain.Get(sellerNif).Current?.Huella;
        if (loaded != head.Huella)
            throw new InvalidOperationException(
                $"La librería no cargó la cabeza de la AEAT ({head.Huella}), sino {loaded ?? "nada"}: ¿ha cambiado el formato de {varFile}?");
    }

    static DateTimeOffset GeneratedAt(RegistroRespuestaConsultaFactuSistemaFacturacion registro) =>
        DateTimeOffset.Parse(registro.DatosRegistroFacturacion.FechaHoraHusoGenRegistro, CultureInfo.InvariantCulture);

    static string VarFileName(string blockchainPath, string sellerNif) =>
        Path.Combine(blockchainPath, sellerNif, $"_{sellerNif}.csv");

    // Formato interno de VeriFactu 1.0.66 (Blockchain.WriteVar): id;marca de tiempo;huella;fecha;NIF;número.
    // La marca de tiempo va con la cultura del proceso, que es con la que la librería la lee. El id solo
    // numera los ficheros locales; la AEAT no lo ve.
    static string VarFileLine(ChainHead head) =>
        string.Join(';', "1", head.GeneratedAt.LocalDateTime.ToString(CultureInfo.CurrentCulture),
            head.Huella, head.FechaExpedicion, head.IdEmisor, head.NumSerie);
}
