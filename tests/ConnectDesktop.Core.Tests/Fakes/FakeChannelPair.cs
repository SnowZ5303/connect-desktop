using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ConnectDesktop.Core.Net;
using ConnectDesktop.Core.Protocol;

namespace ConnectDesktop.Core.Tests.Fakes;

/// <summary>
/// Dois canais em memória ligados por System.Threading.Channels.
/// Fechar qualquer um dos lados fecha o par: os dois disparam Closed (uma vez) e os ReadAllAsync terminam.
/// </summary>
public sealed class FakeChannelPair : IAsyncDisposable
{
    private readonly Channel<Message> _toA = Channel.CreateUnbounded<Message>();
    private readonly Channel<Message> _toB = Channel.CreateUnbounded<Message>();
    private readonly Endpoint _a;
    private readonly Endpoint _b;
    private int _closed;

    public FakeChannelPair(string idA = "id-a", string nameA = "PC-A", string idB = "id-b", string nameB = "PC-B")
    {
        // A lê o que chega em _toA e escreve em _toB; B faz o contrário.
        _a = new Endpoint(this, _toA, _toB, idB, nameB);
        _b = new Endpoint(this, _toB, _toA, idA, nameA);
    }

    public IMessageChannel A => _a;
    public IMessageChannel B => _b;

    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    internal void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _toA.Writer.TryComplete();
        _toB.Writer.TryComplete();
        _a.RaiseClosed();
        _b.RaiseClosed();
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    private sealed class Endpoint : IMessageChannel
    {
        private readonly FakeChannelPair _pair;
        private readonly Channel<Message> _inbox;
        private readonly Channel<Message> _outbox;

        public Endpoint(FakeChannelPair pair, Channel<Message> inbox, Channel<Message> outbox, string remoteId, string remoteName)
        {
            _pair = pair;
            _inbox = inbox;
            _outbox = outbox;
            RemoteDeviceId = remoteId;
            RemoteDeviceName = remoteName;
        }

        public string RemoteDeviceId { get; }
        public string RemoteDeviceName { get; }

        public event Action? Closed;

        public async Task SendAsync(Message message, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_pair.IsClosed || !_outbox.Writer.TryWrite(message))
                throw new IOException("Canal fechado.");
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<Message> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            while (await _inbox.Reader.WaitToReadAsync(ct))
            {
                while (_inbox.Reader.TryRead(out var message))
                    yield return message;
            }
        }

        public void RaiseClosed() => Closed?.Invoke();

        public ValueTask DisposeAsync()
        {
            _pair.Close();
            return ValueTask.CompletedTask;
        }
    }
}
