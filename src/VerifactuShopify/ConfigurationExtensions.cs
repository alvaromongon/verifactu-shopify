using Microsoft.Extensions.Configuration;

namespace VerifactuShopify;

static class ConfigurationExtensions
{
    public static string GetRequired(this IConfiguration configuration, string key) =>
        configuration[key] is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"Falta '{key}'. En local: dotnet user-secrets set \"{key}\" <valor> --project src/VerifactuShopify");
}
