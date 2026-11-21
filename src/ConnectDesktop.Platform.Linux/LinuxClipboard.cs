#nullable enable
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Linux;

/// <summary>
/// Área de transferência via xclip (fallback xsel). Usa o processo externo com timeout de 2 s e UTF-8.
/// Nunca lança exceção.
/// </summary>
internal sealed class LinuxClipboard : IClipboard
{
    private const int TimeoutMs = 2000;
    private static readonly UTF8Encoding Utf8 = new(false);

    public string? GetText()
    {
        try
        {
            if (TryRun("xclip", "-selection clipboard -o", null, out string? text)) return text;
            if (TryRun("xsel", "--clipboard --output", null, out text)) return text;
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void SetText(string text)
    {
        try
        {
            string value = text ?? string.Empty;
            if (TryRun("xclip", "-selection clipboard -i", value, out _)) return;
            TryRun("xsel", "--clipboard --input", value, out _);
        }
        catch (Exception)
        {
            // Nunca lança: em erro ignora.
        }
    }

    /// <summary>
    /// Executa o programa. Retorna false se ele não pôde ser iniciado (ex.: não instalado).
    /// Em <paramref name="output"/> fica o texto lido da saída padrão (null se vazio, em erro ou timeout).
    /// </summary>
    private static bool TryRun(string file, string arguments, string? input, out string? output)
    {
        output = null;

        var startInfo = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = input != null,
            RedirectStandardOutput = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
        };

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception)
        {
            return false;
        }

        if (process == null) return false;

        using (process)
        {
            Task<string>? readTask = null;
            if (input == null)
            {
                readTask = process.StandardOutput.ReadToEndAsync();
            }
            else
            {
                try
                {
                    process.StandardInput.Write(input);
                    process.StandardInput.Close();
                }
                catch (Exception)
                {
                    // Processo pode ter fechado a entrada; o resultado é tratado pelo exit code.
                }
            }

            if (!process.WaitForExit(TimeoutMs))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                    // Já encerrado.
                }
                return true;
            }

            if (readTask != null && process.ExitCode == 0 && readTask.Wait(TimeoutMs))
            {
                string text = readTask.Result;
                output = string.IsNullOrEmpty(text) ? null : text;
            }

            return true;
        }
    }
}
