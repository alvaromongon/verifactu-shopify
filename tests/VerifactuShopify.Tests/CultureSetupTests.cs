using System.Globalization;

namespace VerifactuShopify.Tests;

// La cadena de bloques se lee con la cultura del proceso. Si depende del entorno, una cadena escrita
// en una máquina no se lee en otra: un contenedor con cultura invariante falla al iniciarse.
public sealed class CultureSetupTests
{
    [Fact]
    public void Pins_the_culture_that_reads_the_blockchain()
    {
        CultureSetup.Configure();

        Assert.Equal(CultureSetup.Culture, CultureInfo.CurrentCulture.Name);

        // La fecha tal y como la escribe la librería en los CSV de la cadena.
        Assert.Equal(new DateTime(2026, 9, 17, 0, 25, 22), DateTime.Parse("17/09/2026 00:25:22"));
    }
}
