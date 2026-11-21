using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ConnectDesktop.App.Services;
using ConnectDesktop.App.Views;
using ConnectDesktop.Core.Net;
using Path = Avalonia.Controls.Shapes.Path;

namespace ConnectDesktop.App;

/// <summary>
/// Janela principal: barra de título própria, menu lateral, área de conteúdo e barra inferior (aparência, tema, idioma).
/// Fechar a janela só esconde (o app continua na bandeja).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Cores do seletor livre (botão arco-íris), em hexadecimal.</summary>
    private static readonly string[] ColorChoices =
    {
        "#EF4444", "#F97316", "#F59E0B", "#EAB308", "#22C55E", "#10B981",
        "#14B8A6", "#06B6D4", "#3B82F6", "#6366F1", "#A855F7", "#EC4899"
    };

    private static readonly HashSet<string> SettingsKeys = new() { "general", "appearance", "language", "startup" };

    private readonly AppController _controller;
    private readonly ConnectionView _connectionView;
    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private readonly List<Button> _presetSwatches = new();
    private Button _customSwatch = null!;
    private bool _loading;
    private bool _allowClose;
    private string _status = "";
    private string? _warning;
    private bool _warningIsError;

    /// <summary>Quanto (px) o item do menu precisa andar para virar arrasto; menos que isso é clique.</summary>
    private const double NavDragThreshold = 6;
    private Button? _navPressedButton;
    private Point _navPressStart;
    private bool _navDragging;
    private bool _navSuppressClick;

    /// <summary>Construtor usado pelo carregador de XAML. O controlador vem de <see cref="App.Controller"/>.</summary>
    public MainWindow() : this(App.Controller ?? throw new InvalidOperationException("O controlador do aplicativo não foi criado."))
    {
    }

    public MainWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();

        Icon = AppIcon.Create();
        _navButtons["connection"] = NavConnection;
        _navButtons["devices"] = NavDevices;
        _navButtons["transfers"] = NavTransfers;
        _navButtons["pairs"] = NavPairs;
        _navButtons["general"] = NavGeneral;
        _navButtons["appearance"] = NavAppearance;
        _navButtons["language"] = NavLanguage;
        _navButtons["startup"] = NavStartup;
        _navButtons["about"] = NavAbout;

        BuildSwatches();
        BuildLanguages();
        AttachNavDrag();

        _connectionView = new ConnectionView(_controller);
        _connectionView.NavigateRequested += ShowPage;
        _pages["connection"] = _connectionView;

        _controller.StatusChanged += OnStatusChanged;
        _controller.WarningChanged += OnWarningChanged;
        _controller.StateChanged += RefreshShell;
        Loc.Instance.LanguageChanged += RefreshShell;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                RefreshMaximizeIcon();
        };

        Opened += OnOpened;
        ShowPage(App.StartPage);
        SetSettingsExpanded(true);
        RefreshShell();
    }

    /// <summary>Tamanho inicial de 1000x1000, limitado a 92% da área de trabalho, centralizado na tela.</summary>
    private void OnOpened(object? sender, EventArgs e)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = Math.Min(1000, area.Width / scale * 0.92);
        var height = Math.Min(1000, area.Height / scale * 0.92);
        Width = width;
        Height = height;
        Position = new PixelPoint(
            area.X + (int)((area.Width - width * scale) / 2),
            area.Y + (int)((area.Height - height * scale) / 2));
    }

    /// <summary>Permite fechar a janela de verdade (usado ao sair pelo menu da bandeja ou ao atualizar).</summary>
    public void AllowClose() => _allowClose = true;

    // ---------------- Eventos do controlador (thread da UI) ----------------

    private void OnStatusChanged(string text)
    {
        _status = text;
        RefreshStatusPill();
    }

    private void OnWarningChanged(string? text, bool isError)
    {
        _warning = text;
        _warningIsError = isError;
        RefreshStatusPill();
    }

    // ---------------- Desenho do shell ----------------

    /// <summary>Atualiza tudo que depende de estado, configurações ou idioma.</summary>
    private void RefreshShell()
    {
        VersionText.Text = Loc.Instance.Format("app.version", _controller.AppVersion);
        // Durante um arrasto a ordem visual é a do arrasto; a salva entra no fim do arrasto.
        if (!_navDragging)
            ApplyNavOrder(_controller.NavOrder);
        RefreshStatusPill();
        RefreshModeButtons();
        RefreshSwatches();
        RefreshLanguageCombo();
    }

    private void RefreshStatusPill()
    {
        var primary = _controller.Settings.Role == DeviceRole.Primary;
        var connected = primary ? _controller.IsPrimaryConnected : _controller.IsSecondaryConnected;
        var hasError = _warningIsError && !string.IsNullOrEmpty(_warning);

        string text;
        string state;
        if (hasError)
        {
            text = Loc.Instance["pill.error"];
            state = "error";
        }
        else if (connected)
        {
            // Controlando: o status atual é o de controle (principal controla ou secundário é controlado).
            var peer = _controller.ConnectedPeerName ?? string.Empty;
            var controlling = _status == Loc.Instance.Format("status.controlling", peer)
                || _status == Loc.Instance.Format("status.controlled_by", peer);
            text = Loc.Instance[controlling ? "pill.controlling" : "pill.connected"];
            state = "ok";
        }
        else if (primary && _controller.SecondaryPeers.Count > 0)
        {
            text = Loc.Instance["pill.ready"];
            state = "ok";
        }
        else if (primary)
        {
            text = Loc.Instance["pill.searching"];
            state = "wait";
        }
        else
        {
            text = Loc.Instance["pill.waiting"];
            state = "wait";
        }

        StatusPillText.Text = text;
        StatusDot.Classes.Set("ok", state == "ok");
        StatusDot.Classes.Set("error", state == "error");
    }

    private void RefreshModeButtons()
    {
        var mode = _controller.Settings.ThemeMode;
        ModeSystem.Classes.Set("selected", mode == "System");
        ModeLight.Classes.Set("selected", mode == "Light");
        ModeDark.Classes.Set("selected", mode == "Dark");
    }

    private void BuildSwatches()
    {
        foreach (var hex in ThemeService.PresetAccents)
        {
            var swatch = new Button
            {
                Tag = hex,
                Background = new SolidColorBrush(Color.Parse(hex))
            };
            swatch.Classes.Add("swatch");
            swatch.Click += OnSwatchClick;
            ThemeSwatches.Children.Add(swatch);
            _presetSwatches.Add(swatch);
        }

        _customSwatch = new Button
        {
            Tag = "custom",
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#EF4444"), 0),
                    new GradientStop(Color.Parse("#F59E0B"), 0.25),
                    new GradientStop(Color.Parse("#22C55E"), 0.5),
                    new GradientStop(Color.Parse("#3B82F6"), 0.75),
                    new GradientStop(Color.Parse("#A855F7"), 1)
                }
            }
        };
        _customSwatch.Classes.Add("swatch");
        ThemeSwatches.Children.Add(_customSwatch);
    }

    private void RefreshSwatches()
    {
        var accent = _controller.Settings.AccentColor;
        var isPreset = false;
        foreach (var swatch in _presetSwatches)
        {
            var hex = (string)swatch.Tag!;
            var selected = string.Equals(hex, accent, StringComparison.OrdinalIgnoreCase);
            isPreset |= selected;
            swatch.Classes.Set("selected", selected);
            swatch.Content = selected ? CheckMark() : null;
        }

        var customSelected = !isPreset;
        _customSwatch.Classes.Set("selected", customSelected);
        _customSwatch.Content = customSelected ? CheckMark() : null;

        // O painel de cores é refeito a cada atualização para usar o idioma atual.
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        flyout.Content = BuildColorPanel(flyout, accent);
        _customSwatch.Flyout = flyout;
    }

    private void BuildLanguages()
    {
        foreach (var (code, name) in Loc.Languages)
            LanguageCombo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
    }

    private void RefreshLanguageCombo()
    {
        var code = string.IsNullOrEmpty(_controller.Settings.Language)
            ? Loc.Instance.CurrentLanguage
            : _controller.Settings.Language;

        var wasLoading = _loading;
        _loading = true;
        foreach (var item in LanguageCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag as string == code)
                LanguageCombo.SelectedItem = item;
        }
        _loading = wasLoading;
    }

    private Control CheckMark()
    {
        return new Path
        {
            Data = this.FindResource("IconCheck") as Geometry,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Stroke = Brushes.White,
            StrokeThickness = 2.2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
    }

    /// <summary>Painel do seletor de cor: campo hex, botão Aplicar e 12 cores prontas.</summary>
    private Control BuildColorPanel(Flyout flyout, string accent)
    {
        var title = new TextBlock
        {
            Text = Loc.Instance["shell.color_title"],
            FontWeight = FontWeight.SemiBold
        };

        var grid = new UniformGrid { Columns = 6 };
        foreach (var hex in ColorChoices)
        {
            var choice = new Button
            {
                Tag = hex,
                Background = new SolidColorBrush(Color.Parse(hex)),
                Margin = new Thickness(3)
            };
            choice.Classes.Add("swatch");
            choice.Click += (_, _) =>
            {
                flyout.Hide();
                _controller.SetTheme(_controller.Settings.ThemeMode, hex);
            };
            grid.Children.Add(choice);
        }

        var hexBox = new TextBox
        {
            Classes = { "field" },
            Watermark = "#RRGGBB",
            Text = accent,
            MaxLength = 7,
            Width = 150
        };
        var error = new TextBlock
        {
            Classes = { "muted" },
            Text = Loc.Instance["shell.color_invalid"],
            IsVisible = false
        };
        var apply = new Button
        {
            Content = Loc.Instance["shell.color_apply"],
            Classes = { "primary" },
            Height = 44,
            MinWidth = 110
        };
        apply.Click += (_, _) =>
        {
            if (!TryNormalizeHex(hexBox.Text ?? string.Empty, out var hex))
            {
                error.IsVisible = true;
                return;
            }
            flyout.Hide();
            _controller.SetTheme(_controller.Settings.ThemeMode, hex);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(hexBox);
        row.Children.Add(apply);

        return new StackPanel
        {
            Width = 280,
            Margin = new Thickness(4),
            Spacing = 12,
            Children = { title, grid, row, error }
        };
    }

    private static bool TryNormalizeHex(string text, out string hex)
    {
        hex = string.Empty;
        if (!Color.TryParse(text.Trim(), out var color))
            return false;
        hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        return true;
    }

    // ---------------- Ações ----------------

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        // O soltar de um arrasto não pode navegar.
        if (_navSuppressClick)
        {
            _navSuppressClick = false;
            return;
        }

        if (sender is Button { Tag: string key })
            ShowPage(key);
    }

    // ---------------- Menu reordenável ----------------

    /// <summary>Conexão, Dispositivos, Transferências e Pares salvos podem trocar de lugar entre si (Configurações e Sobre ficam fixos).</summary>
    private void AttachNavDrag()
    {
        foreach (var button in new[] { NavConnection, NavDevices, NavTransfers, NavPairs })
        {
            button.AddHandler(PointerPressedEvent, OnNavPointerPressed, RoutingStrategies.Tunnel);
            button.AddHandler(PointerMovedEvent, OnNavPointerMoved, RoutingStrategies.Tunnel);
            button.AddHandler(PointerReleasedEvent, OnNavPointerReleased, RoutingStrategies.Tunnel);
            button.PointerCaptureLost += OnNavCaptureLost;
        }
    }

    private void OnNavPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _navSuppressClick = false;
        if (sender is not Button button || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _navPressedButton = button;
        _navPressStart = e.GetPosition(NavTop);
        _navDragging = false;
    }

    private void OnNavPointerMoved(object? sender, PointerEventArgs e)
    {
        var button = _navPressedButton;
        if (button is null || !ReferenceEquals(sender, button))
            return;

        var point = e.GetPosition(NavTop);
        if (!_navDragging)
        {
            var dx = Math.Abs(point.X - _navPressStart.X);
            var dy = Math.Abs(point.Y - _navPressStart.Y);
            if (Math.Max(dx, dy) <= NavDragThreshold)
                return;

            _navDragging = true;
            button.Opacity = 0.7;
        }

        MoveNavItem(button, point.Y);
    }

    private void OnNavPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_navPressedButton is null || !ReferenceEquals(sender, _navPressedButton))
            return;

        FinishNavPress();
    }

    private void OnNavCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => FinishNavPress();

    /// <summary>Fim do toque. Se houve arrasto, grava a nova ordem e impede o clique que o soltar geraria.</summary>
    private void FinishNavPress()
    {
        var button = _navPressedButton;
        if (button is null)
            return;

        var dragged = _navDragging;
        _navPressedButton = null;
        _navDragging = false;
        button.Opacity = 1;

        if (dragged)
        {
            _navSuppressClick = true;
            _controller.SetNavOrder(CurrentNavOrder());
        }
    }

    /// <summary>
    /// Troca de lugar com o vizinho quando o ponteiro passa da metade dele. O vizinho é que muda de posição,
    /// e o item arrastado fica no lugar na árvore (assim não perde a captura do ponteiro).
    /// </summary>
    private void MoveNavItem(Button button, double y)
    {
        var index = NavTop.Children.IndexOf(button);
        if (index < 0)
            return;

        if (NavItemAt(index + 1) is { } below && y > below.Bounds.Top + below.Bounds.Height / 2)
        {
            NavTop.Children.RemoveAt(index + 1);
            NavTop.Children.Insert(index, below);
        }
        else if (NavItemAt(index - 1) is { } above && y < above.Bounds.Top + above.Bounds.Height / 2)
        {
            NavTop.Children.RemoveAt(index - 1);
            NavTop.Children.Insert(index, above);
        }
    }

    /// <summary>Item de topo do menu na posição dada (null se a posição não for um item reordenável).</summary>
    private Button? NavItemAt(int index)
    {
        if (index < 0 || index >= NavTop.Children.Count)
            return null;

        return NavTop.Children[index] is Button { Tag: string id } button && AppController.DefaultNavOrder.Contains(id)
            ? button
            : null;
    }

    /// <summary>Ids dos itens de topo, na ordem em que estão na tela.</summary>
    private List<string> CurrentNavOrder()
        => NavTop.Children.OfType<Button>()
            .Select(b => b.Tag as string)
            .OfType<string>()
            .Where(id => AppController.DefaultNavOrder.Contains(id))
            .ToList();

    /// <summary>Põe os itens de topo na ordem dada. Os demais itens do menu não mudam de lugar.</summary>
    private void ApplyNavOrder(IReadOnlyList<string> order)
    {
        for (var i = 0; i < order.Count; i++)
        {
            var button = _navButtons[order[i]];
            var current = NavTop.Children.IndexOf(button);
            if (current < 0 || current == i)
                continue;

            NavTop.Children.RemoveAt(current);
            NavTop.Children.Insert(i, button);
        }
    }

    private void OnSettingsToggleClick(object? sender, RoutedEventArgs e)
        => SetSettingsExpanded(!SettingsItems.IsVisible);

    private void SetSettingsExpanded(bool expanded)
    {
        SettingsItems.IsVisible = expanded;
        SettingsChevron.Data = this.FindResource(expanded ? "IconChevronUp" : "IconChevronDown") as Geometry;
    }

    /// <summary>Mostra uma página do menu (cria na primeira vez e reaproveita depois).</summary>
    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            _pages[key] = page;
        }

        ContentHost.Content = page;
        foreach (var (name, button) in _navButtons)
            button.Classes.Set("selected", name == key);

        if (SettingsKeys.Contains(key))
            SetSettingsExpanded(true);
    }

    private static UserControl CreatePage(string key) => key switch
    {
        "devices" => new DevicesView(),
        "transfers" => new TransfersView(),
        "pairs" => new SavedPairsView(),
        "general" => new SettingsGeneralView(),
        "appearance" => new SettingsAppearanceView(),
        "language" => new SettingsLanguageView(),
        "startup" => new SettingsStartupView(),
        _ => new AboutView()
    };

    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Button { Tag: string mode })
            return;
        _controller.SetTheme(mode, _controller.Settings.AccentColor);
    }

    private void OnSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex })
            return;
        _controller.SetTheme(_controller.Settings.ThemeMode, hex);
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string code })
            return;
        if (code != _controller.Settings.Language)
            _controller.SetLanguage(code);
    }

    // ---------------- Barra de título e janela ----------------

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (IsFromButton(e.Source))
            return;
        BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsFromButton(e.Source))
            return;
        ToggleMaximize();
    }

    private static bool IsFromButton(object? source)
        => source is Visual visual && visual.GetSelfAndVisualAncestors().OfType<Button>().Any();

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void RefreshMaximizeIcon()
    {
        MaximizeIcon.Data = this.FindResource(
            WindowState == WindowState.Maximized ? "IconWinRestore" : "IconWinMaximize") as Geometry;
    }

    /// <summary>Fechar a janela só esconde: o app continua na bandeja (exceto quando AllowClose foi chamado).</summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        Hide();
    }
}
