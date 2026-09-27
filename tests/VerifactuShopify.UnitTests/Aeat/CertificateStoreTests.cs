using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Configuration;

using VeriFactu.Config;

using VerifactuShopify.Aeat;
using VerifactuShopify.UnitTests.TestDoubles;

namespace VerifactuShopify.UnitTests.Aeat;

// Lookup by thumbprint in the user's certificate store, where the certificate comes from locally
// (on macOS, the keychain). Installs a self-signed one and removes it afterwards.
[Collection(VeriFactuSettingsCollection.Name)]
public sealed class CertificateStoreTests : IDisposable
{
    readonly string? _originalPath = Settings.Current.CertificatePath;
    readonly string? _originalPassword = Settings.Current.CertificatePassword;
    readonly string? _originalThumbprint = Settings.Current.CertificateThumbprint;
    X509Certificate2? _installed;

    public void Dispose()
    {
        if (_installed is not null)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(_installed);
            _installed.Dispose();
        }

        Settings.Current.CertificatePath = _originalPath;
        Settings.Current.CertificatePassword = _originalPassword;
        Settings.Current.CertificateThumbprint = _originalThumbprint;
    }

    [CiOnlyFact]
    public void Finds_a_certificate_installed_in_the_user_store()
    {
        _installed = Install();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CertificateSetup.ThumbprintKey] = _installed.Thumbprint,
            })
            .Build();

        using var certificate = CertificateSetup.Configure(configuration);

        Assert.Equal(_installed.Thumbprint, certificate.Thumbprint);
        Assert.True(certificate.HasPrivateKey);
    }

    static X509Certificate2 Install()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=PRUEBA ALMACEN", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));

        // The private key only stays in the store if the certificate is imported from a PKCS#12
        // asking for it to be persisted.
        var certificate = X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
        return certificate;
    }
}
