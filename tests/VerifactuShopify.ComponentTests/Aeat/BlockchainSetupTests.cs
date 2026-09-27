using VeriFactu.Common.Exceptions;

using VerifactuShopify.Aeat;
using VerifactuShopify.ComponentTests.TestDoubles;

using SistemaInformatico = VeriFactu.Xml.Factu.SistemaInformatico;

namespace VerifactuShopify.ComponentTests.Aeat;

// Queries to the AEAT over SOAP, against the stub.
[Collection(VeriFactuCollection.Name)]
public sealed class BlockchainSetupTests : IDisposable
{
    const string SellerNif = "00000010X";
    static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(2));

    static readonly SistemaInformatico Sistema = new()
    {
        NIF = "12345678Z",
        NombreRazon = "PRODUCTOR DE PRUEBA",
        IdSistemaInformatico = "01",
        NumeroInstalacion = "tienda-1",
        Version = "1.0.0.0",
    };

    readonly AeatStub _aeat = new();

    [Fact]
    public void Every_page_of_a_period_is_read()
    {
        _aeat.GivenPagedRecords(Sistema,
            Registros.Alta(SellerNif, "PRE-2026-000001", "Pedido #1001 (1): Jamón", "HUELLA-1"),
            Registros.Alta(SellerNif, "PRE-2026-000002", "Pedido #1002 (2): Jamón", "HUELLA-2"));

        var registros = BlockchainSetup.QueryRecords(SellerNif, "EMISOR DE PRUEBA", Sistema, [BlockchainSetup.Period(Now)]);

        Assert.Equal(["PRE-2026-000001", "PRE-2026-000002"], registros.Select(r => r.IDFactura.NumSerieFactura));
        Assert.Equal(2, _aeat.Consultas());
    }

    [Fact]
    public void A_soap_fault_stops_the_query()
    {
        _aeat.GivenFault("Codigo[4102].El XML no cumple el esquema.");

        var error = Assert.Throws<FaultException>(() =>
            BlockchainSetup.QueryRecords(SellerNif, "EMISOR DE PRUEBA", Sistema, [BlockchainSetup.Period(Now)]));

        Assert.Contains("4102", error.Message, StringComparison.Ordinal);
    }

    public void Dispose() => _aeat.Dispose();
}
