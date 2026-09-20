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
    public const string PasswordPathKey = "VeriFactu:CertificatePasswordPath";
    public const string ThumbprintKey = "VeriFactu:CertificateThumbprint";

    public const int WarningDays = 30;

    // Pasa el certificado configurado a la librería y lo carga aquí, para que un certificado mal
    // configurado falle con un mensaje claro y no en el primer envío.
    // Dos orígenes: un .pfx con su contraseña (el del despliegue, entregado como secreto), o la
    // huella de un certificado instalado en el almacén del usuario (solo desarrollo local).
    // En ambos casos el certificado va a Wsd.Certificate, que es lo primero que mira la librería:
    // así la contraseña no entra nunca en Settings, que es lo que Settings.Save() serializa en claro.
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
            : LoadFromFile(path!, configuration);

        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException("El certificado no incluye la clave privada.");

        // La librería solo lo comprueba al enviar.
        if (certificate.NotAfter < DateTime.Now)
            throw new InvalidOperationException($"El certificado caducó el {certificate.NotAfter:yyyy-MM-dd}.");

        Warn(certificate);

        Wsd.Certificate = certificate;
        return certificate;
    }

    // La AEAT no avisa de la caducidad y renovar un certificado lleva su trámite, así que el aviso
    // sale por stderr sin hacer fallar la ejecución: un envío no se para por esto.
    static void Warn(X509Certificate2 certificate)
    {
        var days = (int)(certificate.NotAfter - DateTime.Now).TotalDays;
        if (days <= WarningDays)
            Console.Error.WriteLine(
                $"Aviso: el certificado caduca el {certificate.NotAfter:yyyy-MM-dd}, {(days == 1 ? "queda 1 día" : $"quedan {days} días")}.");
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

    // El .pfx se lee a memoria y no se deja en Settings: la librería lo recibe ya cargado.
    static X509Certificate2 LoadFromFile(string path, IConfiguration configuration)
    {
        // Sin esta comprobación la librería devuelve null y el error llega al enviar, sin mencionar la ruta.
        if (!File.Exists(path))
            throw new FileNotFoundException($"No existe el certificado indicado en '{PathKey}'.", path);

        // No se toca Settings: Wsd.Certificate tiene prioridad sobre la ruta y la huella, y así la
        // contraseña no entra en el objeto que Settings.Save() serializaría en claro. De paso, mirar
        // Settings.Current dispararía su constructor estático, que crea las carpetas de datos e
        // inicia la cadena de bloques: comprobar un certificado no tiene por qué escribir nada.
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

    // macOS no sabe cargar una clave privada sin un llavero, que exige escribir en disco, y Windows
    // no autentica con claves efímeras. En Linux, que es donde se despliega, la clave se queda en
    // memoria y no toca el disco.
    static X509KeyStorageFlags KeyStorageFlags => OperatingSystem.IsLinux()
        ? X509KeyStorageFlags.EphemeralKeySet
        : X509KeyStorageFlags.DefaultKeySet;

    // Desde fichero, para que la contraseña no pase por el entorno del proceso, que se filtra con
    // más facilidad. Como valor suelto sigue valiendo en local.
    static Span<char> ReadPassword(IConfiguration configuration)
    {
        var passwordPath = configuration[PasswordPathKey];
        if (string.IsNullOrWhiteSpace(passwordPath))
            return configuration[PasswordKey]?.ToCharArray() ?? [];

        if (!File.Exists(passwordPath))
            throw new FileNotFoundException($"No existe el fichero de contraseña indicado en '{PasswordPathKey}'.", passwordPath);

        return File.ReadAllText(passwordPath).TrimEnd('\r', '\n').ToCharArray();
    }
}
