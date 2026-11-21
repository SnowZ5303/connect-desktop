// Dono: g10. Higienização de nomes de arquivo recebidos (interno do FileTransferService).
namespace ConnectDesktop.Core.Transfer.Internal;

internal static class FileNameSanitizer
{
    private const int MaxLength = 200;
    private const string ExtraInvalidChars = "<>:\"/\\|?*";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Devolve um nome seguro (só o nome, sem pastas, sem caracteres inválidos, até 200 caracteres).</summary>
    public static string Sanitize(string? rawName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = Path.GetFileName(rawName ?? string.Empty).ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || ExtraInvalidChars.IndexOf(chars[i]) >= 0)
                chars[i] = '_';
        }

        var name = new string(chars).TrimEnd(' ', '.');
        if (name.Length == 0)
            return "arquivo";

        var stem = name.Split('.')[0].TrimEnd(' ');
        if (Reserved.Contains(stem))
            name = "_" + name;

        if (name.Length > MaxLength)
        {
            var ext = Path.GetExtension(name);
            name = ext.Length < MaxLength
                ? name.Substring(0, MaxLength - ext.Length) + ext
                : name.Substring(0, MaxLength);
            name = name.TrimEnd(' ', '.');
        }

        return name.Length == 0 ? "arquivo" : name;
    }
}
