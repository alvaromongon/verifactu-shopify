using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using VeriFactu.Config;
using VeriFactu.Net;

namespace VerifactuShopify;

public static class CertificateSetup
{
    public const string PathKey = "VeriFactu:CertificatePath";
    public const string PasswordKey = "VeriFactu:CertificatePassword";

    // Pasa la ruta y la contraseña del .pfx (en local, desde dotnet user-secrets) a los Settings
    // globales de VeriFactu y carga el certificado igual que lo hará la librería al enviar.
    // Así un certificado mal configurado falla aquí con un mensaje claro y no en el primer envío.
    public static X509Certificate2 Configure(IConfiguration configuration)
    {
        var path = configuration[PathKey];
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                $"Falta '{PathKey}'. En local: dotnet user-secrets set \"{PathKey}\" <ruta al .pfx> --project src/VerifactuShopify");

        // Sin esta comprobación la librería devuelve null y el error llega al enviar, sin mencionar la ruta.
        if (!File.Exists(path))
            throw new FileNotFoundException($"No existe el certificado indicado en '{PathKey}'.", path);

        Settings.Current.CertificatePath = path;
        Settings.Current.CertificatePassword = configuration[PasswordKey] ?? "";

        X509Certificate2 certificate;
        try
        {
            certificate = Wsd.GetCertificateByFile();
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"No se puede abrir el certificado: revisa '{PasswordKey}' o que el fichero sea un .pfx válido.", ex);
        }

        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException("El certificado no incluye la clave privada: exporta el .pfx con ella.");

        // La librería solo lo comprueba al enviar.
        if (certificate.NotAfter < DateTime.Now)
            throw new InvalidOperationException($"El certificado caducó el {certificate.NotAfter:yyyy-MM-dd}.");

        return certificate;
    }
}
