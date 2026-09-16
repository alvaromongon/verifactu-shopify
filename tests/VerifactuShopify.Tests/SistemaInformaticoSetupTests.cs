using Microsoft.Extensions.Configuration;
using VeriFactu.Business;
using VeriFactu.Config;
using VeriFactu.Xml.Factu;
using VeriFactu.Xml.Factu.Alta;

namespace VerifactuShopify.Tests;

[Collection(VeriFactuSettingsCollection.Name)]
public sealed class SistemaInformaticoSetupTests : IDisposable
{
    readonly SistemaInformatico _original = Settings.Current.SistemaInformatico;

    public void Dispose() => Settings.Current.SistemaInformatico = _original;

    static IConfiguration Configuration(string? nif) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SistemaInformaticoSetup.NifKey] = nif,
                [SistemaInformaticoSetup.NombreRazonKey] = "PRODUCTOR DE PRUEBA",
                [SistemaInformaticoSetup.NumeroInstalacionKey] = "tests",
            })
            .Build();

    [Fact]
    public void Records_carry_our_producer_instead_of_the_library_default()
    {
        SistemaInformaticoSetup.Configure(Configuration("12345678Z"));

        var registro = new Invoice("M1-F2-0002", new DateTime(2026, 9, 17), "12345678Z")
        {
            InvoiceType = TipoFactura.F2,
            SellerName = "EMISOR DE PRUEBA",
            Text = "Pedido de prueba",
            TaxItems = [new TaxItem { TaxRate = 21, TaxBase = 100, TaxAmount = 21 }],
        }.GetRegistroAlta();

        Assert.Equal("12345678Z", registro.SistemaInformatico.NIF);
        Assert.Equal("PRODUCTOR DE PRUEBA", registro.SistemaInformatico.NombreRazon);
        Assert.Equal("verifactu-shopify", registro.SistemaInformatico.NombreSistemaInformatico);
        Assert.Equal("tests", registro.SistemaInformatico.NumeroInstalacion);
    }

    [Fact]
    public void Missing_nif_explains_how_to_set_it_and_keeps_current_settings()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SistemaInformaticoSetup.Configure(Configuration(null)));

        Assert.Contains(SistemaInformaticoSetup.NifKey, ex.Message);
        Assert.Same(_original, Settings.Current.SistemaInformatico);
    }
}
