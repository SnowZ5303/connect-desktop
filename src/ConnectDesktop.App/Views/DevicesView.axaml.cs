// Dono: g13.
using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Layout;
using ConnectDesktop.App.Services;
using ConnectDesktop.Core.Net;

namespace ConnectDesktop.App.Views;

public partial class DevicesView : UserControl
{

    /// <summary>Geometria de ícone do dicionário do app (inclui os ResourceInclude mesclados).</summary>
    private static Avalonia.Media.Geometry? IconGeometry(string key)
        => Application.Current is { } app && app.TryGetResource(key, null, out var resource)
            ? resource as Avalonia.Media.Geometry
            : null;
    public DevicesView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        var controller = App.Controller;
        if (controller is not null)
        {
            controller.StateChanged += Rebuild;
            controller.PeersChanged += OnPeersChanged;
        }
        Loc.Instance.LanguageChanged += Rebuild;
        Rebuild();
    }

    private void Unsubscribe()
    {
        var controller = App.Controller;
        if (controller is not null)
        {
            controller.StateChanged -= Rebuild;
            controller.PeersChanged -= OnPeersChanged;
        }
        Loc.Instance.LanguageChanged -= Rebuild;
    }

    private void OnPeersChanged(IReadOnlyList<PeerInfo> _) => Rebuild();

    /// <summary>Reconstrói a lista de PCs e o cabeçalho "Este PC" com os dados atuais.</summary>
    private void Rebuild()
    {
        var controller = App.Controller;
        PeersList.Children.Clear();

        if (controller is null)
        {
            ThisPcText.Text = "";
            EmptyPanel.IsVisible = true;
            OnlyPrimaryHint.IsVisible = false;
            return;
        }

        var isPrimary = controller.Settings.Role == DeviceRole.Primary;
        ThisPcText.Text = Loc.Instance.Format("devices.thisPcInfo",
            controller.Settings.DeviceName, RoleText(controller.Settings.Role));

        var peers = controller.AllPeers;
        EmptyPanel.IsVisible = peers.Count == 0;
        OnlyPrimaryHint.IsVisible = !isPrimary;

        foreach (var peer in peers)
            PeersList.Children.Add(BuildRow(controller, peer, isPrimary));
    }

    private Border BuildRow(AppController controller, PeerInfo peer, bool isPrimary)
    {
        var connected = isPrimary
            ? controller.IsPrimaryConnected && controller.ConnectedPeerId == peer.Id
            : controller.IsSecondaryConnected && peer.Role == DeviceRole.Primary;

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        badges.Children.Add(Badge(RoleText(peer.Role)));
        if (IsPaired(controller, peer.Id))
            badges.Children.Add(Badge(Loc.Instance["devices.paired"]));
        if (connected)
            badges.Children.Add(Badge(Loc.Instance["devices.connected"]));

        var info = new StackPanel { Spacing = 4 };
        info.Children.Add(new TextBlock { Text = peer.Name, Classes = { "rowTitle" } });
        info.Children.Add(new TextBlock
        {
            Text = Loc.Instance.Format("devices.ip", peer.Address) + "   ·   "
                   + Loc.Instance.Format("devices.version", peer.Version),
            Classes = { "muted" }
        });
        info.Children.Add(badges);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var icon = new Path
        {
            Classes = { "icon", "accent" },
            Data = IconGeometry("IconDevices"),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(info, 1);
        grid.Children.Add(icon);
        grid.Children.Add(info);

        if (connected)
        {
            var disconnect = new Button { Content = Loc.Instance["devices.disconnect"], VerticalAlignment = VerticalAlignment.Center };
            disconnect.Click += async (_, _) => await RunAsync(() => controller.DisconnectAsync());
            Grid.SetColumn(disconnect, 2);
            grid.Children.Add(disconnect);
        }
        else if (isPrimary && peer.Role == DeviceRole.Secondary)
        {
            var connect = new Button
            {
                Content = Loc.Instance["devices.connect"],
                Classes = { "primary" },
                VerticalAlignment = VerticalAlignment.Center
            };
            connect.Click += async (_, _) => await RunAsync(() => controller.ConnectAsync(peer));
            Grid.SetColumn(connect, 2);
            grid.Children.Add(connect);
        }

        return new Border { Classes = { "row" }, Child = grid };
    }

    private static Border Badge(string text)
        => new()
        {
            Classes = { "badge" },
            Child = new TextBlock { Text = text, Classes = { "badge" } }
        };

    private static bool IsPaired(AppController controller, string deviceId)
    {
        try
        {
            return controller.Store?.GetKey(deviceId) is not null;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao ler os pareamentos salvos.", ex);
            return false;
        }
    }

    private static string RoleText(DeviceRole role)
        => role == DeviceRole.Primary
            ? Loc.Instance["devices.role.primary"]
            : Loc.Instance["devices.role.secondary"];

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Error("Falha na ação de conexão.", ex);
        }
    }
}
