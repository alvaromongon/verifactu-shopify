namespace VerifactuShopify.UnitTests.TestDoubles;

// VeriFactu.Config.Settings is static and global: tests using it can't run in parallel.
// On start it creates its folders in a fixed path per OS
// (macOS: ~/Library/Application Support/VeriFactu; Linux: /usr/share/VeriFactu).
[CollectionDefinition(Name, DisableParallelization = true)]
public class VeriFactuSettingsCollection
{
    public const string Name = "VeriFactu Settings";
}
