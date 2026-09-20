using System.Globalization;

namespace VerifactuShopify;

public static class CultureSetup
{
    public const string Culture = "es-ES";

    // La librería escribe y lee las fechas de la cadena de bloques con la cultura del proceso
    // ("17/09/2026"), así que una cadena escrita en una máquina no se puede leer en otra con distinta
    // configuración regional: un contenedor con cultura invariante interpreta ese 17 como mes y falla
    // al iniciarse. Se fija aquí para que no dependa del entorno (ver #12).
    public static void Configure()
    {
        var culture = new CultureInfo(Culture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
    }
}
