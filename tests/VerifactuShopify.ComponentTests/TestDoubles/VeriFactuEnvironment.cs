using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using VeriFactu.Config;
using VeriFactu.Net;

using VerifactuShopify.Configuration;

namespace VerifactuShopify.ComponentTests.TestDoubles;

// VeriFactu keeps its settings, certificate and chains in static state: one environment per test
// run, shared by the tests of the collection, which never run in parallel. Its data folders go to a
// temporary directory, except the chains folder: the library refuses to move it once it holds a
// chain, as on a developer machine. There, tests only use fictitious sellers and delete their chains.
public sealed class VeriFactuEnvironment : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("verifactu-shopify-").FullName;

    public VeriFactuEnvironment()
    {
        CultureSetup.Configure();
        Settings.Current.InboxPath = Folder("Inbox");
        Settings.Current.OutboxPath = Folder("Outbox");
        Settings.Current.InvoicePath = Folder("Invoices");
        Settings.Current.LogPath = Folder("Log");

        // Self-signed: good enough for the stub, which doesn't check it.
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=EMISOR DE PRUEBA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Wsd.Certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
    }

    // Removes a fictitious seller's chain, on disk and wherever else the library keeps it.
    public static void DeleteChain(string sellerNif)
    {
        var chain = Path.Join(Settings.Current.BlockchainPath, sellerNif);
        if (Directory.Exists(chain))
        {
            Directory.Delete(chain, recursive: true);
        }
    }

    string Folder(string name) => Directory.CreateDirectory(Path.Join(_root, name)).FullName + Path.DirectorySeparatorChar;

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VeriFactuCollection : ICollectionFixture<VeriFactuEnvironment>
{
    public const string Name = "VeriFactu";
}
