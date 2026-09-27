using Microsoft.Extensions.Configuration;

using VerifactuShopify.Aeat;
using VerifactuShopify.Sync;

namespace VerifactuShopify.ComponentTests.TestDoubles;

// The configuration a deployment passes as environment variables, for the stubbed shop.
public static class SyncConfiguration
{
    public static IConfiguration Create(string sellerNif, Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["VeriFactu:Emisor:NIF"] = sellerNif,
            ["VeriFactu:Emisor:Nombre"] = "EMISOR DE PRUEBA",
            [SistemaInformaticoSetup.NifKey] = "12345678Z",
            [SistemaInformaticoSetup.NombreRazonKey] = "PRODUCTOR DE PRUEBA",
            [SistemaInformaticoSetup.NumeroInstalacionKey] = "tienda-1",
            [SyncSettings.ShopDomainKey] = ShopifyStub.Shop,
            [SyncSettings.ClientIdKey] = "client-id",
            [SyncSettings.ClientSecretKey] = "client-secret",
            [SyncSettings.CutoffKey] = "2026-09-01T00:00:00+02:00",
            [SyncSettings.PrefixKey] = "PRE",
        };
        foreach (var (key, value) in overrides ?? [])
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
