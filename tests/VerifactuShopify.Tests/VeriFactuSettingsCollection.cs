namespace VerifactuShopify.Tests;

// VeriFactu.Config.Settings es estático y global: los tests que lo usan no pueden correr en paralelo.
// Al iniciarse crea sus carpetas en una ruta fija por sistema operativo
// (macOS: ~/Library/Application Support/VeriFactu; Linux: /usr/share/VeriFactu).
[CollectionDefinition(Name, DisableParallelization = true)]
public class VeriFactuSettingsCollection
{
    public const string Name = "VeriFactu Settings";
}
