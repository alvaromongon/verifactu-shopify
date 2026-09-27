using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Configuration;

using VeriFactu.Config;
using VeriFactu.Net;

namespace VerifactuShopify.Aeat;

public static class CertificateSetup
{
    public const string PathKey = "VeriFactu:CertificatePath";
    public const string PasswordKey = "VeriFactu:CertificatePassword";
    public const string PasswordPathKey = "VeriFactu:CertificatePasswordPath";
    public const string ThumbprintKey = "VeriFactu:CertificateThumbprint";

    public const int WarningDays = 30;

    // Hands the configured certificate to the library and loads it here, so a misconfigured
    // certificate fails with a clear message rather than on the first submission.
    // Two sources: a .pfx with its password (the deployment one, delivered as a secret), or the
    // thumbprint of a certificate installed in the user store (local development only).
    // Either way the certificate goes to Wsd.Certificate, the first thing the library looks at:
    // the password never gets into Settings, which Settings.Save() serializes in plain text.
    public static X509Certificate2 Configure(IConfiguration configuration)
    {
        var path = configuration[PathKey];
        var thumbprint = configuration[ThumbprintKey]?.Replace(" ", "");
        var hasPath = !string.IsNullOrWhiteSpace(path);
        var hasThumbprint = !string.IsNullOrWhiteSpace(thumbprint);

        if (hasPath && hasThumbprint)
        {
            throw new InvalidOperationException($"Configura '{PathKey}' o '{ThumbprintKey}', no los dos.");
        }

        if (!hasPath && !hasThumbprint)
        {
            throw new InvalidOperationException(
                $"Falta el certificado. En local: dotnet user-secrets set \"{ThumbprintKey}\" <huella> --project src/VerifactuShopify, " +
                $"o '{PathKey}' con la ruta a un .pfx.");
        }

        var certificate = hasThumbprint
            ? LoadFromStore(thumbprint!)
            : LoadFromFile(path!, configuration);

        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException("El certificado no incluye la clave privada.");
        }

        // The library only checks it when sending.
        if (certificate.NotAfter < DateTime.Now)
        {
            throw new InvalidOperationException($"El certificado caducó el {certificate.NotAfter:yyyy-MM-dd}.");
        }

        Warn(certificate);

        Wsd.Certificate = certificate;
        return certificate;
    }

    // The AEAT gives no expiry warning and renewing a certificate takes paperwork, so the warning
    // goes to stderr without failing the run: a submission doesn't stop for this.
    static void Warn(X509Certificate2 certificate)
    {
        var days = (int)(certificate.NotAfter - DateTime.Now).TotalDays;
        if (days <= WarningDays)
        {
            Console.Error.WriteLine(
                $"Aviso: el certificado caduca el {certificate.NotAfter:yyyy-MM-dd}, {(days == 1 ? "queda 1 día" : $"quedan {days} días")}.");
        }
    }

    static X509Certificate2 LoadFromStore(string thumbprint)
    {
        // The library looks up by path before thumbprint.
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
            // On Linux, .NET only opens Root and CertificateAuthority in LocalMachine: if the thumbprint
            // isn't in CurrentUser, the library fails when it falls back to LocalMachine\My.
            throw NotFoundInStore(ex);
        }

        return certificate ?? throw NotFoundInStore(null);
    }

    static InvalidOperationException NotFoundInStore(Exception? inner) =>
        new($"No hay ningún certificado con la huella de '{ThumbprintKey}' en el almacén del usuario.", inner);

    // The .pfx is read into memory and kept out of Settings: the library gets it already loaded.
    static X509Certificate2 LoadFromFile(string path, IConfiguration configuration)
    {
        // Without this check the library returns null and the error surfaces when sending, without the path.
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No existe el certificado indicado en '{PathKey}'.", path);
        }

        // Settings is left alone: Wsd.Certificate takes precedence over path and thumbprint, and the
        // password stays out of the object Settings.Save() would serialize in plain text. Besides,
        // touching Settings.Current would run its static constructor, which creates the data folders
        // and starts the blockchain: checking a certificate has no reason to write anything.
        var data = File.ReadAllBytes(path);
        var password = ReadPassword(configuration);

        try
        {
            return X509CertificateLoader.LoadPkcs12(data, password, KeyStorageFlags);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"No se puede abrir el certificado: revisa '{PasswordKey}' o que el fichero sea un .pfx válido.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            password.Clear();
        }
    }

    // macOS can't load a private key without a keychain, which means writing to disk, and Windows
    // doesn't authenticate with ephemeral keys. On Linux, where it's deployed, the key stays in
    // memory and never touches the disk.
    static X509KeyStorageFlags KeyStorageFlags => OperatingSystem.IsLinux()
        ? X509KeyStorageFlags.EphemeralKeySet
        : X509KeyStorageFlags.DefaultKeySet;

    // From a file, so the password doesn't go through the process environment, which leaks more
    // easily. A plain value still works locally.
    static Span<char> ReadPassword(IConfiguration configuration)
    {
        var passwordPath = configuration[PasswordPathKey];
        if (string.IsNullOrWhiteSpace(passwordPath))
        {
            return configuration[PasswordKey]?.ToCharArray() ?? [];
        }

        if (!File.Exists(passwordPath))
        {
            throw new FileNotFoundException($"No existe el fichero de contraseña indicado en '{PasswordPathKey}'.", passwordPath);
        }

        return File.ReadAllText(passwordPath).TrimEnd('\r', '\n').ToCharArray();
    }
}
