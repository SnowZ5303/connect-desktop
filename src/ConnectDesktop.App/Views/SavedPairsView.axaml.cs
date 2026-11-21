// Dono: g13.
using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Layout;
using ConnectDesktop.App.Services;

namespace ConnectDesktop.App.Views;

public partial class SavedPairsView : UserControl
{

    /// <summary>Geometria de ícone do dicionário do app (inclui os ResourceInclude mesclados).</summary>
    private static Avalonia.Media.Geometry? IconGeometry(string key)
        => Application.Current is { } app && app.TryGetResource(key, null, out var resource)
            ? resource as Avalonia.Media.Geometry
            : null;
    public SavedPairsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        if (App.Controller is not null)
            App.Controller.StateChanged += Refresh;
        Loc.Instance.LanguageChanged += Refresh;
        Refresh();
    }

    private void Unsubscribe()
    {
        if (App.Controller is not null)
            App.Controller.StateChanged -= Refresh;
        Loc.Instance.LanguageChanged -= Refresh;
    }

    /// <summary>Reconstrói a lista a partir de Store.List().</summary>
    private void Refresh()
    {
        PairsList.Children.Clear();

        var pairs = ReadPairs();
        EmptyPanel.IsVisible = pairs.Count == 0;
        ListPanel.IsVisible = pairs.Count > 0;

        foreach (var (deviceId, deviceName) in pairs)
            PairsList.Children.Add(BuildRow(deviceId, deviceName));
    }

    private static IReadOnlyList<(string DeviceId, string DeviceName)> ReadPairs()
    {
        try
        {
            return App.Controller?.Store?.List() ?? Array.Empty<(string DeviceId, string DeviceName)>();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao ler os pareamentos salvos.", ex);
            return Array.Empty<(string DeviceId, string DeviceName)>();
        }
    }

    private Border BuildRow(string deviceId, string deviceName)
    {
        var shortId = deviceId.Length > 8 ? deviceId[..8] : deviceId;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var icon = new Path
        {
            Classes = { "icon", "accent" },
            Data = IconGeometry("IconBookmark"),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var info = new StackPanel { Spacing = 4 };
        info.Children.Add(new TextBlock { Text = deviceName, Classes = { "rowTitle" } });
        info.Children.Add(new TextBlock { Text = Loc.Instance.Format("pairs.id", shortId), Classes = { "muted" } });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var forget = new Button { Content = Loc.Instance["pairs.forget"], VerticalAlignment = VerticalAlignment.Center };
        forget.Click += async (_, _) => await ForgetAsync(deviceId, deviceName);
        Grid.SetColumn(forget, 2);
        grid.Children.Add(forget);

        return new Border { Classes = { "row" }, Child = grid };
    }

    private async Task ForgetAsync(string deviceId, string deviceName)
    {
        var confirmed = await ConfirmAsync(
            Loc.Instance["pairs.forgetTitle"],
            Loc.Instance.Format("pairs.forgetText", deviceName));
        if (!confirmed)
            return;

        App.Controller?.ForgetPair(deviceId);
        Refresh();
    }

    /// <summary>Diálogo simples de confirmação, com os botões Esquecer e Cancelar.</summary>
    private async Task<bool> ConfirmAsync(string title, string text)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return false;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var cancel = new Button { Content = Loc.Instance["pairs.cancel"] };
        cancel.Click += (_, _) => dialog.Close(false);

        var confirm = new Button { Content = Loc.Instance["pairs.confirm"], Classes = { "primary" } };
        confirm.Click += (_, _) => dialog.Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, confirm }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                buttons
            }
        };

        var result = await dialog.ShowDialog<bool>(owner);
        return result;
    }
}
