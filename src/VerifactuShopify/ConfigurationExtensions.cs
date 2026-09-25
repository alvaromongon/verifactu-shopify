using Microsoft.Extensions.Configuration;

namespace VerifactuShopify;

static class ConfigurationExtensions
{
    public const string SellerNifKey = "VeriFactu:Emisor:NIF";
    public const string SellerNameKey = "VeriFactu:Emisor:Nombre";

    public static string GetRequired(this IConfiguration configuration, string key) =>
        configuration[key] is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"Falta '{key}'. En local: dotnet user-secrets set \"{key}\" <valor> --project src/VerifactuShopify");

    // Same convention as the certificate password: in a deployment the secret arrives as a file
    // at {key}Path (a mounted Secret, Docker secrets, systemd credentials...), which leaks less
    // easily than the process environment. The plain value still works for local development.
    public static string GetRequiredSecret(this IConfiguration configuration, string key)
    {
        var path = configuration[$"{key}Path"];
        if (string.IsNullOrWhiteSpace(path))
            return configuration.GetRequired(key);

        if (!File.Exists(path))
            throw new FileNotFoundException($"No existe el fichero indicado en '{key}Path'.", path);

        return File.ReadAllText(path).TrimEnd('\r', '\n');
    }
}
