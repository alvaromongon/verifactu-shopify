using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;


namespace VerifactuShopify.UnitTests.TestDoubles;

// For tests that install a certificate in the user store: that changes the machine they run on, so
// they're skipped outside CI. On macOS the store is the keychain, which may be locked on a runner,
// so they don't run there either.
public sealed class CiOnlyFactAttribute : FactAttribute
{
    public CiOnlyFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("CI") is not "true")
        {
            Skip = "Solo en CI: instalaría un certificado en el almacén de esta máquina.";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Skip = "En macOS el almacén del usuario es el llavero del runner.";
        }
    }
}
