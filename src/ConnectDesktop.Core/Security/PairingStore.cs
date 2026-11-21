// Dono: g01. Assinaturas públicas FIXAS.
using System.Security.Cryptography;
using System.Text.Json;

namespace ConnectDesktop.Core.Security;

/// <summary>
/// Guarda as chaves de longo prazo dos PCs já pareados (deviceId -> chave de 32 bytes, base64),
/// num arquivo JSON (o caminho vem no construtor; o app usa AppPaths.PairingsFile). Thread-safe.
/// Arquivo inexistente ou corrompido = lista vazia (não lança).
/// </summary>
public sealed class PairingStore
{
    private sealed record Entry(string Name, byte[] Key);

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _devices = new();

    public PairingStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public byte[]? GetKey(string deviceId)
    {
        lock (_lock)
        {
            return _devices.TryGetValue(deviceId, out var entry) ? (byte[])entry.Key.Clone() : null;
        }
    }

    public void SetKey(string deviceId, string deviceName, byte[] key)
    {
        lock (_lock)
        {
            _devices[deviceId] = new Entry(deviceName, (byte[])key.Clone());
            Save();
        }
    }

    public void Remove(string deviceId)
    {
        lock (_lock)
        {
            if (_devices.Remove(deviceId))
                Save();
        }
    }

    public IReadOnlyList<(string DeviceId, string DeviceName)> List()
    {
        lock (_lock)
        {
            return _devices.Select(kv => (DeviceId: kv.Key, DeviceName: kv.Value.Name)).ToList();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllBytes(_filePath));
            foreach (var item in doc.RootElement.GetProperty("devices").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString() ?? throw new FormatException("id ausente.");
                var name = item.GetProperty("name").GetString() ?? string.Empty;
                var key = Convert.FromBase64String(item.GetProperty("key").GetString() ?? throw new FormatException("key ausente."));
                _devices[id] = new Entry(name, key);
            }
        }
        catch (Exception)
        {
            // Arquivo corrompido = lista vazia.
            _devices.Clear();
        }
    }

    private void Save()
    {
        var devices = _devices.Select(kv => new
        {
            id = kv.Key,
            name = kv.Value.Name,
            key = Convert.ToBase64String(kv.Value.Key),
        }).ToList();

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { devices }, new JsonSerializerOptions { WriteIndented = true });

        var fullPath = Path.GetFullPath(_filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = fullPath + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, fullPath, overwrite: true);
    }
}

public sealed class PairingException : Exception
{
    public PairingException(string message) : base(message) { }
}

public static class PairingCode
{
    /// <summary>Gera um código aleatório de 6 dígitos (RandomNumberGenerator), ex.: "048213".</summary>
    public static string Generate() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}
