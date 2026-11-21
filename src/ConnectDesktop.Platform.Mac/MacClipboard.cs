// Dono: g04. Área de transferência de texto no macOS (pbpaste / pbcopy).
using System.Diagnostics;
using System.Text;
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Mac;

public sealed class MacClipboard : IClipboard
{
    private const string PbPaste = "/usr/bin/pbpaste";
    private const string PbCopy = "/usr/bin/pbcopy";
    private const int TimeoutMs = 2000;
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Retorna null se não houver texto ou em qualquer erro. Nunca lança.</summary>
    public string? GetText()
    {
        try
        {
            string? output = Run(PbPaste, null);
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Nunca lança: em erro ignora.</summary>
    public void SetText(string text)
    {
        try
        {
            Run(PbCopy, text ?? string.Empty);
        }
        catch
        {
            // ignora erros da área de transferência
        }
    }

    /// <summary>Executa o comando com timeout. Retorna a saída (UTF-8) ou null se falhar ou exceder o tempo.</summary>
    private static string? Run(string fileName, string? input)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = input != null,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
        };
        psi.Environment["LANG"] = "en_US.UTF-8";

        using Process? process = Process.Start(psi);
        if (process == null)
        {
            return null;
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        if (input != null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }

        if (!process.WaitForExit(TimeoutMs))
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // processo já encerrado
            }
            return null;
        }

        if (!outputTask.Wait(TimeoutMs) || process.ExitCode != 0)
        {
            return null;
        }
        return outputTask.Result;
    }
}
