// Dono: g02. Assinaturas públicas FIXAS.
using System.Text.Json;
using System.Text.Json.Serialization;
using ConnectDesktop.Core.Layout;
using ConnectDesktop.Core.Net;

namespace ConnectDesktop.Core.Settings;

public static class AppPaths
{
    /// <summary>Environment.SpecialFolder.ApplicationData + "ConnectDesktop" (cria a pasta se não existir).</summary>
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConnectDesktop");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string PairingsFile => Path.Combine(DataDir, "pairings.json");
    public static string LogFile => Path.Combine(DataDir, "connectdesktop.log");
}

/// <summary>Configurações persistidas em JSON (System.Text.Json, enums como texto).</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Guid "N" gerado na primeira execução.</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>Padrão: Environment.MachineName.</summary>
    public string DeviceName { get; set; } = "";
    public DeviceRole Role { get; set; } = DeviceRole.Secondary;
    /// <summary>Lado em que o PC secundário fica em relação ao principal (configurado no principal).</summary>
    public ScreenSide SecondarySide { get; set; } = ScreenSide.Right;
    /// <summary>Id do último secundário conectado (o principal reconecta sozinho ao vê-lo na rede).</summary>
    public string? LastPeerId { get; set; }
    public bool ShareClipboard { get; set; } = true;
    public bool CheckUpdatesOnStart { get; set; } = true;

    // ---- v1.0.2 (interface nova) ----
    /// <summary>"" = idioma do sistema; senão "pt-BR", "en" ou "es".</summary>
    public string Language { get; set; } = "";
    /// <summary>"System", "Light" ou "Dark".</summary>
    public string ThemeMode { get; set; } = "System";
    /// <summary>Cor de destaque em hexadecimal "#RRGGBB". Padrão azul.</summary>
    public string AccentColor { get; set; } = "#2563EB";
    public bool StartWithSystem { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    /// <summary>"" = pasta padrão (FileTransferService.DefaultDownloadDir).</summary>
    public string DownloadDir { get; set; } = "";

    /// <summary>Carrega de <paramref name="path"/> (padrão AppPaths.SettingsFile). Se não existir/corrompido, cria padrão
    /// (com DeviceId novo e DeviceName = MachineName) e salva. Nunca lança.</summary>
    public static AppSettings Load(string? path = null)
    {
        bool needsSave = false;
        AppSettings? settings = null;
        try
        {
            path ??= AppPaths.SettingsFile;
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppSettings] falha ao ler configurações: {ex.Message}");
            settings = null;
        }

        if (settings == null)
        {
            settings = new AppSettings();
            needsSave = true;
        }

        if (string.IsNullOrWhiteSpace(settings.DeviceId))
        {
            settings.DeviceId = Guid.NewGuid().ToString("N");
            needsSave = true;
        }
        if (string.IsNullOrWhiteSpace(settings.DeviceName))
        {
            settings.DeviceName = Environment.MachineName;
            needsSave = true;
        }

        if (needsSave)
        {
            try
            {
                settings.Save(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] falha ao salvar configurações: {ex.Message}");
            }
        }

        return settings;
    }

    /// <summary>Grava em arquivo temporário e troca pelo definitivo (escrita atômica).</summary>
    public void Save(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignora: arquivo temporário órfão */ }
            }
        }
    }

    /// <summary>Ordem dos itens do menu lateral (ids: "connection", "devices", "transfers", "pairs"). Vazia = ordem padrão.</summary>
    public List<string> NavOrder { get; set; } = new();

    /// <param name="version">Versão do app ("1.0.4"), enviada no handshake.</param>
    public DeviceIdentity ToIdentity(string version = "") => new(DeviceId, DeviceName, version);
}
