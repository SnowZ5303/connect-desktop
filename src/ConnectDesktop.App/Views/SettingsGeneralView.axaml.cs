// Dono: g13.
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ConnectDesktop.App.Services;

namespace ConnectDesktop.App.Views;

public partial class SettingsGeneralView : UserControl
{
    private bool _loading;

    public SettingsGeneralView()
    {
        InitializeComponent();

        SaveNameButton.Click += (_, _) => SaveName();
        ShareClipboardCheck.IsCheckedChanged += (_, _) => OnOptionChanged();
        UpdatesOnStartCheck.IsCheckedChanged += (_, _) => OnOptionChanged();
        CheckUpdatesButton.Click += async (_, _) => await CheckUpdatesAsync();
        ChangeDirButton.Click += async (_, _) => await ChangeDirAsync();
        ResetDirButton.Click += (_, _) => App.Controller?.SetDownloadDir("");

        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        if (App.Controller is not null)
            App.Controller.StateChanged += SyncFromSettings;
        LoadAll();
    }

    private void Unsubscribe()
    {
        if (App.Controller is not null)
            App.Controller.StateChanged -= SyncFromSettings;
    }

    /// <summary>Preenche todos os campos a partir das configurações atuais.</summary>
    private void LoadAll()
    {
        var controller = App.Controller;
        if (controller is null)
            return;

        _loading = true;
        try
        {
            NameBox.Text = controller.Settings.DeviceName;
            ShareClipboardCheck.IsChecked = controller.Settings.ShareClipboard;
            UpdatesOnStartCheck.IsChecked = controller.Settings.CheckUpdatesOnStart;
            CheckUpdatesButton.IsEnabled = controller.UpdatesEnabled;
            ShowDownloadDir();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Atualiza checkboxes e pasta quando o estado muda (sem sobrescrever o nome que está sendo digitado).</summary>
    private void SyncFromSettings()
    {
        var controller = App.Controller;
        if (controller is null)
            return;

        _loading = true;
        try
        {
            ShareClipboardCheck.IsChecked = controller.Settings.ShareClipboard;
            UpdatesOnStartCheck.IsChecked = controller.Settings.CheckUpdatesOnStart;
            ShowDownloadDir();
        }
        finally
        {
            _loading = false;
        }
    }

    private void ShowDownloadDir()
    {
        var controller = App.Controller;
        DownloadDirText.Text = controller?.Files.DownloadDir ?? "";
    }

    private void SaveName()
    {
        var controller = App.Controller;
        if (controller is null)
            return;

        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            NameStatusText.Text = Loc.Instance["settings.general.nameEmpty"];
            return;
        }

        try
        {
            controller.SetDeviceName(name);
            NameStatusText.Text = Loc.Instance["settings.general.nameSaved"];
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao salvar o nome do PC.", ex);
        }
    }

    private void OnOptionChanged()
    {
        if (_loading)
            return;

        var controller = App.Controller;
        if (controller is null)
            return;

        controller.SetShareClipboard(ShareClipboardCheck.IsChecked == true);
        controller.SetCheckUpdatesOnStart(UpdatesOnStartCheck.IsChecked == true);
    }

    private async Task CheckUpdatesAsync()
    {
        var controller = App.Controller;
        if (controller is null)
            return;

        CheckUpdatesButton.IsEnabled = false;
        UpdateResultText.Text = Loc.Instance["settings.general.checking"];
        try
        {
            var release = await controller.CheckForUpdatesAsync();
            UpdateResultText.Text = release is null
                ? Loc.Instance.Format("settings.general.upToDate", controller.AppVersion)
                : Loc.Instance.Format("settings.general.updateAvailable", release.Version.ToString());
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao verificar atualizações.", ex);
            UpdateResultText.Text = Loc.Instance["settings.general.updateFailed"];
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = controller.UpdatesEnabled;
        }
    }

    private async Task ChangeDirAsync()
    {
        var controller = App.Controller;
        var top = TopLevel.GetTopLevel(this);
        if (controller is null || top is null)
            return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.Instance["settings.general.pickDirTitle"],
            AllowMultiple = false
        });

        var path = folders.FirstOrDefault()?.Path;
        if (path is null || !path.IsFile)
            return;

        controller.SetDownloadDir(path.LocalPath);
        ShowDownloadDir();
    }
}
