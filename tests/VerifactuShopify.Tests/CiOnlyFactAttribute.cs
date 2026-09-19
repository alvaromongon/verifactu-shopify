using System.Runtime.InteropServices;

namespace VerifactuShopify.Tests;

// Para tests que instalan un certificado en el almacén del usuario: eso cambia la máquina donde
// corren, así que fuera de CI se saltan. En macOS el almacén es el llavero, que en un runner puede
// estar bloqueado, y tampoco se ejecutan.
public sealed class CiOnlyFactAttribute : FactAttribute
{
    public CiOnlyFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CI") is not "true")
            Skip = "Solo en CI: instalaría un certificado en el almacén de esta máquina.";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Skip = "En macOS el almacén del usuario es el llavero del runner.";
    }
}
