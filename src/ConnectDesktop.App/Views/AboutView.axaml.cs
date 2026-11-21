// Dono: g13.
using System.Diagnostics;
using Avalonia.Controls;
using ConnectDesktop.App.Services;

namespace ConnectDesktop.App.Views;

public partial class AboutView : UserControl
{
    private const string RepoUrl = "https://github.com/SnowZ5303/connect-desktop";

    public AboutView()
    {
        InitializeComponent();

        RepoButton.Click += (_, _) => OpenRepo();
        CheckUpdatesButton.Click += async (_, _) => await CheckUpdatesAsync();

        AttachedToVisualTree += (_, _) => Load();
    }

    /// <summary>Versão atual e estado do botão de atualização.</summary>
    private void Load()
    {
        var controller = App.Controller;
        if (controller is null)
            return;

        VersionText.Text = Loc.Instance.Format("about.version", controller.AppVersion);
        CheckUpdatesButton.IsEnabled = controller.UpdatesEnabled;
    }

    private void OpenRepo()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao abrir o repositório no navegador.", ex);
        }
    }

    private async Task CheckUpdatesAsync()
    {
        var controller = App.Controller;
        if (controller is null)
            return;

        CheckUpdatesButton.IsEnabled = false;
        UpdateResultText.Text = Loc.Instance["about.checking"];
        try
        {
            var release = await controller.CheckForUpdatesAsync();
            UpdateResultText.Text = release is null
                ? Loc.Instance.Format("about.upToDate", controller.AppVersion)
                : Loc.Instance.Format("about.updateAvailable", release.Version.ToString());
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao verificar atualizações.", ex);
            UpdateResultText.Text = Loc.Instance["about.updateFailed"];
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = controller.UpdatesEnabled;
        }
    }
}
