using Microsoft.Extensions.Configuration;
using VerifactuShopify;

// M1: de momento solo comprueba que el certificado configurado se puede cargar, sin enviar nada.
if (args is not ["certificado"])
{
    Console.Error.WriteLine("Uso: dotnet run --project src/VerifactuShopify -- certificado");
    return 1;
}

var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
try
{
    using var certificate = CertificateSetup.Configure(configuration);
    Console.WriteLine($"Titular: {certificate.Subject}");
    Console.WriteLine($"Emisor:  {certificate.Issuer}");
    Console.WriteLine($"Caduca:  {certificate.NotAfter:yyyy-MM-dd}");
    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
