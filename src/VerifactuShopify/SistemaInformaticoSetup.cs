using Microsoft.Extensions.Configuration;
using VeriFactu.Config;
using VeriFactu.Xml.Factu;

namespace VerifactuShopify;

public static class SistemaInformaticoSetup
{
    public const string NifKey = "VeriFactu:SistemaInformatico:NIF";
    public const string NombreRazonKey = "VeriFactu:SistemaInformatico:NombreRazon";
    public const string NumeroInstalacionKey = "VeriFactu:SistemaInformatico:NumeroInstalacion";

    // Sin Settings.xml, la librería declara como productor a Irene Solutions (su NIF y su nombre) y usa
    // la MAC del equipo como número de instalación. Esos datos van en cada registro enviado a la AEAT,
    // así que se sustituyen por los del productor de este SIF antes de enviar nada.
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
            // Cada empresa despliega su propio conector: un único obligado tributario.
            TipoUsoPosibleMultiOT = "N",
            IndicadorMultiplesOT = "N",
        };

        Settings.Current.SistemaInformatico = sistema;
        return sistema;
    }
}
