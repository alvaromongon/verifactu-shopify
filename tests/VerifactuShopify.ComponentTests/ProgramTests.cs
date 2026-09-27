using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using VeriFactu.Config;

using VerifactuShopify.ComponentTests.TestDoubles;

namespace VerifactuShopify.ComponentTests;

// The command line as an operator uses it. Commands that send to the AEAT are only checked up to
// the pre-production guard: their sending is covered through OrderSync against the stubs.
[Collection(VeriFactuCollection.Name)]
public sealed class ProgramTests : IDisposable
{
    const string Password = "test-password";

    readonly string _folder = Directory.CreateTempSubdirectory("verifactu-shopify-cert-").FullName;

    // A whitespace thumbprint overrides one a developer may have in user-secrets: an empty
    // environment variable would just be removed.
    static Dictionary<string, string?> Certificate(string path) => new()
    {
        ["VeriFactu__CertificatePath"] = path,
        ["VeriFactu__CertificatePassword"] = Password,
        ["VeriFactu__CertificateThumbprint"] = " ",
    };

    string CreatePfx()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=TITULAR DE PRUEBA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        var path = Path.Join(_folder, "certificado.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, Password));
        return path;
    }

    [Fact]
    public void Without_a_known_command_prints_the_usage()
    {
        var result = ConnectorProcess.Run([], "ayuda");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("sincronizar", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Certificado_shows_the_configured_certificate_without_sending_anything()
    {
        var result = ConnectorProcess.Run(Certificate(CreatePfx()), "certificado");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Titular: CN=TITULAR DE PRUEBA", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_certificate_file_fails_naming_the_setting()
    {
        var path = Path.Join(_folder, "no-existe.pfx");

        var result = ConnectorProcess.Run(Certificate(path), "certificado");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("VeriFactu:CertificatePath", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("enviar-f2")]
    [InlineData("anular", "PRE-2026-000001", "01-09-2026")]
    [InlineData("consultar", "2026", "09")]
    [InlineData("cadena")]
    [InlineData("sincronizar")]
    public void Commands_that_reach_the_aeat_refuse_an_endpoint_other_than_pre_production(params string[] args)
    {
        // Unreachable on purpose: should the guard fail, nothing leaves this machine.
        Settings.Current.VeriFactuEndPointPrefix = "http://localhost:1/not-the-aeat";

        var result = ConnectorProcess.Run([], args);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no es de preproducción", result.Error, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
