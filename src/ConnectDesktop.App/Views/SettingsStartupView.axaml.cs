// Dono: g13.
using Avalonia.Controls;

namespace ConnectDesktop.App.Views;

public partial class SettingsStartupView : UserControl
{
    private bool _loading;

    public SettingsStartupView()
    {
        InitializeComponent();

        StartWithSystemToggle.IsCheckedChanged += (_, _) => OnToggled();
        StartMinimizedToggle.IsCheckedChanged += (_, _) => OnToggled();

        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        if (App.Controller is not null)
            App.Controller.StateChanged += Load;
        Load();
    }

    private void Unsubscribe()
    {
        if (App.Controller is not null)
            App.Controller.StateChanged -= Load;
    }

    /// <summary>Lê as configurações atuais; o "minimizado" só fica ligado com "abrir com o sistema".</summary>
    private void Load()
    {
        var controller = App.Controller;
        if (controller is null)
            return;

        _loading = true;
        try
        {
            StartWithSystemToggle.IsChecked = controller.Settings.StartWithSystem;
            StartMinimizedToggle.IsChecked = controller.Settings.StartMinimized;
            StartMinimizedToggle.IsEnabled = controller.Settings.StartWithSystem;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnToggled()
    {
        if (_loading)
            return;

        var controller = App.Controller;
        if (controller is null)
            return;

        var withSystem = StartWithSystemToggle.IsChecked == true;
        var minimized = withSystem && StartMinimizedToggle.IsChecked == true;
        controller.SetStartup(withSystem, minimized);
        Load();
    }
}
