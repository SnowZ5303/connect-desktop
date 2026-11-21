using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.App;

/// <summary>
/// Escolhe a implementação de plataforma do sistema atual por reflexão,
/// para o app compilar sem depender diretamente das classes de plataforma.
/// </summary>
internal static class PlatformFactory
{
    public static IPlatform Create()
    {
        string typeName;
        string systemName;
        if (OperatingSystem.IsWindows())
        {
            typeName = "ConnectDesktop.Platform.Windows.WindowsPlatform, ConnectDesktop.Platform.Windows";
            systemName = "Windows";
        }
        else if (OperatingSystem.IsMacOS())
        {
            typeName = "ConnectDesktop.Platform.Mac.MacPlatform, ConnectDesktop.Platform.Mac";
            systemName = "macOS";
        }
        else if (OperatingSystem.IsLinux())
        {
            typeName = "ConnectDesktop.Platform.Linux.LinuxPlatform, ConnectDesktop.Platform.Linux";
            systemName = "Linux";
        }
        else
        {
            throw new PlatformNotSupportedException("Sistema operacional não suportado pelo Connect Desktop.");
        }

        var type = Type.GetType(typeName);
        if (type is null)
            throw new InvalidOperationException($"A implementação de plataforma para {systemName} não foi encontrada. Reinstale o Connect Desktop.");

        try
        {
            return Activator.CreateInstance(type) as IPlatform
                ?? throw new InvalidOperationException($"A implementação de plataforma para {systemName} é inválida.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Não foi possível iniciar a implementação de plataforma para {systemName}: {ex.Message}", ex);
        }
    }
}
