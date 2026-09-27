using Microsoft.Extensions.Configuration;

using VeriFactu.Config;
using VeriFactu.Xml.Factu;

using VerifactuShopify.Configuration;

namespace VerifactuShopify.Aeat;

public static class SistemaInformaticoSetup
{
    public const string NifKey = "VeriFactu:SistemaInformatico:NIF";
    public const string NombreRazonKey = "VeriFactu:SistemaInformatico:NombreRazon";
    public const string NumeroInstalacionKey = "VeriFactu:SistemaInformatico:NumeroInstalacion";

    // Without Settings.xml, the library declares Irene Solutions as producer (its NIF and name) and
    // uses the machine's MAC as installation number. That data goes in every registro sent to the
    // AEAT, so it's replaced with this SIF's producer before sending anything.
    public static SistemaInformatico Configure(IConfiguration configuration)
    {
        var sistema = new SistemaInformatico
        {
            NIF = configuration.GetRequired(NifKey),
            NombreRazon = configuration.GetRequired(NombreRazonKey),
            NombreSistemaInformatico = "verifactu-shopify",
            IdSistemaInformatico = "01",
            Version = $"{typeof(SistemaInformaticoSetup).Assembly.GetName().Version}",
            NumeroInstalacion = configuration.GetRequired(NumeroInstalacionKey),
            TipoUsoPosibleSoloVerifactu = "S",
            // Each business deploys its own connector: a single taxpayer.
            TipoUsoPosibleMultiOT = "N",
            IndicadorMultiplesOT = "N",
        };

        Settings.Current.SistemaInformatico = sistema;
        return sistema;
    }
}
