using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using ConnectDesktop.App.Services;
using ConnectDesktop.Core.Layout;
using ConnectDesktop.Core.Net;
using ConnectDesktop.Core.Platform;
using ConnectDesktop.Core.Security;
using ConnectDesktop.Core.Session;
using ConnectDesktop.Core.Settings;
using ConnectDesktop.Core.Transfer;
using ConnectDesktop.Core.Update;

namespace ConnectDesktop.App;

/// <summary>
/// Lógica do app sem UI: liga a interface aos serviços do Core (descoberta, pareamento, sessões e atualizações).
/// Os eventos são sempre disparados na thread da UI (callbacks do Core passam por Dispatcher.UIThread.Post).
/// Cada chamada ao Core é protegida: se algo lançar, o erro vai para o log e para o status, sem derrubar o app.
/// </summary>
public sealed class AppController
{
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CodeRenewInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HandshakeRenewInterval = TimeSpan.FromSeconds(3);
    private const string WrongCodeMessage = "Código de pareamento incorreto.";

    /// <summary>Mensagens fixas do Core (em português) e a chave de tradução de cada uma.</summary>
    private static readonly Dictionary<string, string> CoreMessageKeys = new()
    {
        ["Este PC não reconhece mais o pareamento. Pareie de novo."] = "err.not_recognized",
        ["Código de pareamento incorreto."] = "err.wrong_code",
        ["O outro PC não confirmou a identidade."] = "err.not_confirmed",
        ["Pareamento cancelado."] = "err.cancelled",
        ["O outro PC não reconhece este pareamento. Esqueça o pareamento em 'Pares salvos' e conecte de novo para parear com código."] = "err.unknown_pairing",
        ["Pareamento recusado pelo outro PC."] = "err.refused",
    };

    private DateTime _lastHandshakeRenewUtc = DateTime.MinValue;

    private readonly DispatcherTimer _codeTimer;
    private readonly DispatcherTimer _reconnectTimer;

    private volatile string _pairingCode = "";
    private IPlatform? _platform;
    private DiscoveryService? _discovery;
    private ControlListener? _listener;

    private PrimarySession? _primarySession;
    private IInputCapture? _capture;
    private ClipboardSync? _primaryClipboard;
    private string? _primaryPeerId;
    private string? _primaryPeerName;
    private PeerInfo? _primaryPeer;
    private bool _connecting;
    private bool _autoReconnectSuppressed;
    private DateTime _lastAutoAttemptUtc = DateTime.MinValue;

    private SecondarySession? _secondarySession;
    private ClipboardSync? _secondaryClipboard;
    private string? _secondaryPeerName;

    private bool _permissionsOk;
    private string? _permissionMessage;
    private bool _restartPending;
    private ReleaseInfo? _pendingUpdate;

    public AppController()
    {
        AppVersion = ComputeVersion();
        Settings = LoadSettings();
        Store = CreateStore();
        Files = new FileTransferService(EffectiveDownloadDir());

        _codeTimer = new DispatcherTimer { Interval = CodeRenewInterval };
        _codeTimer.Tick += (_, _) => RenewPairingCode();

        _reconnectTimer = new DispatcherTimer { Interval = ReconnectInterval };
        _reconnectTimer.Tick += (_, _) => TryAutoReconnect();

        // Qualquer mudança de estado avisa a janela por StateChanged.
        StatusChanged += _ => RaiseStateChanged();
        PairingCodeChanged += _ => RaiseStateChanged();
        PeersChanged += _ => RaiseStateChanged();
        ConnectionChanged += RaiseStateChanged;
        WarningChanged += (_, _) => RaiseStateChanged();
        UpdateAvailable += _ => RaiseStateChanged();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Configurações carregadas (a UI lê daqui).</summary>
    public AppSettings Settings { get; }

    /// <summary>Pareamentos salvos. Null se o armazenamento não pôde ser aberto.</summary>
    public PairingStore? Store { get; }

    /// <summary>Envio e recebimento de arquivos (compartilhado pelas sessões).</summary>
    public FileTransferService Files { get; }

    /// <summary>Versão "Major.Minor.Build" do assembly de entrada.</summary>
    public string AppVersion { get; }

    /// <summary>Janela usada como dono do diálogo de código de pareamento.</summary>
    public Window? OwnerWindow { get; set; }

    /// <summary>Pedido de encerramento do app (usado pela atualização no Windows).</summary>
    public Action? RequestShutdown { get; set; }

    /// <summary>Lista de PCs secundários encontrados na rede (só no papel principal).</summary>
    public IReadOnlyList<PeerInfo> SecondaryPeers
    {
        get
        {
            try
            {
                var all = _discovery?.Peers;
                if (all is null)
                    return Array.Empty<PeerInfo>();
                return all.Where(p => p.Role == DeviceRole.Secondary).ToList();
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao ler os PCs encontrados.", ex);
                return Array.Empty<PeerInfo>();
            }
        }
    }

    public bool IsPrimaryConnected => _primarySession is not null;

    public string? ConnectedPeerId => _primarySession is null ? null : _primaryPeerId;

    /// <summary>PC principal conectado (só no papel principal), ou null.</summary>
    public PeerInfo? ConnectedPeer => _primarySession is null ? null : _primaryPeer;

    /// <summary>true quando o PC secundário está com uma sessão aberta.</summary>
    public bool IsSecondaryConnected => _secondarySession is not null;

    /// <summary>Nome do outro PC na sessão atual (principal ou secundário), ou null.</summary>
    public string? ConnectedPeerName
        => _primarySession is not null ? _primaryPeerName
           : _secondarySession is not null ? _secondaryPeerName
           : null;

    /// <summary>Todos os PCs encontrados na rede, com o papel de cada um.</summary>
    public IReadOnlyList<PeerInfo> AllPeers
    {
        get
        {
            try
            {
                return _discovery?.Peers ?? Array.Empty<PeerInfo>();
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao ler os PCs encontrados.", ex);
                return Array.Empty<PeerInfo>();
            }
        }
    }

    /// <summary>Status em texto para a linha de status da janela (papel atual).</summary>
    public event Action<string>? StatusChanged;
    /// <summary>Código de pareamento atual do PC secundário (null = nenhum).</summary>
    public event Action<string?>? PairingCodeChanged;
    /// <summary>Lista de PCs secundários encontrados.</summary>
    public event Action<IReadOnlyList<PeerInfo>>? PeersChanged;
    /// <summary>Conexão do principal entrou ou saiu (ver IsPrimaryConnected).</summary>
    public event Action? ConnectionChanged;
    /// <summary>Aviso fixo da janela. text null = sem aviso. isError true = permissão faltando.</summary>
    public event Action<string?, bool>? WarningChanged;
    /// <summary>Nova versão encontrada no GitHub.</summary>
    public event Action<ReleaseInfo>? UpdateAvailable;
    /// <summary>Qualquer mudança (status, PCs, conexão, código, pareamentos, configurações). Sempre na thread da UI.</summary>
    public event Action? StateChanged;

    /// <summary>Carrega serviços conforme o papel salvo. Chamar uma vez, com a janela já criada.</summary>
    public void Start()
    {
        Log.Info($"Connect Desktop {AppVersion} iniciado (papel: {Settings.Role}).");

        try
        {
            _platform = PlatformFactory.Create();
        }
        catch (Exception ex)
        {
            ReportError("err.act.start_system", ex);
            return;
        }

        StartServices();

        if (Settings.CheckUpdatesOnStart)
            _ = CheckUpdatesAfterStartAsync();
    }

    /// <summary>Encerra serviços e sessões (usado ao sair do app).</summary>
    public async Task ShutdownAsync()
    {
        await StopServicesAsync();
        try
        {
            Files.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao encerrar a transferência de arquivos.", ex);
        }
        Log.Info("Connect Desktop encerrado.");
    }

    // ---------------- Papel e serviços ----------------

    /// <summary>Troca o papel: encerra o que estiver rodando, salva e reinicia os serviços.</summary>
    public async Task SetRoleAsync(DeviceRole role)
    {
        if (role == Settings.Role)
            return;

        Settings.Role = role;
        SaveSettings();
        Log.Info($"Papel alterado para {role}.");
        await StopServicesAsync();
        StartServices();
    }

    /// <summary>Lado do secundário visto do principal. Aplica ao vivo na sessão ativa.</summary>
    public void SetSecondarySide(ScreenSide side)
    {
        if (side == Settings.SecondarySide)
            return;

        Settings.SecondarySide = side;
        SaveSettings();

        var session = _primarySession;
        if (session is null)
            return;
        try
        {
            session.SecondarySide = side;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao aplicar o lado na sessão ativa.", ex);
        }
    }

    /// <summary>Nome deste PC. Se houver sessão ativa, os serviços reiniciam quando ela terminar.</summary>
    public void SetDeviceName(string name)
    {
        var clean = name.Trim();
        if (clean.Length == 0)
            clean = Environment.MachineName;
        if (clean == Settings.DeviceName)
            return;

        Settings.DeviceName = clean;
        SaveSettings();

        if (IsSessionActive)
        {
            _restartPending = true;
            return;
        }
        _ = RestartServicesAsync();
    }

    public void SetShareClipboard(bool enabled)
    {
        Settings.ShareClipboard = enabled;
        SaveSettings();
        if (_primaryClipboard is not null)
            _primaryClipboard.Enabled = enabled;
        if (_secondaryClipboard is not null)
            _secondaryClipboard.Enabled = enabled;
    }

    public void SetCheckUpdatesOnStart(bool enabled)
    {
        Settings.CheckUpdatesOnStart = enabled;
        SaveSettings();
    }

    /// <summary>Modo ("System", "Light" ou "Dark") e cor de destaque ("#RRGGBB"). Aplica na hora.</summary>
    public void SetTheme(string mode, string accentHex)
    {
        Settings.ThemeMode = mode;
        Settings.AccentColor = accentHex;
        SaveSettings();
        try
        {
            ThemeService.Apply(mode, accentHex);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao aplicar o tema.", ex);
        }
        RaiseStateChanged();
    }

    /// <summary>Idioma: "pt-BR", "en", "es", ou "" para o idioma do sistema. Aplica na hora.</summary>
    public void SetLanguage(string code)
    {
        Settings.Language = code;
        SaveSettings();
        Loc.Instance.SetLanguage(code);
        RaiseStateChanged();
    }

    /// <summary>Abrir com o sistema (e se abre só na bandeja).</summary>
    public void SetStartup(bool withSystem, bool minimized)
    {
        Settings.StartWithSystem = withSystem;
        Settings.StartMinimized = minimized;
        SaveSettings();
        if (!StartupService.SetEnabled(withSystem, minimized))
            Log.Info("Não foi possível alterar a inicialização com o sistema.");
        RaiseStateChanged();
    }

    /// <summary>Pasta de downloads de arquivos recebidos. "" = pasta padrão.</summary>
    public void SetDownloadDir(string dir)
    {
        Settings.DownloadDir = dir.Trim();
        SaveSettings();
        try
        {
            Files.DownloadDir = EffectiveDownloadDir();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao trocar a pasta de downloads.", ex);
        }
        RaiseStateChanged();
    }

    /// <summary>Esquece um pareamento salvo.</summary>
    public void ForgetPair(string deviceId)
    {
        try
        {
            Store?.Remove(deviceId);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao esquecer o pareamento.", ex);
            StatusChanged?.Invoke(Loc.Instance.Format("err.forget_failed", Describe(ex)));
            return;
        }
        RaiseStateChanged();
    }

    private bool IsSessionActive => _primarySession is not null || _secondarySession is not null;

    private void StartServices()
    {
        if (_platform is null)
            return;

        _permissionsOk = CheckPermissions();
        var identity = Settings.ToIdentity();

        try
        {
            _discovery = new DiscoveryService(identity, Settings.Role, AppVersion);
            _discovery.PeersChanged += () => Dispatcher.UIThread.Post(() => OnPeersChanged());
            _discovery.Start();
        }
        catch (Exception ex)
        {
            DisposeDiscovery();
            ReportError("err.act.discovery", ex);
        }

        if (Settings.Role == DeviceRole.Secondary)
            StartSecondary(identity);
        else
            StartPrimary();
    }

    private void StartSecondary(DeviceIdentity identity)
    {
        try
        {
            var store = Store;
            if (store is null)
                throw new InvalidOperationException(Loc.Instance["err.store_short"]);

            RenewPairingCode();
            _codeTimer.Start();

            _listener = new ControlListener(identity, store, () => _pairingCode);
            _listener.ChannelAccepted += channel => Dispatcher.UIThread.Post(() => OnChannelAccepted(channel));
            _listener.HandshakeFailed += message => Dispatcher.UIThread.Post(() => OnHandshakeFailed(message));
            _listener.Start();

            UpdateSecondaryStatus();
        }
        catch (Exception ex)
        {
            ReportError("err.act.secondary", ex);
        }
    }

    private void StartPrimary()
    {
        _reconnectTimer.Start();
        UpdatePrimaryStatus();
        OnPeersChanged();
    }

    private async Task StopServicesAsync()
    {
        _reconnectTimer.Stop();
        _codeTimer.Stop();

        await DisconnectInternalAsync();
        await DisposeSecondarySessionAsync();
        DisposeDiscovery();

        var listener = _listener;
        _listener = null;
        if (listener is not null)
        {
            try
            {
                await listener.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao parar o servidor de controle.", ex);
            }
        }

        PairingCodeChanged?.Invoke(null);
        PeersChanged?.Invoke(Array.Empty<PeerInfo>());
    }

    private async Task RestartServicesAsync()
    {
        await StopServicesAsync();
        StartServices();
    }

    private void DisposeDiscovery()
    {
        var discovery = _discovery;
        _discovery = null;
        try
        {
            discovery?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao parar a descoberta na rede.", ex);
        }
    }

    private bool CheckPermissions()
    {
        _permissionMessage = null;
        try
        {
            var platform = RequirePlatform();
            if (!platform.CheckPermissions(out var message))
            {
                _permissionMessage = string.IsNullOrWhiteSpace(message)
                    ? Loc.Instance["err.perm_default"]
                    : message;
                WarningChanged?.Invoke(_permissionMessage, true);
                return false;
            }

            WarningChanged?.Invoke(string.IsNullOrWhiteSpace(message) ? null : message, false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao verificar as permissões.", ex);
            _permissionMessage = Loc.Instance.Format("err.perm_check", Describe(ex));
            WarningChanged?.Invoke(_permissionMessage, true);
            return false;
        }
    }

    // ---------------- Papel secundário ----------------

    private void RenewPairingCode()
    {
        try
        {
            _pairingCode = PairingCode.Generate();
            PairingCodeChanged?.Invoke(_pairingCode);
        }
        catch (Exception ex)
        {
            ReportError("err.act.pairing_code", ex);
        }
    }

    private void OnHandshakeFailed(string message)
    {
        Log.Info($"Falha no pareamento: {message}");
        if (message != WrongCodeMessage)
            return;

        if (DateTime.UtcNow - _lastHandshakeRenewUtc >= HandshakeRenewInterval)
        {
            _lastHandshakeRenewUtc = DateTime.UtcNow;
            RenewPairingCode();
        }

        if (_secondarySession is null)
            StatusChanged?.Invoke(LocalizeError(message));
    }

    private void OnChannelAccepted(IMessageChannel channel)
    {
        var platform = _platform;
        if (Settings.Role != DeviceRole.Secondary || platform is null || !_permissionsOk)
        {
            _ = SafeDisposeAsync(channel);
            if (Settings.Role == DeviceRole.Secondary && !_permissionsOk)
                StatusChanged?.Invoke(_permissionMessage ?? Loc.Instance["status.perm_pending_secondary"]);
            return;
        }

        _ = DisposeSecondarySessionAsync();

        ClipboardSync? clipboard = null;
        SecondarySession? session = null;
        try
        {
            _secondaryPeerName = channel.RemoteDeviceName;
            clipboard = new ClipboardSync(platform.Clipboard) { Enabled = Settings.ShareClipboard };
            session = new SecondarySession(channel, platform.CreateInjector(), platform.Screen, clipboard);
            session.ActiveChanged += _ => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_secondarySession, session))
                    UpdateSecondaryStatus();
            });
            session.Ended += () => Dispatcher.UIThread.Post(() => OnSecondaryEnded(session));
            session.Files = Files;

            _secondarySession = session;
            _secondaryClipboard = clipboard;
            session.Start();

            Log.Info($"Conexão aceita de {_secondaryPeerName}.");
            UpdateSecondaryStatus();
        }
        catch (Exception ex)
        {
            if (session is null)
            {
                clipboard?.Dispose();
                _ = SafeDisposeAsync(channel);
            }
            else
            {
                _ = DisposeSecondarySessionAsync();
            }
            ReportError("err.act.accept", ex);
        }
    }

    private void OnSecondaryEnded(SecondarySession? session)
    {
        if (!ReferenceEquals(_secondarySession, session))
            return;

        Log.Info("Sessão com o PC principal encerrada.");
        _ = DisposeSecondarySessionAsync();
        UpdateSecondaryStatus();
        AfterSessionEnded();
    }

    private void UpdateSecondaryStatus()
    {
        var session = _secondarySession;
        if (session is null)
        {
            StatusChanged?.Invoke(Loc.Instance["status.waiting_primary"]);
            return;
        }

        var name = _secondaryPeerName ?? Loc.Instance["conn.default_primary"];
        StatusChanged?.Invoke(session.IsActive
            ? Loc.Instance.Format("status.controlled_by", name)
            : Loc.Instance.Format("status.connected_to", name));
    }

    private async Task DisposeSecondarySessionAsync()
    {
        var session = _secondarySession;
        var clipboard = _secondaryClipboard;
        _secondarySession = null;
        _secondaryClipboard = null;
        _secondaryPeerName = null;

        if (session is null && clipboard is null)
            return;

        try
        {
            if (session is not null)
                await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao encerrar a sessão do secundário.", ex);
        }

        try
        {
            clipboard?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao encerrar a área de transferência.", ex);
        }
    }

    // ---------------- Papel principal ----------------

    /// <summary>Conexão manual com um PC secundário (pede o código de pareamento se preciso).</summary>
    public Task ConnectAsync(PeerInfo peer)
    {
        _autoReconnectSuppressed = false;
        return ConnectToPeerAsync(peer, AskPairingCodeAsync, automatic: false);
    }

    /// <summary>Desconecta a sessão atual. Não reconecta sozinho até o usuário conectar de novo.</summary>
    public async Task DisconnectAsync()
    {
        _autoReconnectSuppressed = true;
        await DisconnectInternalAsync();
        UpdatePrimaryStatus();
        AfterSessionEnded();
    }

    private void OnPeersChanged()
    {
        PeersChanged?.Invoke(SecondaryPeers);
        TryAutoReconnect();
    }

    private void TryAutoReconnect()
    {
        try
        {
            if (Settings.Role != DeviceRole.Primary || _primarySession is not null || _connecting)
                return;
            if (_autoReconnectSuppressed || !_permissionsOk)
                return;

            var store = Store;
            var lastPeerId = Settings.LastPeerId;
            if (store is null || string.IsNullOrEmpty(lastPeerId))
                return;
            if (DateTime.UtcNow - _lastAutoAttemptUtc < ReconnectInterval)
                return;

            var peer = SecondaryPeers.FirstOrDefault(p => p.Id == lastPeerId);
            if (peer is null || store.GetKey(peer.Id) is null)
                return;

            _lastAutoAttemptUtc = DateTime.UtcNow;
            Log.Info($"Reconectando automaticamente a {peer.Name}.");
            _ = ConnectToPeerAsync(peer, () => Task.FromResult<string?>(null), automatic: true);
        }
        catch (Exception ex)
        {
            Log.Error("Falha na reconexão automática.", ex);
        }
    }

    private async Task ConnectToPeerAsync(PeerInfo peer, Func<Task<string?>> askPairingCode, bool automatic)
    {
        if (_connecting || Settings.Role != DeviceRole.Primary)
            return;

        if (!_permissionsOk)
        {
            if (!automatic)
                StatusChanged?.Invoke(_permissionMessage ?? Loc.Instance["status.perm_pending_primary"]);
            return;
        }

        var store = Store;
        var identity = Settings.ToIdentity();
        if (store is null)
        {
            if (!automatic)
                StatusChanged?.Invoke(Loc.Instance["err.store_unavailable"]);
            return;
        }

        if (_primarySession is not null)
        {
            if (automatic)
                return;
            await DisconnectInternalAsync();
        }

        _connecting = true;
        if (!automatic)
            StatusChanged?.Invoke(Loc.Instance.Format("status.connecting", peer.Name));

        try
        {
            var channel = await SecureChannel.ConnectAsync(peer.Address, peer.TcpPort, identity, store, askPairingCode);
            await StartPrimarySessionAsync(channel, peer);
        }
        catch (PairingException ex)
        {
            Log.Error($"Pareamento não concluído com {peer.Name}.", ex);
            if (!automatic)
                StatusChanged?.Invoke(LocalizeError(ex.Message));
        }
        catch (Exception ex)
        {
            Log.Error($"Falha ao conectar a {peer.Name}.", ex);
            if (!automatic)
                StatusChanged?.Invoke(Loc.Instance.Format("err.connect_failed", Describe(ex)));
        }
        finally
        {
            _connecting = false;
        }
    }

    private async Task StartPrimarySessionAsync(IMessageChannel channel, PeerInfo peer)
    {
        var platform = _platform;
        if (Settings.Role != DeviceRole.Primary || platform is null)
        {
            await SafeDisposeAsync(channel);
            return;
        }

        IInputCapture? capture = null;
        ClipboardSync? clipboard = null;
        PrimarySession? session = null;
        try
        {
            capture = platform.CreateCapture();
            clipboard = new ClipboardSync(platform.Clipboard) { Enabled = Settings.ShareClipboard };
            session = new PrimarySession(channel, capture, platform.Screen, Settings.SecondarySide, clipboard);
            session.StateChanged += _ => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_primarySession, session))
                    UpdatePrimaryStatus();
            });
            session.Ended += () => Dispatcher.UIThread.Post(() => OnPrimaryEnded(session));
            session.Files = Files;

            _primarySession = session;
            _capture = capture;
            _primaryClipboard = clipboard;
            _primaryPeerId = peer.Id;
            _primaryPeerName = peer.Name;
            _primaryPeer = peer;
            session.Start();

            Settings.LastPeerId = peer.Id;
            SaveSettings();

            Log.Info($"Conectado a {peer.Name}.");
            ConnectionChanged?.Invoke();
            UpdatePrimaryStatus();
        }
        catch
        {
            if (session is null)
            {
                try
                {
                    capture?.Dispose();
                    clipboard?.Dispose();
                }
                catch (Exception disposeEx)
                {
                    Log.Error("Falha ao liberar a captura.", disposeEx);
                }
                await SafeDisposeAsync(channel);
            }
            else
            {
                await DisconnectInternalAsync();
            }
            throw;
        }
    }

    private void UpdatePrimaryStatus()
    {
        if (Settings.Role != DeviceRole.Primary)
            return;

        var session = _primarySession;
        if (session is null)
        {
            StatusChanged?.Invoke(Loc.Instance["status.searching"]);
            return;
        }

        var name = _primaryPeerName ?? Loc.Instance["conn.default_secondary"];
        StatusChanged?.Invoke(session.State == PrimaryState.Remote
            ? Loc.Instance.Format("status.controlling", name)
            : Loc.Instance.Format("status.connected_to", name));
    }

    private void OnPrimaryEnded(PrimarySession? session)
    {
        if (!ReferenceEquals(_primarySession, session))
            return;

        Log.Info("Sessão com o PC secundário encerrada.");
        _ = DisconnectInternalAsync();
        UpdatePrimaryStatus();
        AfterSessionEnded();
    }

    private async Task DisconnectInternalAsync()
    {
        var session = _primarySession;
        var capture = _capture;
        var clipboard = _primaryClipboard;
        _primarySession = null;
        _capture = null;
        _primaryClipboard = null;
        _primaryPeerId = null;
        _primaryPeerName = null;
        _primaryPeer = null;

        if (session is null && capture is null && clipboard is null)
            return;

        ConnectionChanged?.Invoke();

        try
        {
            if (session is not null)
                await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao encerrar a sessão do principal.", ex);
        }

        try
        {
            capture?.Dispose();
            clipboard?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao liberar a captura.", ex);
        }
    }

    /// <summary>Aplica mudanças que ficaram pendentes enquanto havia sessão (ex.: renomear o PC).</summary>
    private void AfterSessionEnded()
    {
        if (_restartPending && !IsSessionActive)
        {
            _restartPending = false;
            _ = RestartServicesAsync();
        }
    }

    private Task<string?> AskPairingCodeAsync()
    {
        var owner = OwnerWindow;
        return Dispatcher.UIThread.InvokeAsync(() => PairingCodeDialog.AskAsync(owner));
    }

    // ---------------- Atualizações ----------------

    /// <summary>false quando o repositório ainda é o padrão "OWNER/..." (versão de desenvolvimento).</summary>
    public bool UpdatesEnabled
    {
        get
        {
            try
            {
                return !new UpdateChecker().Repo.StartsWith("OWNER/", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao ler o repositório de atualizações.", ex);
                return false;
            }
        }
    }

    /// <summary>Consulta o GitHub. Retorna a release nova, ou null se já está atualizado (ou se a consulta falhou).</summary>
    public async Task<ReleaseInfo?> CheckForUpdatesAsync()
    {
        if (!UpdatesEnabled)
            return null;

        try
        {
            var release = await new UpdateChecker().CheckAsync();
            if (release is not null)
            {
                _pendingUpdate = release;
                UpdateAvailable?.Invoke(release);
            }
            return release;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao verificar atualizações.", ex);
            return null;
        }
    }

    /// <summary>
    /// Windows: baixa o instalador, abre e encerra o app.
    /// Mac/Linux: abre a página da release no navegador.
    /// </summary>
    public async Task InstallUpdateAsync(IProgress<double> progress)
    {
        var release = _pendingUpdate ?? throw new InvalidOperationException(Loc.Instance["update.none"]);

        if (OperatingSystem.IsWindows())
        {
            var checker = new UpdateChecker();
            var installer = await checker.DownloadInstallerAsync(release, progress);
            checker.LaunchInstaller(installer);
            await StopServicesAsync();
            RequestShutdown?.Invoke();
            return;
        }

        var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        var info = new ProcessStartInfo(opener) { UseShellExecute = false };
        info.ArgumentList.Add(release.HtmlUrl);
        using var process = Process.Start(info);
    }

    private async Task CheckUpdatesAfterStartAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (Settings.CheckUpdatesOnStart && UpdatesEnabled)
            await CheckForUpdatesAsync();
    }

    // ---------------- Utilitários ----------------

    private IPlatform RequirePlatform()
        => _platform ?? throw new InvalidOperationException("a plataforma deste sistema não foi iniciada");

    /// <param name="actionKey">Chave Loc do que falhou (ex.: err.act.discovery).</param>
    private void ReportError(string actionKey, Exception ex)
    {
        Log.Error($"Falha ({actionKey}).", ex);
        StatusChanged?.Invoke(Loc.Instance.Format("err.report", Loc.Instance[actionKey], Describe(ex)));
    }

    private static string Describe(Exception ex)
        => ex is NotImplementedException ? Loc.Instance["err.not_available"] : LocalizeError(ex.Message);

    /// <summary>Mensagem do Core no idioma atual, quando conhecida; senão a original.</summary>
    private static string LocalizeError(string message)
        => CoreMessageKeys.TryGetValue(message, out var key) ? Loc.Instance[key] : message;

    private void RaiseStateChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            StateChanged?.Invoke();
        else
            Dispatcher.UIThread.Post(() => StateChanged?.Invoke());
    }

    private void OnLanguageChanged()
    {
        if (Settings.Role == DeviceRole.Secondary)
            UpdateSecondaryStatus();
        else
            UpdatePrimaryStatus();
        RaiseStateChanged();
    }

    private string EffectiveDownloadDir()
        => string.IsNullOrWhiteSpace(Settings.DownloadDir) ? FileTransferService.DefaultDownloadDir : Settings.DownloadDir;

    private static string ComputeVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    private static AppSettings LoadSettings()
    {
        try
        {
            return AppSettings.Load();
        }
        catch (Exception ex)
        {
            Log.Error("Não foi possível carregar as configurações. Usando valores padrão.", ex);
            return new AppSettings
            {
                DeviceId = Guid.NewGuid().ToString("N"),
                DeviceName = Environment.MachineName
            };
        }
    }

    private static PairingStore? CreateStore()
    {
        try
        {
            return new PairingStore(AppPaths.PairingsFile);
        }
        catch (Exception ex)
        {
            Log.Error("Não foi possível abrir os pareamentos salvos.", ex);
            return null;
        }
    }

    private void SaveSettings()
    {
        try
        {
            Settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Não foi possível salvar as configurações.", ex);
        }
    }

    private static async Task SafeDisposeAsync(IAsyncDisposable? disposable)
    {
        try
        {
            if (disposable is not null)
                await disposable.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao fechar a conexão.", ex);
        }
    }
}
