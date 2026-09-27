using System.Globalization;
using System.Web;

using VeriFactu.Business;
using VeriFactu.Config;
using VeriFactu.Xml.Factu.Alta;

using VerifactuShopify.UnitTests.TestDoubles;

namespace VerifactuShopify.UnitTests.Invoicing;

// Builds an F2 locally without sending it: touches neither the blockchain nor the AEAT.
[Collection(VeriFactuSettingsCollection.Name)]
public class SimplifiedInvoiceTests
{
    // 12345678Z: fictitious NIF with a valid check letter.
    static Invoice CreateF2() => new("M1-F2-0001", new DateTime(2026, 9, 15), "12345678Z")
    {
        InvoiceType = TipoFactura.F2,
        SellerName = "EMISOR DE PRUEBA",
        Text = "Pedido de prueba",
        TaxItems = [new TaxItem { TaxRate = 21, TaxBase = 100, TaxAmount = 21 }],
    };

    // The package has no net10.0 build: its net8.0 one runs.
    [Fact]
    public void Library_initializes_on_net10()
    {
        Assert.Equal(10, Environment.Version.Major);
        Assert.NotNull(Settings.Current);
    }

    [Fact]
    public void F2_without_buyer_has_no_recipients_and_totals()
    {
        var registro = CreateF2().GetRegistroAlta();

        Assert.Equal(TipoFactura.F2, registro.TipoFactura);
        Assert.Null(registro.Destinatarios);
        Assert.Equal(21m, decimal.Parse(registro.CuotaTotal, CultureInfo.InvariantCulture));
        Assert.Equal(121m, decimal.Parse(registro.ImporteTotal, CultureInfo.InvariantCulture));
    }

    // AEAT format for the QR: date DD-MM-YYYY and amount with a decimal point.
    [Fact]
    public void Validation_url_points_to_preproduccion_with_invoice_data()
    {
        var url = new Uri(CreateF2().GetRegistroAlta().GetUrlValidate());
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("https://prewww2.aeat.es/wlpl/TIKE-CONT/ValidarQR", url.GetLeftPart(UriPartial.Path));
        Assert.Equal("12345678Z", query["nif"]);
        Assert.Equal("M1-F2-0001", query["numserie"]);
        Assert.Equal("15-09-2026", query["fecha"]);
        Assert.Equal(121m, decimal.Parse(query["importe"]!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Qr_image_is_generated()
    {
        var image = CreateF2().GetRegistroAlta().GetValidateQr();

        Assert.NotEmpty(image);
        Assert.Equal("BM"u8.ToArray(), image[..2]);
    }
}
