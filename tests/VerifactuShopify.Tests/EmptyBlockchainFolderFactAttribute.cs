namespace VerifactuShopify.Tests;

// Para tests que cargan una cadena desde la AEAT: la librería solo lo admite con la carpeta de cadenas
// vacía, que es como arranca el despliegue y como está en CI. En una máquina de desarrollo con cadenas
// reales se saltan, para no tocarlas. La ruta es la de VeriFactu.Config.Settings, sin iniciarlo.
public sealed class EmptyBlockchainFolderFactAttribute : FactAttribute
{
    public EmptyBlockchainFolderFactAttribute()
    {
        var root = Environment.GetFolderPath(OperatingSystem.IsMacOS()
            ? Environment.SpecialFolder.ApplicationData
            : Environment.SpecialFolder.CommonApplicationData);
        var blockchains = Path.Combine(root, "VeriFactu", "Blockchains");

        if (Directory.Exists(blockchains) && Directory.EnumerateDirectories(blockchains).Any())
            Skip = $"{blockchains} ya tiene cadenas: la librería no puede cargar otra desde la AEAT.";
    }
}
