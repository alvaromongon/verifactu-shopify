using VeriFactu.Xml.Factu.Consulta.Respuesta;

using IDFactura = VeriFactu.Xml.Factu.Respuesta.IDFactura;

namespace VerifactuShopify.ComponentTests.TestDoubles;

// Registros as the AEAT returns them when queried.
public static class Registros
{
    public static RegistroRespuestaConsultaFactuSistemaFacturacion Alta(
        string sellerNif, string numSerie, string descripcion, string huella = "HUELLA-DE-LA-AEAT") => new()
        {
            IDFactura = new IDFactura { IDEmisorFactura = sellerNif, NumSerieFactura = numSerie, FechaExpedicionFactura = "22-09-2026" },
            DatosRegistroFacturacion = new DatosRegistroFacturacion
            {
                DescripcionOperacion = descripcion,
                Huella = huella,
                FechaHoraHusoGenRegistro = "2026-09-22T10:00:00+02:00",
                Encadenamiento = new Encadenamiento { PrimerRegistro = "S" },
            },
            EstadoRegistro = new EstadoRegistro { EstadoReg = "Correcto" },
        };
}
