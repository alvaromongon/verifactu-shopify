using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using VeriFactu.Config;

namespace VerifactuShopify.Tests;

// Usa certificados autofirmados creados en el propio test. La AEAT los rechaza,
// pero sirven para probar la carga del .pfx sin un certificado real.
// No instala nada en el almacén de certificados del usuario.
[Collection(VeriFactuSettingsCollection.Name)]
public sealed class CertificateSetupTests : IDisposable
{
    const string Password = "contraseña-de-prueba";

    readonly string _dir = Directory.CreateTempSubdirectory("verifactu-cert-").FullName;
    readonly string? _originalPath = Settings.Current.CertificatePath;
    readonly string? _originalPassword = Settings.Current.CertificatePassword;
    readonly string? _originalThumbprint = Settings.Current.CertificateThumbprint;

    public void Dispose()
    {
        Settings.Current.CertificatePath = _originalPath;
        Settings.Current.CertificatePassword = _originalPassword;
        Settings.Current.CertificateThumbprint = _originalThumbprint;
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

    static IConfiguration Configuration(string? path, string? password, string? thumbprint = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CertificateSetup.PathKey] = path,
                [CertificateSetup.PasswordKey] = password,
                [CertificateSetup.ThumbprintKey] = thumbprint,
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
        Assert.Equal("", Settings.Current.CertificateThumbprint);
    }

    [Fact]
    public void Missing_certificate_explains_how_to_set_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CertificateSetup.Configure(Configuration(null, null)));

        Assert.Contains("dotnet user-secrets set", ex.Message);
    }

    [Fact]
    public void Path_and_thumbprint_together_are_rejected()
    {
        var path = CreatePfx(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));

        var ex = Assert.Throws<InvalidOperationException>(
            () => CertificateSetup.Configure(Configuration(path, Password, new string('0', 40))));

        Assert.Contains("no los dos", ex.Message);
    }

    [Fact]
    public void Unknown_thumbprint_fails_clearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CertificateSetup.Configure(Configuration(null, null, new string('0', 40))));

        Assert.Contains(CertificateSetup.ThumbprintKey, ex.Message);
        Assert.Equal("", Settings.Current.CertificatePath);
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
