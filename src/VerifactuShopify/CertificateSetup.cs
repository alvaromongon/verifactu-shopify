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
    public const string ThumbprintKey = "VeriFactu:CertificateThumbprint";

    // Pasa el certificado configurado (en local, desde dotnet user-secrets) a los Settings globales
    // de VeriFactu y lo carga igual que lo hará la librería al enviar. Así un certificado mal
    // configurado falla aquí con un mensaje claro y no en el primer envío.
    // Dos orígenes: un .pfx con su contraseña, o la huella de un certificado instalado en el almacén
    // del usuario (en macOS, el llavero), que no deja fichero ni contraseña.
    public static X509Certificate2 Configure(IConfiguration configuration)
    {
        var path = configuration[PathKey];
        var thumbprint = configuration[ThumbprintKey]?.Replace(" ", "");
        var hasPath = !string.IsNullOrWhiteSpace(path);
        var hasThumbprint = !string.IsNullOrWhiteSpace(thumbprint);

        if (hasPath && hasThumbprint)
            throw new InvalidOperationException($"Configura '{PathKey}' o '{ThumbprintKey}', no los dos.");
        if (!hasPath && !hasThumbprint)
            throw new InvalidOperationException(
                $"Falta el certificado. En local: dotnet user-secrets set \"{ThumbprintKey}\" <huella> --project src/VerifactuShopify, " +
                $"o '{PathKey}' con la ruta a un .pfx.");

        var certificate = hasThumbprint
            ? LoadFromStore(thumbprint!)
            : LoadFromFile(path!, configuration[PasswordKey]);

        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException("El certificado no incluye la clave privada.");

        // La librería solo lo comprueba al enviar.
        if (certificate.NotAfter < DateTime.Now)
            throw new InvalidOperationException($"El certificado caducó el {certificate.NotAfter:yyyy-MM-dd}.");

        return certificate;
    }

    static X509Certificate2 LoadFromStore(string thumbprint)
    {
        // La librería busca antes por ruta que por huella.
        Settings.Current.CertificatePath = "";
        Settings.Current.CertificatePassword = "";
        Settings.Current.CertificateThumbprint = thumbprint;

        X509Certificate2? certificate;
        try
        {
            certificate = Wsd.GetCertificateByThumbprint();
        }
        catch (CryptographicException ex)
        {
            // En Linux, .NET solo abre Root y CertificateAuthority en LocalMachine: si la huella no
            // está en CurrentUser, la librería falla al pasar a LocalMachine\My.
            throw NotFoundInStore(ex);
        }

        return certificate ?? throw NotFoundInStore(null);
    }

    static InvalidOperationException NotFoundInStore(Exception? inner) =>
        new($"No hay ningún certificado con la huella de '{ThumbprintKey}' en el almacén del usuario.", inner);

    static X509Certificate2 LoadFromFile(string path, string? password)
    {
        // Sin esta comprobación la librería devuelve null y el error llega al enviar, sin mencionar la ruta.
        if (!File.Exists(path))
            throw new FileNotFoundException($"No existe el certificado indicado en '{PathKey}'.", path);

        Settings.Current.CertificateThumbprint = "";
        Settings.Current.CertificatePath = path;
        Settings.Current.CertificatePassword = password ?? "";

        try
        {
            return Wsd.GetCertificateByFile();
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"No se puede abrir el certificado: revisa '{PasswordKey}' o que el fichero sea un .pfx válido.", ex);
        }
    }
}
