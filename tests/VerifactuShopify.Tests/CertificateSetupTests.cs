using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using VeriFactu.Config;
using VeriFactu.Net;

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
        Wsd.Certificate = null!;
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

        // La librería lo recibe ya cargado y la contraseña no queda en Settings, que es lo que
        // Settings.Save() serializaría en claro.
        Assert.Same(certificate, Wsd.Certificate);
        Assert.Equal("", Settings.Current.CertificatePassword);
    }

    [Fact]
    public void Reads_the_password_from_a_file()
    {
        var path = CreatePfx(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        var passwordPath = Path.Combine(_dir, "prueba.pass");
        File.WriteAllText(passwordPath, Password + Environment.NewLine);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CertificateSetup.PathKey] = path,
                [CertificateSetup.PasswordPathKey] = passwordPath,
            })
            .Build();

        using var certificate = CertificateSetup.Configure(configuration);

        Assert.True(certificate.HasPrivateKey);
    }

    [LinuxOnlyFact]
    public void Loading_the_pfx_writes_nothing_to_disk()
    {
        var path = CreatePfx(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        var before = Snapshot();

        using var certificate = CertificateSetup.Configure(Configuration(path, Password));

        Assert.True(certificate.HasPrivateKey);
        Assert.Empty(Snapshot().Except(before));
    }

    // El directorio temporal sin recorrer subcarpetas: es donde cae el llavero que macOS necesita
    // para cargar la clave, y recorrer /tmp entero tropieza con directorios ajenos ilegibles.
    // La carpeta de datos de la librería sí entera, que es la que no debe recibir copias.
    static string[] Snapshot()
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true };
        var temp = Directory.GetFileSystemEntries(Path.GetTempPath(), "*", options);

        options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        var data = Directory.Exists(VeriFactu.Config.Settings.Path)
            ? Directory.GetFileSystemEntries(VeriFactu.Config.Settings.Path, "*", options)
            : [];

        return [.. temp, .. data];
    }

    [Fact]
    public void Warns_before_the_certificate_expires()
    {
        var path = CreatePfx(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(CertificateSetup.WarningDays - 1));
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);

        try
        {
            using var certificate = CertificateSetup.Configure(Configuration(path, Password));
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("caduca el", error.ToString());
    }

    [Fact]
    public void Does_not_warn_for_a_certificate_with_time_left()
    {
        var path = CreatePfx(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(CertificateSetup.WarningDays + 30));
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);

        try
        {
            using var certificate = CertificateSetup.Configure(Configuration(path, Password));
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal("", error.ToString());
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
