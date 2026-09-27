using System.Globalization;

namespace VerifactuShopify.Configuration;

public static class CultureSetup
{
    public const string Culture = "es-ES";

    // The library writes and reads the blockchain dates with the process culture ("17/09/2026"), so a
    // chain written on one machine can't be read on another with different regional settings: a
    // container with the invariant culture reads that 17 as the month and fails on start. It's pinned
    // here so it doesn't depend on the environment (see #12).
    public static void Configure()
    {
        var culture = new CultureInfo(Culture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
    }
}
