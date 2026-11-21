using ConnectDesktop.Core.Layout;
using ConnectDesktop.Core.Net;
using ConnectDesktop.Core.Settings;
using ConnectDesktop.Core.Tests.Fakes;
using Xunit;

namespace ConnectDesktop.Core.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Load_de_caminho_inexistente_cria_padrao_com_DeviceId()
    {
        var path = _temp.File("settings.json");
        Assert.False(File.Exists(path));

        var settings = AppSettings.Load(path);

        Assert.False(string.IsNullOrWhiteSpace(settings.DeviceId));
        Assert.Equal(Environment.MachineName, settings.DeviceName);
        Assert.Equal(DeviceRole.Secondary, settings.Role);
        Assert.Equal(ScreenSide.Right, settings.SecondarySide);
        Assert.True(settings.ShareClipboard);
        Assert.True(settings.CheckUpdatesOnStart);
        Assert.True(File.Exists(path), "Load deveria ter salvo o padrão no arquivo.");
    }

    [Fact]
    public void Load_padrao_gera_DeviceId_que_se_mantem_na_proxima_leitura()
    {
        var path = _temp.File("settings.json");

        var first = AppSettings.Load(path);
        var second = AppSettings.Load(path);

        Assert.Equal(first.DeviceId, second.DeviceId);
    }

    [Fact]
    public void DeviceId_padrao_e_um_guid_no_formato_N()
    {
        var settings = AppSettings.Load(_temp.File("settings.json"));
        Assert.Matches("^[0-9a-f]{32}$", settings.DeviceId);
        Assert.True(Guid.TryParseExact(settings.DeviceId, "N", out _));
    }

    [Fact]
    public void Save_e_Load_fazem_round_trip()
    {
        var path = _temp.File("settings.json");
        var original = new AppSettings
        {
            DeviceId = "0123456789abcdef0123456789abcdef",
            DeviceName = "PC da Sala",
            Role = DeviceRole.Primary,
            SecondarySide = ScreenSide.Top,
            LastPeerId = "fedcba9876543210fedcba9876543210",
            ShareClipboard = false,
            CheckUpdatesOnStart = false,
        };

        original.Save(path);
        var loaded = AppSettings.Load(path);

        Assert.Equal(original.DeviceId, loaded.DeviceId);
        Assert.Equal(original.DeviceName, loaded.DeviceName);
        Assert.Equal(original.Role, loaded.Role);
        Assert.Equal(original.SecondarySide, loaded.SecondarySide);
        Assert.Equal(original.LastPeerId, loaded.LastPeerId);
        Assert.Equal(original.ShareClipboard, loaded.ShareClipboard);
        Assert.Equal(original.CheckUpdatesOnStart, loaded.CheckUpdatesOnStart);
    }

    [Fact]
    public void Save_grava_enums_como_texto()
    {
        var path = _temp.File("settings.json");
        new AppSettings { DeviceId = "abc", DeviceName = "x", Role = DeviceRole.Primary, SecondarySide = ScreenSide.Top }.Save(path);

        var json = File.ReadAllText(path);
        Assert.Contains("\"Primary\"", json);
        Assert.Contains("\"Top\"", json);
    }

    [Fact]
    public void Load_de_arquivo_corrompido_cria_padrao()
    {
        var path = _temp.File("settings.json");
        File.WriteAllText(path, "{ quebrado");

        var settings = AppSettings.Load(path);

        Assert.False(string.IsNullOrWhiteSpace(settings.DeviceId));
        Assert.Equal(Environment.MachineName, settings.DeviceName);
    }

    [Fact]
    public void ToIdentity_usa_id_e_nome()
    {
        var settings = new AppSettings { DeviceId = "id-1", DeviceName = "Nome 1" };
        var identity = settings.ToIdentity();
        Assert.Equal("id-1", identity.Id);
        Assert.Equal("Nome 1", identity.Name);
    }
}
