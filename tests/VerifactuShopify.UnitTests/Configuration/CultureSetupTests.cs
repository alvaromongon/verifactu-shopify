using System.Globalization;

using VerifactuShopify.Configuration;

namespace VerifactuShopify.UnitTests.Configuration;

// The blockchain is read with the process culture. If it depends on the environment, a chain written
// on one machine can't be read on another: a container with the invariant culture fails on start.
public sealed class CultureSetupTests
{
    [Fact]
    public void Pins_the_culture_that_reads_the_blockchain()
    {
        CultureSetup.Configure();

        Assert.Equal(CultureSetup.Culture, CultureInfo.CurrentCulture.Name);

        // The date as the library writes it in the chain CSV files.
        Assert.Equal(new DateTime(2026, 9, 17, 0, 25, 22), DateTime.Parse("17/09/2026 00:25:22", CultureInfo.CurrentCulture));
    }
}
