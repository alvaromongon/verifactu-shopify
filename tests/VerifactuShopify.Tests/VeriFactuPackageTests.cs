using System.Reflection;

namespace VerifactuShopify.Tests;

public class VeriFactuPackageTests
{
    // Solo se usan versiones de mdiago/VeriFactu con declaración responsable publicada
    // (NetFramework/Doc/Legal en el repositorio original). Antes de subir de versión,
    // comprueba que existe la declaración de la nueva y actualiza este test.
    [Fact]
    public void Pinned_to_version_with_published_declaracion_responsable()
    {
        var version = typeof(VeriFactu.Business.Invoice).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0];

        Assert.Equal("1.0.66", version);
    }
}
