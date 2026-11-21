using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ConnectDesktop.Core.Layout;
using ConnectDesktop.Core.Net;
using ConnectDesktop.Core.Update;

namespace ConnectDesktop.App;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private bool _loading = true;
    private bool _allowClose;

    /// <summary>Construtor usado pelo carregador de XAML. O controlador vem de <see cref="App.Controller"/>.</summary>
    public MainWindow() : this(App.Controller ?? throw new InvalidOperationException("O controlador do aplicativo não foi criado."))
    {
    }

    public MainWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();

        Icon = AppIcon.Create();
        VersionText.Text = $"Versão {_controller.AppVersion}";
        NameBox.Text = _controller.Settings.DeviceName;
        ClipboardCheck.IsChecked = _controller.Settings.ShareClipboard;
        UpdatesCheck.IsChecked = _controller.Settings.CheckUpdatesOnStart;

        RefreshRoleView();
        RefreshSideButtons();
        RefreshConnectButton();
        RebuildPeers(_controller.SecondaryPeers);

        // Só ações do usuário depois que a janela abriu contam como mudança.
        Opened += (_, _) => _loading = false;

        _controller.StatusChanged += OnStatusChanged;
        _controller.PairingCodeChanged += OnPairingCodeChanged;
        _controller.PeersChanged += OnPeersChanged;
        _controller.ConnectionChanged += OnConnectionChanged;
        _controller.WarningChanged += OnWarningChanged;
        _controller.UpdateAvailable += OnUpdateAvailable;
    }

    /// <summary>Permite fechar a janela de verdade (usado ao sair pelo menu da bandeja ou ao atualizar).</summary>
    public void AllowClose() => _allowClose = true;

    // ---------------- Eventos do controlador (sempre na thread da UI) ----------------

    private void OnStatusChanged(string text) => StatusText.Text = text;

    private void OnPairingCodeChanged(string? code) => CodeText.Text = FormatCode(code);

    private void OnPeersChanged(IReadOnlyList<PeerInfo> peers) => RebuildPeers(peers);

    private void OnConnectionChanged()
    {
        RefreshConnectButton();
        RebuildPeers(_controller.SecondaryPeers);
    }

    private void OnWarningChanged(string? text, bool isError)
    {
        WarningBorder.IsVisible = !string.IsNullOrEmpty(text);
        WarningBorder.Background = new SolidColorBrush(Color.Parse(isError ? "#FDECEA" : "#E8F1FF"));
        WarningText.Text = text ?? string.Empty;
    }

    private void OnUpdateAvailable(ReleaseInfo release)
    {
        UpdateText.Text = $"Nova versão v{release.Version} disponível";
        UpdateButton.IsEnabled = true;
        UpdateProgress.IsVisible = false;
        UpdateBanner.IsVisible = true;
    }

    // ---------------- Papel e painéis ----------------

    private void OnRoleChecked(object? sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        var role = ReferenceEquals(sender, PrimaryRadio) ? DeviceRole.Primary : DeviceRole.Secondary;
        if (role == _controller.Settings.Role)
            return;

        _ = ApplyRoleAsync(role);
    }

    private async Task ApplyRoleAsync(DeviceRole role)
    {
        await _controller.SetRoleAsync(role);
        RefreshRoleView();
        RefreshConnectButton();
        RebuildPeers(_controller.SecondaryPeers);
    }

    private void RefreshRoleView()
    {
        var primary = _controller.Settings.Role == DeviceRole.Primary;
        PrimaryPanel.IsVisible = primary;
        SecondaryPanel.IsVisible = !primary;

        var wasLoading = _loading;
        _loading = true;
        PrimaryRadio.IsChecked = primary;
        SecondaryRadio.IsChecked = !primary;
        _loading = wasLoading;
    }

    private void RefreshConnectButton()
    {
        ConnectButton.Content = _controller.IsPrimaryConnected ? "Desconectar" : "Conectar";
    }

    private void RebuildPeers(IReadOnlyList<PeerInfo> peers)
    {
        var selectedId = (PeersList.SelectedItem as PeerItem)?.Peer.Id;
        PeersList.Items.Clear();
        foreach (var peer in peers)
        {
            var item = new PeerItem(peer, IsPaired(peer.Id));
            PeersList.Items.Add(item);
            if (peer.Id == selectedId)
                PeersList.SelectedItem = item;
        }

        PeersList.IsVisible = peers.Count > 0;
        EmptyPeersText.IsVisible = peers.Count == 0;
    }

    private bool IsPaired(string peerId)
    {
        try
        {
            return _controller.Store?.GetKey(peerId) is not null;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao consultar o pareamento.", ex);
            return false;
        }
    }

    private static string FormatCode(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return "--- ---";
        return code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_controller.IsPrimaryConnected)
        {
            _ = _controller.DisconnectAsync();
            return;
        }

        if (PeersList.SelectedItem is not PeerItem item)
        {
            StatusText.Text = "Selecione um PC secundário na lista.";
            return;
        }

        _ = _controller.ConnectAsync(item.Peer);
    }

    private void OnSideClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag)
            return;
        if (!Enum.TryParse<ScreenSide>(tag, out var side))
            return;
        if (_loading || side == _controller.Settings.SecondarySide)
            return;

        _controller.SetSecondarySide(side);
        RefreshSideButtons();
    }

    private void RefreshSideButtons()
    {
        var selected = _controller.Settings.SecondarySide;
        ApplySideButton(LeftButton, "Esquerda", ScreenSide.Left, selected);
        ApplySideButton(RightButton, "Direita", ScreenSide.Right, selected);
        ApplySideButton(TopButton, "Acima", ScreenSide.Top, selected);
        ApplySideButton(BottomButton, "Abaixo", ScreenSide.Bottom, selected);
    }

    private static void ApplySideButton(Button button, string label, ScreenSide side, ScreenSide selected)
    {
        var isSelected = side == selected;
        button.Content = isSelected ? "Outro PC" : label;
        button.Classes.Set("selected", isSelected);
    }

    // ---------------- Nome, opções e atualizações ----------------

    private void OnSaveNameClick(object? sender, RoutedEventArgs e)
    {
        _controller.SetDeviceName(NameBox.Text ?? string.Empty);
        NameBox.Text = _controller.Settings.DeviceName;
    }

    private void OnOptionToggled(object? sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        if (ReferenceEquals(sender, ClipboardCheck))
        {
            var value = ClipboardCheck.IsChecked == true;
            if (value != _controller.Settings.ShareClipboard)
                _controller.SetShareClipboard(value);
        }
        else if (ReferenceEquals(sender, UpdatesCheck))
        {
            var value = UpdatesCheck.IsChecked == true;
            if (value != _controller.Settings.CheckUpdatesOnStart)
                _controller.SetCheckUpdatesOnStart(value);
        }
    }

    private async void OnCheckUpdatesClick(object? sender, RoutedEventArgs e)
    {
        if (!_controller.UpdatesEnabled)
        {
            UpdateInfoText.Text = "Atualizações automáticas indisponíveis nesta versão de desenvolvimento.";
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        UpdateInfoText.Text = "Procurando atualizações…";

        var release = await _controller.CheckForUpdatesAsync();

        CheckUpdatesButton.IsEnabled = true;
        UpdateInfoText.Text = release is null
            ? $"Você já está na versão mais recente ({_controller.AppVersion})."
            : $"Nova versão v{release.Version} disponível.";
    }

    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.IsVisible = true;

        var progress = new Progress<double>(value => UpdateProgress.Value = value);
        try
        {
            await _controller.InstallUpdateAsync(progress);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao atualizar o aplicativo.", ex);
            UpdateButton.IsEnabled = true;
            UpdateProgress.IsVisible = false;
            UpdateInfoText.Text = $"Não foi possível atualizar: {ex.Message}";
        }
    }

    // ---------------- Pares salvos ----------------

    private void OnPairsToggleClick(object? sender, RoutedEventArgs e)
    {
        PairsPanel.IsVisible = !PairsPanel.IsVisible;
        if (PairsPanel.IsVisible)
            RefreshPairs();
    }

    private void RefreshPairs()
    {
        PairsList.Items.Clear();
        IReadOnlyList<(string DeviceId, string DeviceName)> pairs;
        try
        {
            pairs = _controller.Store?.List() ?? Array.Empty<(string DeviceId, string DeviceName)>();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao listar os pareamentos.", ex);
            pairs = Array.Empty<(string DeviceId, string DeviceName)>();
        }

        foreach (var (deviceId, deviceName) in pairs)
            PairsList.Items.Add(new PairItem(deviceId, deviceName));

        PairsEmptyText.IsVisible = pairs.Count == 0;
    }

    private void OnForgetPairClick(object? sender, RoutedEventArgs e)
    {
        if (PairsList.SelectedItem is not PairItem item)
            return;

        try
        {
            _controller.Store?.Remove(item.DeviceId);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao esquecer o pareamento.", ex);
            StatusText.Text = $"Não foi possível esquecer o pareamento: {ex.Message}";
            return;
        }

        RefreshPairs();
        RebuildPeers(_controller.SecondaryPeers);
    }

    // ---------------- Janela ----------------

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        Hide();
    }

    private sealed record PeerItem(PeerInfo Peer, bool Paired)
    {
        public override string ToString()
            => Paired ? $"{Peer.Name} · {Peer.Address} · pareado" : $"{Peer.Name} · {Peer.Address}";
    }

    private sealed record PairItem(string DeviceId, string DeviceName)
    {
        public override string ToString() => DeviceName;
    }
}
