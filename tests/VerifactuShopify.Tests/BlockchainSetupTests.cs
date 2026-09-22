using System.Xml.Linq;
using VeriFactu.Blockchain;
using VeriFactu.Business;
using VeriFactu.Config;
using VeriFactu.Xml;
using SistemaInformatico = VeriFactu.Xml.Factu.SistemaInformatico;
using VeriFactu.Xml.Factu.Alta;
using VeriFactu.Xml.Factu.Consulta.Respuesta;
using IDFactura = VeriFactu.Xml.Factu.Respuesta.IDFactura;

namespace VerifactuShopify.Tests;

[Collection(VeriFactuSettingsCollection.Name)]
public sealed class BlockchainSetupTests
{
    const string SellerNif = "99999999R";

    static RegistroRespuestaConsultaFactuSistemaFacturacion Registro(
        string numSerie, string huella, string generado, string? anterior = null, string estado = "Correcto") => new()
    {
        IDFactura = new IDFactura { IDEmisorFactura = SellerNif, NumSerieFactura = numSerie, FechaExpedicionFactura = "22-09-2026" },
        DatosRegistroFacturacion = new DatosRegistroFacturacion
        {
            Huella = huella,
            FechaHoraHusoGenRegistro = generado,
            Encadenamiento = anterior is null
                ? new Encadenamiento { PrimerRegistro = "S" }
                : new Encadenamiento { RegistroAnterior = new RegistroAnterior { Huella = anterior } },
        },
        EstadoRegistro = new EstadoRegistro { EstadoReg = estado },
    };

    [Fact]
    public void Without_records_there_is_no_head()
    {
        Assert.Null(BlockchainSetup.FindHead([]));
    }

    [Fact]
    public void Head_is_the_latest_generated_record_whatever_the_time_zone()
    {
        var head = BlockchainSetup.FindHead(
        [
            // Anulada: la consulta devuelve el registro de anulación, generado después que el alta siguiente.
            Registro("A-1", "HUELLA-ANULACION-1", "2026-09-22T12:40:21+02:00", anterior: "HUELLA-2"),
            Registro("A-2", "HUELLA-2", "2026-09-22T10:40:15Z", anterior: "HUELLA-ALTA-1"),
        ]);

        Assert.Equal("HUELLA-ANULACION-1", head?.Huella);
        Assert.Equal("A-1", head?.NumSerie);
    }

    [Fact]
    public void Rejected_records_are_not_part_of_the_chain()
    {
        var head = BlockchainSetup.FindHead(
        [
            Registro("A-1", "HUELLA-1", "2026-09-22T12:00:00+02:00"),
            Registro("A-2", "HUELLA-2", "2026-09-22T12:05:00+02:00", anterior: "HUELLA-1", estado: "Incorrecto"),
        ]);

        Assert.Equal("HUELLA-1", head?.Huella);
    }

    [Fact]
    public void Records_generated_in_the_same_second_are_ordered_by_their_chaining()
    {
        var head = BlockchainSetup.FindHead(
        [
            Registro("A-2", "HUELLA-2", "2026-09-22T12:00:00+02:00", anterior: "HUELLA-1"),
            Registro("A-1", "HUELLA-1", "2026-09-22T12:00:00+02:00"),
        ]);

        Assert.Equal("HUELLA-2", head?.Huella);
    }

    static readonly SistemaInformatico Sistema = new()
    {
        NIF = "12345678Z", IdSistemaInformatico = "01", NumeroInstalacion = "tienda-1", Version = "1.0.0.0",
    };

    // Como lo manda la AEAT: el bloque en el espacio de nombres de la respuesta y sus campos en el de SuministroInformacion.
    static XElement Sif(string nif, string id, string instalacion, string version = "0.9.0.0")
    {
        XNamespace respuesta = Namespaces.NamespaceTikLRRC;
        XNamespace sf = Namespaces.NamespaceSF;
        return new XElement(respuesta + "SistemaInformatico",
            new XElement(sf + "NombreRazon", "PRODUCTOR"), new XElement(sf + "NIF", nif),
            new XElement(sf + "IdSistemaInformatico", id), new XElement(sf + "Version", version),
            new XElement(sf + "NumeroInstalacion", instalacion));
    }

    [Fact]
    public void A_record_belongs_to_our_chain_whatever_version_produced_it()
    {
        Assert.True(BlockchainSetup.IsFrom(Sistema, Sif("12345678Z", "01", "tienda-1")));
    }

    [Fact]
    public void Records_from_another_installation_or_producer_are_another_chain()
    {
        Assert.False(BlockchainSetup.IsFrom(Sistema, Sif("12345678Z", "01", "tpv")));
        Assert.False(BlockchainSetup.IsFrom(Sistema, Sif("87654321X", "01", "tienda-1")));
    }

    [Fact]
    public void A_record_without_its_system_stops_instead_of_starting_another_chain()
    {
        Assert.Throws<InvalidOperationException>(() => BlockchainSetup.IsFrom(Sistema, null));
    }

    [Fact]
    public void Queries_the_current_and_previous_month_across_the_new_year()
    {
        var periods = BlockchainSetup.Periods(new DateTimeOffset(2027, 1, 5, 10, 0, 0, TimeSpan.FromHours(1)))
            .Select(p => $"{p.Ejercicio}-{p.Periodo}");

        Assert.Equal(["2026-12", "2027-01"], periods);
    }

    // Fija el formato interno del fichero de cabeza de VeriFactu: si cambia al subir de versión, falla aquí
    // y no en un envío real encadenado a la nada.
    [EmptyBlockchainFolderFact]
    public void Next_record_is_chained_to_the_head_loaded_from_the_aeat()
    {
        CultureSetup.Configure();
        var head = new ChainHead("78CAE8557088F0E2AEA2E6600A59927A0E3ED2484CE08A15783541D7079999E7",
            SellerNif, "M1-F2-0001", "20-09-2026", new DateTimeOffset(2026, 9, 20, 12, 12, 5, TimeSpan.FromHours(2)));

        try
        {
            BlockchainSetup.Load(SellerNif, head);

            var registro = new Invoice("M1-F2-0002", new DateTime(2026, 9, 22), SellerNif)
            {
                InvoiceType = TipoFactura.F2,
                SellerName = "EMISOR DE PRUEBA",
                Text = "Pedido de prueba",
                TaxItems = [new TaxItem { TaxRate = 21, TaxBase = 100, TaxAmount = 21 }],
            }.GetRegistroAlta();
            Blockchain.Get(SellerNif).Add(registro);

            var anterior = registro.Encadenamiento.RegistroAnterior;
            Assert.Equal(head.Huella, anterior.Huella);
            Assert.Equal(head.NumSerie, anterior.NumSerieFactura);
            Assert.Equal(head.FechaExpedicion, anterior.FechaExpedicionFactura);
            Assert.Equal(SellerNif, anterior.IDEmisorFactura);
        }
        finally
        {
            Directory.Delete(Path.Combine(Settings.Current.BlockchainPath, SellerNif), recursive: true);
        }
    }
}
