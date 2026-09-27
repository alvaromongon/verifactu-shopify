using System.Runtime.CompilerServices;


namespace VerifactuShopify.UnitTests.TestDoubles;

// For tests that load a chain from the AEAT: the library only allows it with an empty chains folder,
// which is how the deployment starts and how CI is. On a development machine with real chains they're
// skipped so those aren't touched. The path is VeriFactu.Config.Settings' one, without starting it.
public sealed class EmptyBlockchainFolderFactAttribute : FactAttribute
{
    public EmptyBlockchainFolderFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        var root = Environment.GetFolderPath(OperatingSystem.IsMacOS()
            ? Environment.SpecialFolder.ApplicationData
            : Environment.SpecialFolder.CommonApplicationData);
        var blockchains = Path.Combine(root, "VeriFactu", "Blockchains");

        if (Directory.Exists(blockchains) && Directory.EnumerateDirectories(blockchains).Any())
        {
            Skip = $"{blockchains} ya tiene cadenas: la librería no puede cargar otra desde la AEAT.";
        }
    }
}
