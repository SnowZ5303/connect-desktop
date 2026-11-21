// Dono: g02. Assinaturas públicas FIXAS.
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using ConnectDesktop.Core.Layout;
using ConnectDesktop.Core.Net;
using ConnectDesktop.Core.Platform;
using ConnectDesktop.Core.Protocol;

namespace ConnectDesktop.Core.Session;

public enum PrimaryState { Local, Remote }

/// <summary>
/// Lógica do PC principal com um secundário conectado.
/// - Assina os eventos de <see cref="IInputCapture"/> (já criado; esta classe chama Start/Stop).
/// - Estado Local: quando o mouse encosta na borda <see cref="SecondarySide"/> da área de trabalho, entra em Remote:
///   liga SuppressLocalInput e envia EnterMessage(ScreenLayout.Opposite(SecondarySide), posição ao longo da borda).
/// - Estado Remote: envia MouseMove(DX,DY), botões, roda e teclas para o secundário.
/// - Ao receber LeaveMessage: volta para Local, desliga SuppressLocalInput e põe o cursor em
///   ScreenLayout.PointOnEdge(bounds, SecondarySide, msg.Position, inset: 2).
/// - Atalho Ctrl+Alt+Esc (qualquer Ctrl + qualquer Alt + Esc) em Remote: envia ReleaseAll e volta para Local na hora.
/// - Se o canal fechar em Remote: volta para Local.
/// - Teclas pressionadas no principal antes de entrar em Remote: ao entrar, não enviar o "up" de teclas que não foram enviadas como "down".
/// - Lê as mensagens do canal em segundo plano (ScreenInfo, Leave, Clipboard repassado ao <see cref="ClipboardSync"/> quando houver, Pong).
/// Os envios a partir da thread do hook não podem bloquear: usar uma fila (Channel&lt;Message&gt;) com uma Task que envia.
/// </summary>
public sealed class PrimarySession : IAsyncDisposable
{
    private enum ReturnReason { Leave, Shortcut, Closed }

    private readonly IMessageChannel _channel;
    private readonly IInputCapture _capture;
    private readonly IScreenInfo _screen;
    private readonly ClipboardSync? _clipboard;
    private readonly Channel<Message> _outbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<ushort> _pressedKeys = new();
    private readonly object _lock = new();

    private PrimaryState _state = PrimaryState.Local;
    private int _remoteWidth;
    private int _remoteHeight;
    private bool _leftCtrl, _rightCtrl, _leftAlt, _rightAlt;
    private bool _started;
    private bool _disposed;
    private bool _endedRaised;
    private System.Threading.Timer? _pingTimer;

    public PrimarySession(IMessageChannel channel, IInputCapture capture, IScreenInfo screen, ScreenSide secondarySide, ClipboardSync? clipboard)
    {
        _channel = channel;
        _capture = capture;
        _screen = screen;
        SecondarySide = secondarySide;
        _clipboard = clipboard;
    }

    public ScreenSide SecondarySide { get; set; }

    /// <summary>Serviço de transferência de arquivos (definido pelo app antes do Start).</summary>
    public Transfer.FileTransferService? Files { get; set; }

    public PrimaryState State
    {
        get
        {
            lock (_lock) { return _state; }
        }
    }

    /// <summary>Tamanho da tela do secundário (0x0 até chegar o ScreenInfo).</summary>
    public (int Width, int Height) RemoteScreenSize
    {
        get
        {
            lock (_lock) { return (_remoteWidth, _remoteHeight); }
        }
    }

    public event Action<PrimaryState>? StateChanged;
    /// <summary>Disparado quando a sessão termina (canal fechou ou Dispose).</summary>
    public event Action? Ended;

    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed) return;
            _started = true;
        }

        _capture.MouseMoved += OnMouseMoved;
        _capture.MouseButtonChanged += OnMouseButtonChanged;
        _capture.MouseWheel += OnMouseWheel;
        _capture.KeyChanged += OnKeyChanged;
        _capture.Start();

        _ = Task.Run(SendLoopAsync);
        _ = Task.Run(ReadLoopAsync);

        _pingTimer = new System.Threading.Timer(_ => _outbox.Writer.TryWrite(new PingMessage(Environment.TickCount64)), null, 5000, 5000);

        _clipboard?.Attach(_channel);
        Files?.Attach(_channel);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _capture.MouseMoved -= OnMouseMoved;
        _capture.MouseButtonChanged -= OnMouseButtonChanged;
        _capture.MouseWheel -= OnMouseWheel;
        _capture.KeyChanged -= OnKeyChanged;

        ReturnToLocal(ReturnReason.Closed);
        try
        {
            _capture.SuppressLocalInput = false;
            _capture.Stop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PrimarySession] erro ao parar a captura: {ex.Message}");
        }

        _pingTimer?.Dispose();
        _outbox.Writer.TryComplete();
        _clipboard?.Attach(null);
        _cts.Cancel();

        try
        {
            await _channel.DisposeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PrimarySession] erro ao fechar o canal: {ex.Message}");
        }

        RaiseEndedOnce();
    }

    private void OnMouseMoved(MouseMoveEvent e)
    {
        lock (_lock)
        {
            if (_state == PrimaryState.Remote)
            {
                if (e.DX != 0 || e.DY != 0)
                    _outbox.Writer.TryWrite(new MouseMoveMessage(e.DX, e.DY));
                return;
            }
        }

        var side = SecondarySide;
        var bounds = _screen.GetDesktopBounds();
        if (!ScreenLayout.IsAtEdge(bounds, side, e.X, e.Y) || !IsMovingToward(side, e.DX, e.DY))
            return;

        lock (_lock)
        {
            if (_disposed || _state != PrimaryState.Local) return;
            _state = PrimaryState.Remote;
            _pressedKeys.Clear();
            var position = ScreenLayout.PositionAlongEdge(bounds, side, e.X, e.Y);
            _outbox.Writer.TryWrite(new EnterMessage(ScreenLayout.Opposite(side), position));
            _capture.SuppressLocalInput = true;
        }

        StateChanged?.Invoke(PrimaryState.Remote);
    }

    private void OnMouseButtonChanged(MouseButtonEvent e)
    {
        lock (_lock)
        {
            if (_state != PrimaryState.Remote) return;
            _outbox.Writer.TryWrite(new MouseButtonMessage(e.Button, e.Down));
        }
    }

    private void OnMouseWheel(MouseWheelEvent e)
    {
        lock (_lock)
        {
            if (_state != PrimaryState.Remote) return;
            _outbox.Writer.TryWrite(new MouseWheelMessage(e.DX, e.DY));
        }
    }

    private void OnKeyChanged(KeyEvent e)
    {
        bool emergency = false;
        lock (_lock)
        {
            TrackModifier(e);
            if (_state != PrimaryState.Remote) return;

            if (e.Hid == HidKeys.Escape && e.Down && (_leftCtrl || _rightCtrl) && (_leftAlt || _rightAlt))
            {
                emergency = true;
            }
            else if (e.Down)
            {
                _pressedKeys.Add(e.Hid);
                _outbox.Writer.TryWrite(new KeyMessage(e.Hid, true));
            }
            else if (_pressedKeys.Remove(e.Hid))
            {
                _outbox.Writer.TryWrite(new KeyMessage(e.Hid, false));
            }
        }

        if (emergency)
            ReturnToLocal(ReturnReason.Shortcut);
    }

    private void TrackModifier(KeyEvent e)
    {
        switch (e.Hid)
        {
            case HidKeys.LeftCtrl: _leftCtrl = e.Down; break;
            case HidKeys.RightCtrl: _rightCtrl = e.Down; break;
            case HidKeys.LeftAlt: _leftAlt = e.Down; break;
            case HidKeys.RightAlt: _rightAlt = e.Down; break;
        }
    }

    private static bool IsMovingToward(ScreenSide side, int dx, int dy) => side switch
    {
        ScreenSide.Left => dx < 0,
        ScreenSide.Right => dx > 0,
        ScreenSide.Top => dy < 0,
        ScreenSide.Bottom => dy > 0,
        _ => false
    };

    /// <summary>Volta para Local (só age se estiver em Remote). Não chamar com o lock tomado.</summary>
    private void ReturnToLocal(ReturnReason reason, double leavePosition = 0.0)
    {
        lock (_lock)
        {
            if (_state != PrimaryState.Remote) return;
            _state = PrimaryState.Local;
            _pressedKeys.Clear();
            if (reason == ReturnReason.Shortcut)
                _outbox.Writer.TryWrite(new ReleaseAllMessage());
            try
            {
                _capture.SuppressLocalInput = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PrimarySession] erro ao religar a entrada local: {ex.Message}");
            }
        }

        try
        {
            if (reason == ReturnReason.Leave)
            {
                var bounds = _screen.GetDesktopBounds();
                var (x, y) = ScreenLayout.PointOnEdge(bounds, SecondarySide, leavePosition, 2);
                _screen.SetCursorPosition(x, y);
            }
            else if (reason == ReturnReason.Shortcut)
            {
                var bounds = _screen.GetDesktopBounds();
                _screen.SetCursorPosition(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PrimarySession] erro ao posicionar o cursor: {ex.Message}");
        }

        StateChanged?.Invoke(PrimaryState.Local);
    }

    private void HandleIncoming(Message message)
    {
        switch (message)
        {
            case LeaveMessage leave:
                ReturnToLocal(ReturnReason.Leave, leave.Position);
                break;
            case ScreenInfoMessage info:
                lock (_lock)
                {
                    _remoteWidth = info.Width;
                    _remoteHeight = info.Height;
                }
                break;
            case ClipboardMessage clipboardMessage:
                _clipboard?.OnRemoteText(clipboardMessage.Text);
                break;
            case PingMessage ping:
                _outbox.Writer.TryWrite(new PongMessage(ping.Timestamp));
                break;
            case FileOfferMessage:
            case FileChunkMessage:
            case FileDoneMessage:
            case FileCancelMessage:
            case FileResultMessage:
                ForwardFileMessage(message);
                break;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var message in _channel.ReadAllAsync(_cts.Token))
                HandleIncoming(message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PrimarySession] leitura encerrada: {ex.Message}");
        }
        finally
        {
            ReturnToLocal(ReturnReason.Closed);
            RaiseEndedOnce();
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync())
            {
                try
                {
                    await _channel.SendAsync(message);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PrimarySession] envio falhou: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PrimarySession] fila de envio encerrada: {ex.Message}");
        }
    }

    private void ForwardFileMessage(Message message)
    {
        try
        {
            Files?.OnRemoteMessage(message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Session] falha ao processar arquivo: {ex.Message}");
        }
    }

    private void RaiseEndedOnce()
    {
        lock (_lock)
        {
            if (_endedRaised) return;
            _endedRaised = true;
        }

        try
        {
            Files?.Detach(_channel);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Session] falha ao desligar transferência: {ex.Message}");
        }

        Ended?.Invoke();
    }
}

/// <summary>
/// Lógica do PC secundário com um principal conectado.
/// - Ao iniciar: envia ScreenInfoMessage com o tamanho da área de trabalho.
/// - EnterMessage(side, p): guarda PrimarySide = side, ativa, posiciona o cursor em
///   PointOnEdge(bounds, side, p, inset: 2) (injeta MoveMouseAbsolute) e passa a aceitar input.
/// - MouseMove(dx,dy): atualiza a posição virtual (limitada à área de trabalho) e injeta MoveMouseAbsolute.
///   Se o movimento empurrar além da borda PrimarySide (ex.: Left e x tentaria ficar &lt; bounds.X), envia
///   LeaveMessage(PositionAlongEdge(...)) e desativa (ignora input até o próximo Enter).
/// - Botões, roda e teclas: injeta. Guarda o que está pressionado; em ReleaseAll, Leave ou canal fechado, solta tudo.
/// - Ping -> responde Pong.
/// </summary>
public sealed class SecondarySession : IAsyncDisposable
{
    private readonly IMessageChannel _channel;
    private readonly IInputInjector _injector;
    private readonly IScreenInfo _screen;
    private readonly ClipboardSync? _clipboard;
    private readonly Channel<Message> _outbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<ushort> _pressedKeys = new();
    private readonly HashSet<MouseButton> _pressedButtons = new();
    private readonly object _lock = new();

    private bool _isActive;
    private int _virtualX;
    private int _virtualY;
    private bool _started;
    private bool _disposed;
    private bool _endedRaised;

    public SecondarySession(IMessageChannel channel, IInputInjector injector, IScreenInfo screen, ClipboardSync? clipboard)
    {
        _channel = channel;
        _injector = injector;
        _screen = screen;
        _clipboard = clipboard;
    }

    /// <summary>Lado em que o principal fica, visto do secundário (vem do último EnterMessage). Padrão Left.</summary>
    public ScreenSide PrimarySide { get; private set; } = ScreenSide.Left;

    /// <summary>Serviço de transferência de arquivos (definido pelo app antes do Start).</summary>
    public Transfer.FileTransferService? Files { get; set; }

    /// <summary>true enquanto o cursor do principal está nesta tela.</summary>
    public bool IsActive
    {
        get
        {
            lock (_lock) { return _isActive; }
        }
    }

    public event Action<bool>? ActiveChanged;
    public event Action? Ended;

    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed) return;
            _started = true;
        }

        var bounds = _screen.GetDesktopBounds();
        var cursor = _screen.GetCursorPosition();
        lock (_lock)
        {
            _virtualX = cursor.X;
            _virtualY = cursor.Y;
        }

        _outbox.Writer.TryWrite(new ScreenInfoMessage(bounds.Width, bounds.Height));
        _ = Task.Run(SendLoopAsync);
        _ = Task.Run(ReadLoopAsync);

        _clipboard?.Attach(_channel);
        Files?.Attach(_channel);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _clipboard?.Attach(null);
        DeactivateAndRelease();
        _outbox.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            await _channel.DisposeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SecondarySession] erro ao fechar o canal: {ex.Message}");
        }

        RaiseEndedOnce();
    }

    private void HandleIncoming(Message message)
    {
        switch (message)
        {
            case EnterMessage enter:
                HandleEnter(enter);
                break;
            case MouseMoveMessage move:
                HandleMouseMove(move);
                break;
            case MouseButtonMessage button:
                HandleMouseButton(button);
                break;
            case MouseWheelMessage wheel:
                HandleMouseWheel(wheel);
                break;
            case KeyMessage key:
                HandleKey(key);
                break;
            case ReleaseAllMessage:
                DeactivateAndRelease();
                break;
            case PingMessage ping:
                _outbox.Writer.TryWrite(new PongMessage(ping.Timestamp));
                break;
            case ClipboardMessage clipboardMessage:
                _clipboard?.OnRemoteText(clipboardMessage.Text);
                break;
            case FileOfferMessage:
            case FileChunkMessage:
            case FileDoneMessage:
            case FileCancelMessage:
            case FileResultMessage:
                ForwardFileMessage(message);
                break;
        }
    }

    private void HandleEnter(EnterMessage enter)
    {
        var bounds = _screen.GetDesktopBounds();
        var (x, y) = ScreenLayout.PointOnEdge(bounds, enter.PrimarySide, enter.Position, 2);

        lock (_lock)
        {
            PrimarySide = enter.PrimarySide;
            _isActive = true;
            _virtualX = x;
            _virtualY = y;
        }

        Inject(i => i.MoveMouseAbsolute(x, y));
        ActiveChanged?.Invoke(true);
    }

    private void HandleMouseMove(MouseMoveMessage move)
    {
        if (!IsActive) return;

        var bounds = _screen.GetDesktopBounds();
        int x, y;
        bool leaving;
        ScreenSide side;

        lock (_lock)
        {
            if (!_isActive) return;

            side = PrimarySide;
            int nx = _virtualX + move.DX;
            int ny = _virtualY + move.DY;
            leaving = side switch
            {
                ScreenSide.Left => nx < bounds.X,
                ScreenSide.Right => nx > bounds.Right,
                ScreenSide.Top => ny < bounds.Y,
                ScreenSide.Bottom => ny > bounds.Bottom,
                _ => false
            };

            _virtualX = Math.Clamp(nx, bounds.X, bounds.Right);
            _virtualY = Math.Clamp(ny, bounds.Y, bounds.Bottom);
            x = _virtualX;
            y = _virtualY;

            if (leaving)
            {
                var position = ScreenLayout.PositionAlongEdge(bounds, side, x, y);
                _outbox.Writer.TryWrite(new LeaveMessage(position));
                _isActive = false;
            }
        }

        if (leaving)
        {
            ReleaseAllPressed();
            ActiveChanged?.Invoke(false);
        }
        else
        {
            Inject(i => i.MoveMouseAbsolute(x, y));
        }
    }

    private void HandleMouseButton(MouseButtonMessage button)
    {
        lock (_lock)
        {
            if (!_isActive) return;
            if (button.Down) _pressedButtons.Add(button.Button);
            else _pressedButtons.Remove(button.Button);
        }

        Inject(i => i.MouseButton(button.Button, button.Down));
    }

    private void HandleMouseWheel(MouseWheelMessage wheel)
    {
        if (!IsActive) return;
        Inject(i => i.MouseWheel(wheel.DX, wheel.DY));
    }

    private void HandleKey(KeyMessage key)
    {
        lock (_lock)
        {
            if (!_isActive) return;
            if (key.Down) _pressedKeys.Add(key.Hid);
            else _pressedKeys.Remove(key.Hid);
        }

        Inject(i => i.Key(key.Hid, key.Down));
    }

    /// <summary>Solta todas as teclas e botões pressionados. Não chamar com o lock tomado.</summary>
    private void ReleaseAllPressed()
    {
        List<ushort> keys;
        List<MouseButton> buttons;
        lock (_lock)
        {
            keys = _pressedKeys.ToList();
            buttons = _pressedButtons.ToList();
            _pressedKeys.Clear();
            _pressedButtons.Clear();
        }

        foreach (var button in buttons)
            Inject(i => i.MouseButton(button, false));
        foreach (var key in keys)
            Inject(i => i.Key(key, false));
    }

    private void DeactivateAndRelease()
    {
        ReleaseAllPressed();

        bool wasActive;
        lock (_lock)
        {
            wasActive = _isActive;
            _isActive = false;
        }

        if (wasActive)
            ActiveChanged?.Invoke(false);
    }

    private void Inject(Action<IInputInjector> action)
    {
        try
        {
            action(_injector);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SecondarySession] falha ao injetar entrada: {ex.Message}");
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var message in _channel.ReadAllAsync(_cts.Token))
                HandleIncoming(message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SecondarySession] leitura encerrada: {ex.Message}");
        }
        finally
        {
            DeactivateAndRelease();
            RaiseEndedOnce();
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync())
            {
                try
                {
                    await _channel.SendAsync(message);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SecondarySession] envio falhou: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SecondarySession] fila de envio encerrada: {ex.Message}");
        }
    }

    private void ForwardFileMessage(Message message)
    {
        try
        {
            Files?.OnRemoteMessage(message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Session] falha ao processar arquivo: {ex.Message}");
        }
    }

    private void RaiseEndedOnce()
    {
        lock (_lock)
        {
            if (_endedRaised) return;
            _endedRaised = true;
        }

        try
        {
            Files?.Detach(_channel);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Session] falha ao desligar transferência: {ex.Message}");
        }

        Ended?.Invoke();
    }
}

/// <summary>
/// Sincroniza o texto da área de transferência. Verifica o clipboard local a cada 500 ms; se mudou (e não foi
/// a gente que acabou de escrever), envia ClipboardMessage pelo canal. Ao receber, escreve no clipboard local e
/// lembra o texto para não ecoar. Ignora textos vazios e maiores que 512 KB.
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    private const int MaxBytes = 512 * 1024;

    private readonly IClipboard _clipboard;
    private readonly object _lock = new();
    private readonly System.Threading.Timer _timer;
    private IMessageChannel? _channel;
    private string? _lastText;
    private bool _baselineTaken;
    private bool _remoteWriteInProgress;
    private int _remoteWriteSeq;
    private bool _disposed;

    public ClipboardSync(IClipboard clipboard)
    {
        _clipboard = clipboard;
        _timer = new System.Threading.Timer(_ => Poll(), null, 500, 500);
    }

    /// <summary>Liga ao canal (pode trocar de canal ao reconectar). null = desliga.</summary>
    public void Attach(IMessageChannel? channel)
    {
        lock (_lock)
        {
            _channel = channel;
            _baselineTaken = false;
            _lastText = null;
        }
    }

    /// <summary>Chamado pelas sessões quando chega um ClipboardMessage.</summary>
    public void OnRemoteText(string text)
    {
        if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > MaxBytes) return;

        lock (_lock)
        {
            if (_disposed) return;
            _lastText = text;
            _baselineTaken = true;
            if (!Enabled) return;
            _remoteWriteInProgress = true;
            _remoteWriteSeq++;
        }

        try
        {
            _clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardSync] falha ao escrever no clipboard: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _remoteWriteInProgress = false;
            }
        }
    }

    public bool Enabled { get; set; } = true;

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _channel = null;
        }

        _timer.Dispose();
    }

    private void Poll()
    {
        IMessageChannel? channel = null;
        string? toSend = null;

        try
        {
            int seqBefore;
            lock (_lock)
            {
                if (_disposed || _channel == null || _remoteWriteInProgress) return;
                seqBefore = _remoteWriteSeq;
            }

            var text = _clipboard.GetText();

            lock (_lock)
            {
                if (_disposed || _channel == null || _remoteWriteInProgress) return;
                // Se houve escrita remota durante a leitura, o texto lido pode ser o antigo: pula este ciclo.
                if (_remoteWriteSeq != seqBefore) return;

                if (!_baselineTaken)
                {
                    _baselineTaken = true;
                    _lastText = text;
                    return;
                }

                if (string.Equals(text, _lastText, StringComparison.Ordinal)) return;

                _lastText = text;
                if (!Enabled || string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > MaxBytes) return;

                channel = _channel;
                toSend = text;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardSync] falha ao ler o clipboard: {ex.Message}");
        }

        if (channel != null && toSend != null)
            _ = SendTextAsync(channel, toSend);
    }

    private static async Task SendTextAsync(IMessageChannel channel, string text)
    {
        try
        {
            await channel.SendAsync(new ClipboardMessage(text));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardSync] envio falhou: {ex.Message}");
        }
    }
}
