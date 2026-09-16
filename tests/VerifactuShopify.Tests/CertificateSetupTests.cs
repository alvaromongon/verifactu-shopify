using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using VeriFactu.Config;

namespace VerifactuShopify.Tests;

// Usa certificados autofirmados creados en el propio test. La AEAT los rechaza,
// pero sirven para probar la carga del .pfx sin un certificado real.
[Collection(VeriFactuSettingsCollection.Name)]
public sealed class CertificateSetupTests : IDisposable
{
    const string Password = "contraseña-de-prueba";

    readonly string _dir = Directory.CreateTempSubdirectory("verifactu-cert-").FullName;
    readonly string? _originalPath = Settings.Current.CertificatePath;
    readonly string? _originalPassword = Settings.Current.CertificatePassword;

    public void Dispose()
    {
        Settings.Current.CertificatePath = _originalPath;
        Settings.Current.CertificatePassword = _originalPassword;
        Directory.Delete(_dir, recursive: true);
    }

    string CreatePfx(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=PRUEBA AUTOFIRMADO", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);

        var path = Path.Combine(_dir, "prueba.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, Password));
        return path;
    }

    static IConfiguration Configuration(string? path, string? password) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CertificateSetup.PathKey] = path,
                [CertificateSetup.PasswordKey] = password,
            })
            .Build();

    [Fact]
    public void Loads_pfx_with_private_key_and_hands_it_to_the_library()
    {
        var path = CreatePfx(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));

        using var certificate = CertificateSetup.Configure(Configuration(path, Password));

        Assert.Equal("CN=PRUEBA AUTOFIRMADO", certificate.Subject);
        Assert.True(certificate.HasPrivateKey);
        Assert.Equal(path, Settings.Current.CertificatePath);
    }

    [Fact]
    public void Missing_path_explains_how_to_set_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CertificateSetup.Configure(Configuration(null, null)));

        Assert.Contains("dotnet user-secrets set", ex.Message);
    }

    [Fact]
    public void Nonexistent_file_fails_before_reaching_the_library()
    {
        var path = Path.Combine(_dir, "no-existe.pfx");

        var ex = Assert.Throws<FileNotFoundException>(() => CertificateSetup.Configure(Configuration(path, Password)));

        Assert.Equal(path, ex.FileName);
    }

    [Fact]
    public void Wrong_password_fails_without_revealing_it()
    {
        var path = CreatePfx(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));

        var ex = Assert.Throws<InvalidOperationException>(() => CertificateSetup.Configure(Configuration(path, "incorrecta")));

        Assert.Contains(CertificateSetup.PasswordKey, ex.Message);
        Assert.DoesNotContain("incorrecta", ex.Message);
    }

    [Fact]
    public void Expired_certificate_is_rejected()
    {
        var path = CreatePfx(DateTimeOffset.Now.AddDays(-10), DateTimeOffset.Now.AddDays(-1));

        var ex = Assert.Throws<InvalidOperationException>(() => CertificateSetup.Configure(Configuration(path, Password)));

        Assert.Contains("caducó", ex.Message);
    }
}
