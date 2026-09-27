using System.Runtime.CompilerServices;


namespace VerifactuShopify.UnitTests.TestDoubles;

// For what only holds on the deployment OS: on macOS loading a .pfx needs a temporary keychain on
// disk, and on Windows the key goes through the user's key container.
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Solo en Linux: macOS y Windows no pueden cargar la clave sin tocar el disco.";
        }
    }
}
