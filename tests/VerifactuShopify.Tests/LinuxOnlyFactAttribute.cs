namespace VerifactuShopify.Tests;

// Para lo que solo se cumple en el sistema donde se despliega: en macOS la carga de un .pfx exige un
// llavero temporal en disco y en Windows la clave pasa por el contenedor de claves del usuario.
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Solo en Linux: macOS y Windows no pueden cargar la clave sin tocar el disco.";
    }
}
