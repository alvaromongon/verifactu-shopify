using Microsoft.Extensions.Configuration;
using VeriFactu.Xml.Factu.Consulta.Respuesta;
using IDFactura = VeriFactu.Xml.Factu.Respuesta.IDFactura;

namespace VerifactuShopify.Tests;

public class SyncSettingsTests
{
    static readonly Dictionary<string, string?> Required = new()
    {
        [SyncSettings.ShopDomainKey] = "tienda.myshopify.com",
        [SyncSettings.ClientIdKey] = "client-id",
        [SyncSettings.ClientSecretKey] = "secreto",
        [SyncSettings.CutoffKey] = "2026-09-01T00:00:00+02:00",
        [SyncSettings.PrefixKey] = "PRE",
    };

    static SyncSettings From(Dictionary<string, string?> values) =>
        SyncSettings.From(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void Optional_settings_have_conservative_defaults()
    {
        var settings = From(Required);

        Assert.Equal(TimeSpan.FromMinutes(10), settings.Rule.Margin);
        Assert.Equal(400m, settings.SimplifiedInvoiceLimit);
        Assert.Equal(0, settings.LastUsedBefore(2026));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)), settings.Rule.Cutoff);
    }

    [Fact]
    public void Seed_only_applies_to_its_own_year()
    {
        var settings = From(new(Required) { [SyncSettings.SeedKey] = "2026:37" });

        Assert.Equal(37, settings.LastUsedBefore(2026));
        Assert.Equal(0, settings.LastUsedBefore(2027));
    }

    [Fact]
    public void Client_secret_can_come_from_a_secret_file()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "secreto-desde-fichero\n");
            var settings = From(new(Required) { [SyncSettings.ClientSecretKey] = null, [$"{SyncSettings.ClientSecretKey}Path"] = path });

            Assert.Equal("secreto-desde-fichero", settings.ClientSecret);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(SyncSettings.CutoffKey, "ayer")]
    [InlineData(SyncSettings.PrefixKey, "PRE-")]
    [InlineData(SyncSettings.SeedKey, "37")]
    [InlineData(SyncSettings.MarginKey, "diez")]
    public void Invalid_values_name_their_setting(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => From(new(Required) { [key] = value }));

        Assert.Contains(key, ex.Message);
    }

    [Theory]
    [InlineData(SyncSettings.ShopDomainKey)]
    [InlineData(SyncSettings.CutoffKey)]
    [InlineData(SyncSettings.PrefixKey)]
    public void Missing_required_settings_name_their_key(string key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => From(new(Required) { [key] = null }));

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void Orders_already_invoiced_are_read_from_the_AEAT_descriptions()
    {
        static RegistroRespuestaConsultaFactuSistemaFacturacion Registro(string numSerie, string? description, string estado) => new()
        {
            IDFactura = new IDFactura { NumSerieFactura = numSerie },
            DatosRegistroFacturacion = new DatosRegistroFacturacion { DescripcionOperacion = description },
            EstadoRegistro = new EstadoRegistro { EstadoReg = estado },
        };

        var invoiced = OrderSync.InvoicedOrderIds(
        [
            Registro("PRE-2026-000001", "Pedido #1289 (12901469978959): Sobres de jamón", "Correcto"),
            Registro("PRE-2026-000002", "Pedido #1290 (12903660061007): Paleta", "Anulado"),
            Registro("M1-F2-20260923132623", "Prueba M1 verifactu-shopify", "Correcto"),
            Registro("PRE-2026-000003", null, "Correcto"),
        ]);

        Assert.Equal(["12901469978959", "12903660061007"], invoiced.Order());
    }
}
