using Avalonia.Controls;
using Avalonia.Interactivity;
using ConnectDesktop.App.Services;
using ConnectDesktop.Core.Layout;
using ConnectDesktop.Core.Net;
using ConnectDesktop.Core.Update;

namespace ConnectDesktop.App.Views;

/// <summary>
/// Tela Conexão: nome e papel deste PC; no secundário, o código de pareamento; no principal, a posição do outro PC.
/// Todo o texto dinâmico vem de Loc; a tela se redesenha em StateChanged e LanguageChanged.
/// </summary>
public partial class ConnectionView : UserControl
{
    private readonly AppController _controller;
    private bool _loading;
    private string _status = "";
    private ReleaseInfo? _release;
    private string? _warning;
    private bool _warningIsError;

    /// <summary>Construtor usado pelo carregador de XAML. O controlador vem de <see cref="App.Controller"/>.</summary>
    public ConnectionView() : this(App.Controller ?? throw new InvalidOperationException("O controlador do aplicativo não foi criado."))
    {
    }

    public ConnectionView(AppController controller)
    {
        _controller = controller;
        InitializeComponent();

        _loading = true;
        NameBox.Text = _controller.Settings.DeviceName;
        ClipboardCheck.IsChecked = _controller.Settings.ShareClipboard;
        UpdatesCheck.IsChecked = _controller.Settings.CheckUpdatesOnStart;
        _loading = false;

        _controller.StatusChanged += OnStatusChanged;
        _controller.PairingCodeChanged += OnPairingCodeChanged;
        _controller.WarningChanged += OnWarningChanged;
        _controller.UpdateAvailable += OnUpdateAvailable;
        _controller.StateChanged += Refresh;
        Loc.Instance.LanguageChanged += Refresh;

        Refresh();
    }

    /// <summary>Pede a troca de página do shell: "devices" ou "pairs".</summary>
    public event Action<string>? NavigateRequested;

    // ---------------- Eventos do controlador (thread da UI) ----------------

    private void OnStatusChanged(string text)
    {
        _status = text;
        StatusText.Text = text;
    }

    private void OnPairingCodeChanged(string? code) => CodeText.Text = FormatCode(code);

    private void OnWarningChanged(string? text, bool isError)
    {
        _warning = text;
        _warningIsError = isError;
        RenderWarning();
    }

    private void OnUpdateAvailable(ReleaseInfo release)
    {
        _release = release;
        UpdateProgress.IsVisible = false;
        UpdateButton.IsEnabled = true;
        RenderUpdate();
    }

    // ---------------- Desenho ----------------

    /// <summary>Redesenha o que depende do estado (papel, conexão, posição, avisos).</summary>
    private void Refresh()
    {
        var primary = _controller.Settings.Role == DeviceRole.Primary;
        var wasLoading = _loading;
        _loading = true;
        PrimaryRadio.IsChecked = primary;
        SecondaryRadio.IsChecked = !primary;
        _loading = wasLoading;

        PairCard.IsVisible = !primary;
        PositionCard.IsVisible = primary;

        var connected = primary ? _controller.IsPrimaryConnected : _controller.IsSecondaryConnected;
        Spinner.IsVisible = !connected;
        StatusDot.IsVisible = connected;
        StatusText.Text = _status;

        RefreshPosition(connected);
        RenderWarning();
        RenderUpdate();
    }

    private void RefreshPosition(bool connected)
    {
        SummaryText.Text = connected
            ? Loc.Instance.Format("conn.summary_connected", _controller.ConnectedPeerName ?? string.Empty)
            : Loc.Instance["conn.summary_none"];
        DisconnectButton.IsVisible = connected;
        ChooseDeviceLink.IsVisible = !connected;

        var selected = _controller.Settings.SecondarySide;
        ApplySide(LeftSide, "conn.side_left", ScreenSide.Left, selected);
        ApplySide(RightSide, "conn.side_right", ScreenSide.Right, selected);
        ApplySide(TopSide, "conn.side_top", ScreenSide.Top, selected);
        ApplySide(BottomSide, "conn.side_bottom", ScreenSide.Bottom, selected);
    }

    private static void ApplySide(Button button, string labelKey, ScreenSide side, ScreenSide selected)
    {
        var isSelected = side == selected;
        button.Content = isSelected ? Loc.Instance["conn.other_pc"] : Loc.Instance[labelKey];
        button.Classes.Set("selected", isSelected);
    }

    private void RenderWarning()
    {
        WarningBorder.IsVisible = !string.IsNullOrEmpty(_warning);
        WarningBorder.Classes.Set("error", _warningIsError);
        WarningText.Text = _warning ?? string.Empty;
    }

    private void RenderUpdate()
    {
        var release = _release;
        UpdateBanner.IsVisible = release is not null;
        if (release is not null)
            UpdateText.Text = Loc.Instance.Format("conn.update_banner", release.Version);
    }

    private static string FormatCode(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return "--- ---";
        return code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
    }

    // ---------------- Ações ----------------

    private void OnSaveNameClick(object? sender, RoutedEventArgs e)
    {
        _controller.SetDeviceName(NameBox.Text ?? string.Empty);
        NameBox.Text = _controller.Settings.DeviceName;
    }

    private void OnRoleChecked(object? sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        var role = ReferenceEquals(sender, PrimaryRadio) ? DeviceRole.Primary : DeviceRole.Secondary;
        if (role == _controller.Settings.Role)
            return;

        _ = _controller.SetRoleAsync(role);
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        var digits = (CodeText.Text ?? string.Empty).Replace(" ", string.Empty);
        if (digits.Length == 0 || digits.Contains('-'))
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(digits);
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
        Refresh();
    }

    private void OnDisconnectClick(object? sender, RoutedEventArgs e)
        => _ = _controller.DisconnectAsync();

    private void OnChooseDeviceClick(object? sender, RoutedEventArgs e)
        => NavigateRequested?.Invoke("devices");

    private void OnPairsClick(object? sender, RoutedEventArgs e)
        => NavigateRequested?.Invoke("pairs");

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
            UpdateInfoText.Text = Loc.Instance["conn.updates_unavailable"];
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        UpdateInfoText.Text = Loc.Instance["conn.checking"];

        var release = await _controller.CheckForUpdatesAsync();

        CheckUpdatesButton.IsEnabled = true;
        UpdateInfoText.Text = release is null
            ? Loc.Instance.Format("conn.up_to_date", _controller.AppVersion)
            : Loc.Instance.Format("conn.update_found", release.Version);
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
            UpdateInfoText.Text = Loc.Instance.Format("conn.update_failed", ex.Message);
        }
    }
}
