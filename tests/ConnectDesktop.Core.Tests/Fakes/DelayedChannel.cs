using ConnectDesktop.Core.Net;
using ConnectDesktop.Core.Protocol;

namespace ConnectDesktop.Core.Tests.Fakes;

/// <summary>
/// Envolve um canal e espera <paramref name="delayMs"/> antes de cada envio. Usado para dar tempo de
/// cancelar ou derrubar uma transferência no meio, sem depender de corrida.
/// Leitura, Closed e Dispose são repassados ao canal original.
/// </summary>
public sealed class DelayedChannel : IMessageChannel
{
    private readonly IMessageChannel _inner;
    private readonly int _delayMs;

    public DelayedChannel(IMessageChannel inner, int delayMs)
    {
        _inner = inner;
        _delayMs = delayMs;
    }

    public string RemoteDeviceId => _inner.RemoteDeviceId;
    public string RemoteDeviceName => _inner.RemoteDeviceName;
    public string RemoteVersion => _inner.RemoteVersion;

    public event Action? Closed
    {
        add => _inner.Closed += value;
        remove => _inner.Closed -= value;
    }

    public async Task SendAsync(Message message, CancellationToken ct = default)
    {
        if (_delayMs > 0)
            await Task.Delay(_delayMs, ct);
        await _inner.SendAsync(message, ct);
    }

    public IAsyncEnumerable<Message> ReadAllAsync(CancellationToken ct = default) => _inner.ReadAllAsync(ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
