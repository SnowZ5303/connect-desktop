using System.Diagnostics;

namespace ConnectDesktop.Core.Tests.Fakes;

/// <summary>Espera uma condição com polling de 10 ms, até 3 s por padrão.</summary>
public static class Wait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 3000, string? what = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"Condição não atingida em {timeoutMs} ms" + (what is null ? "." : $": {what}."));
            await Task.Delay(10);
        }
    }
}

/// <summary>Pasta temporária única para o teste; apagada no Dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cd-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Arquivo ainda aberto por algum componente: deixa a pasta temporária para o SO limpar.
        }
    }
}
