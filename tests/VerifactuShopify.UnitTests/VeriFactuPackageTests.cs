using System.Reflection;


namespace VerifactuShopify.UnitTests;

public class VeriFactuPackageTests
{
    // Only mdiago/VeriFactu versions with a published responsible declaration are used
    // (NetFramework/Doc/Legal in the original repository). Before bumping the version, check that
    // the new one has its declaration and update this test.
    [Fact]
    public void Pinned_to_version_with_published_declaracion_responsable()
    {
        var version = typeof(VeriFactu.Business.Invoice).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0];

        Assert.Equal("1.0.66", version);
    }
}
