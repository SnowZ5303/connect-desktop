// Dono: g10. Média de velocidade dos últimos ~2 s (interno do FileTransferService). Não é thread-safe: usar sob o lock do serviço.
namespace ConnectDesktop.Core.Transfer.Internal;

internal sealed class SpeedMeter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    private readonly Queue<(DateTime AtUtc, long Total)> _samples = new();
    private double _bytesPerSecond;

    /// <summary>Registra o total transferido até agora e devolve a média (bytes/s) da janela.</summary>
    public double Add(long total, DateTime nowUtc)
    {
        _samples.Enqueue((nowUtc, total));
        while (_samples.Count > 1 && nowUtc - _samples.Peek().AtUtc > Window)
            _samples.Dequeue();

        var first = _samples.Peek();
        var seconds = (nowUtc - first.AtUtc).TotalSeconds;
        if (seconds > 0)
            _bytesPerSecond = (total - first.Total) / seconds;
        return _bytesPerSecond;
    }
}
