// Dono: g13.
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ConnectDesktop.App.Services;
using ConnectDesktop.Core.Transfer;

namespace ConnectDesktop.App.Views;

public partial class TransfersView : UserControl
{

    /// <summary>Geometria de ícone do dicionário do app (inclui os ResourceInclude mesclados).</summary>
    private static Avalonia.Media.Geometry? IconGeometry(string key)
        => Application.Current is { } app && app.TryGetResource(key, null, out var resource)
            ? resource as Avalonia.Media.Geometry
            : null;
    public TransfersView()
    {
        InitializeComponent();

        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
        PickButton.Click += async (_, _) => await PickFilesAsync();
        OpenDownloadsButton.Click += (_, _) => OpenFolder(App.Controller?.Files.DownloadDir);
        ClearButton.Click += (_, _) =>
        {
            App.Controller?.Files.ClearFinished();
            Reconcile();
        };

        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        var controller = App.Controller;
        if (controller is not null)
        {
            controller.Files.TransferChanged += OnTransferChanged;
            controller.StateChanged += Refresh;
            controller.ConnectionChanged += Refresh;
        }
        Loc.Instance.LanguageChanged += Refresh;
        Refresh();
    }

    private void Unsubscribe()
    {
        var controller = App.Controller;
        if (controller is not null)
        {
            controller.Files.TransferChanged -= OnTransferChanged;
            controller.StateChanged -= Refresh;
            controller.ConnectionChanged -= Refresh;
        }
        Loc.Instance.LanguageChanged -= Refresh;
    }

    /// <summary>Uma linha visual por Id. Progresso e estado atualizam só a linha da transferência.</summary>
    private readonly Dictionary<Guid, TransferRow> _rows = new();

    private sealed class TransferRow
    {
        public Border Root { get; init; } = null!;
        public TextBlock Title { get; init; } = null!;
        public TextBlock Meta { get; init; } = null!;
        public ProgressBar Bar { get; init; } = null!;
        public TextBlock Progress { get; init; } = null!;
        public TextBlock Error { get; init; } = null!;
        public Button Cancel { get; init; } = null!;
        public Button OpenFolder { get; init; } = null!;
        public TransferInfo Info { get; set; } = null!;
    }

    private void OnTransferChanged(TransferInfo info) => Dispatcher.UIThread.Post(() => ApplyTransfer(info));

    /// <summary>Atualiza conexão, pasta de destino e sincroniza as linhas (sem reconstruir a lista).</summary>
    private void Refresh()
    {
        var controller = App.Controller;
        var connected = controller?.Files.IsConnected ?? false;

        PickButton.IsEnabled = connected;
        NotConnectedText.IsVisible = !connected;
        DestText.Text = controller?.Files.DownloadDir ?? "";
        Reconcile();
    }

    /// <summary>Atualiza só a linha desta transferência (cria a linha se ela acabou de entrar).</summary>
    private void ApplyTransfer(TransferInfo info)
    {
        Update(GetOrAddRow(info), info);
        UpdateListState();
    }

    /// <summary>Remove as linhas das transferências que saíram da lista (ex.: Limpar concluídas) e atualiza as demais.</summary>
    private void Reconcile()
    {
        var transfers = App.Controller?.Files.Transfers ?? Array.Empty<TransferInfo>();
        var ids = new HashSet<Guid>(transfers.Select(t => t.Id));

        foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            TransfersList.Children.Remove(_rows[id].Root);
            _rows.Remove(id);
        }

        foreach (var info in transfers)
            Update(GetOrAddRow(info), info);

        UpdateListState();
    }

    private TransferRow GetOrAddRow(TransferInfo info)
    {
        if (_rows.TryGetValue(info.Id, out var row))
            return row;

        row = CreateRow(info.Id, info.Direction);
        _rows[info.Id] = row;
        TransfersList.Children.Insert(0, row.Root);
        return row;
    }

    private void UpdateListState()
    {
        EmptyText.IsVisible = _rows.Count == 0;
        ClearButton.IsVisible = _rows.Values.Any(r => !IsActive(r.Info));
    }

    /// <summary>Monta a linha uma vez. Os botões leem o Id/estado no clique, então a linha pode ser atualizada sem perder o clique.</summary>
    private TransferRow CreateRow(Guid id, TransferDirection direction)
    {
        var received = direction == TransferDirection.Incoming;
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Classes = { "icon", "accent" },
            Data = IconGeometry(received ? "IconDownload" : "IconArrowRight"),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var title = new TextBlock { Classes = { "rowTitle" }, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        var meta = new TextBlock { Classes = { "muted" } };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6 };
        var progress = new TextBlock { Classes = { "muted" } };
        var error = new TextBlock { Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        var cancel = new Button();
        cancel.Click += (_, _) => App.Controller?.Files.Cancel(id);

        var openFolder = new Button();
        openFolder.Click += (_, _) =>
        {
            if (_rows.TryGetValue(id, out var current) && !string.IsNullOrEmpty(current.Info.LocalPath))
                OpenFolderOf(current.Info.LocalPath);
        };

        var details = new StackPanel { Spacing = 4 };
        details.Children.Add(title);
        details.Children.Add(meta);
        details.Children.Add(bar);
        details.Children.Add(progress);
        details.Children.Add(error);

        var actions = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(cancel);
        actions.Children.Add(openFolder);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(details, 1);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(icon);
        grid.Children.Add(details);
        grid.Children.Add(actions);

        return new TransferRow
        {
            Root = new Border { Classes = { "row" }, Child = grid },
            Title = title,
            Meta = meta,
            Bar = bar,
            Progress = progress,
            Error = error,
            Cancel = cancel,
            OpenFolder = openFolder
        };
    }

    /// <summary>Escreve os campos variáveis de uma linha (textos, barra, botões). Não cria nem remove controles.</summary>
    private static void Update(TransferRow row, TransferInfo info)
    {
        row.Info = info;
        var active = IsActive(info);
        var received = info.Direction == TransferDirection.Incoming;
        var direction = received ? Loc.Instance["transfers.received"] : Loc.Instance["transfers.sent"];

        row.Title.Text = info.FileName;
        row.Meta.Text = $"{direction}   ·   {FormatBytes(info.Size)}   ·   {StateText(info.State)}";

        row.Bar.IsVisible = active;
        row.Progress.IsVisible = active;
        if (active)
        {
            row.Bar.Value = info.Size > 0 ? info.Transferred * 100.0 / info.Size : 0;
            row.Progress.Text = ProgressText(info);
        }

        var errorText = ErrorMessage(info);
        row.Error.IsVisible = errorText is not null;
        row.Error.Text = errorText ?? "";

        row.Cancel.Content = Loc.Instance["transfers.cancel"];
        row.Cancel.IsVisible = active;

        row.OpenFolder.Content = Loc.Instance["transfers.openFolder"];
        row.OpenFolder.IsVisible = info.State == TransferState.Completed && received && !string.IsNullOrEmpty(info.LocalPath);
    }

    private static string? ErrorMessage(TransferInfo info)
    {
        if (info.State != TransferState.Failed && info.State != TransferState.Canceled)
            return null;
        if (!string.IsNullOrEmpty(info.Error))
            return ErrorText(info.Error);
        return info.State == TransferState.Failed ? Loc.Instance["transfers.error.generic"] : null;
    }

    private static bool IsActive(TransferInfo info)
        => info.State == TransferState.Pending || info.State == TransferState.InProgress;

    private static string StateText(TransferState state) => state switch
    {
        TransferState.Pending => Loc.Instance["transfers.state.pending"],
        TransferState.InProgress => Loc.Instance["transfers.state.inProgress"],
        TransferState.Completed => Loc.Instance["transfers.state.completed"],
        TransferState.Failed => Loc.Instance["transfers.state.failed"],
        _ => Loc.Instance["transfers.state.canceled"],
    };

    private static string ProgressText(TransferInfo info)
    {
        var text = Loc.Instance.Format("transfers.progress", FormatBytes(info.Transferred), FormatBytes(info.Size));
        if (info.BytesPerSecond > 0)
        {
            text += "   ·   " + Loc.Instance.Format("transfers.speed", FormatBytes((long)info.BytesPerSecond));
            var remaining = TimeSpan.FromSeconds((info.Size - info.Transferred) / info.BytesPerSecond);
            var remainingText = remaining.TotalHours >= 1
                ? remaining.ToString(@"h\:mm\:ss")
                : remaining.ToString(@"m\:ss");
            text += "   ·   " + Loc.Instance.Format("transfers.remaining", remainingText);
        }
        else
        {
            text += "   ·   " + Loc.Instance["transfers.calculating"];
        }
        return text;
    }

    /// <summary>Traduz os erros conhecidos do FileTransferService; os demais são mostrados como vieram.</summary>
    private static string ErrorText(string error)
    {
        const string writePrefix = "Erro ao gravar o arquivo:";
        const string readPrefix = "Erro ao ler o arquivo:";

        return error switch
        {
            "Conexão perdida." => Loc.Instance["transfers.error.connectionLost"],
            "Cancelado pelo usuário." => Loc.Instance["transfers.error.canceledByUser"],
            "Cancelado pelo outro PC." => Loc.Instance["transfers.error.canceledByPeer"],
            "Sem resposta do outro PC." => Loc.Instance["transfers.error.noResponse"],
            "Arquivo corrompido na transferência." => Loc.Instance["transfers.error.corrupted"],
            "Nenhum PC conectado." => Loc.Instance["transfers.error.noPeer"],
            "Arquivo não encontrado." => Loc.Instance["transfers.error.fileNotFound"],
            "Erro desconhecido." => Loc.Instance["transfers.error.unknown"],
            _ when error.StartsWith(writePrefix, StringComparison.Ordinal)
                => Loc.Instance.Format("transfers.error.writeFile", error[writePrefix.Length..].Trim()),
            _ when error.StartsWith(readPrefix, StringComparison.Ordinal)
                => Loc.Instance.Format("transfers.error.readFile", error[readPrefix.Length..].Trim()),
            _ => error,
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    // ---------------- Envio ----------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var connected = App.Controller?.Files.IsConnected ?? false;
        e.DragEffects = connected && e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var paths = e.Data.GetFiles()?
            .Select(item => item.Path)
            .Where(uri => uri.IsFile)
            .Select(uri => uri.LocalPath)
            .ToList() ?? new List<string>();
        SendPaths(paths);
    }

    private async Task PickFilesAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Instance["transfers.pickTitle"],
            AllowMultiple = true
        });

        var paths = files
            .Select(file => file.Path)
            .Where(uri => uri.IsFile)
            .Select(uri => uri.LocalPath)
            .ToList();
        SendPaths(paths);
    }

    /// <summary>Um SendFile por arquivo; erros aparecem na linha de status.</summary>
    private void SendPaths(IEnumerable<string> paths)
    {
        var controller = App.Controller;
        if (controller is null)
            return;

        foreach (var path in paths)
        {
            try
            {
                controller.Files.SendFile(path);
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao enviar arquivo.", ex);
                ShowStatus(Loc.Instance.Format("transfers.sendFailed", ErrorDetail(ex)));
            }
        }
        Refresh();
    }

    private static string ErrorDetail(Exception ex) => ex switch
    {
        InvalidOperationException => Loc.Instance["transfers.error.noPeer"],
        FileNotFoundException => Loc.Instance["transfers.error.fileNotFound"],
        _ => ex.Message,
    };

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = true;
    }

    // ---------------- Abrir pastas ----------------

    private void OpenFolderOf(string filePath)
    {
        var folder = Path.GetDirectoryName(filePath);
        if (!File.Exists(filePath) || folder is null)
        {
            ShowStatus(Loc.Instance["transfers.openFailed"]);
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", filePath }, UseShellExecute = false });
            else
                OpenFolder(folder);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao abrir a pasta do arquivo.", ex);
            ShowStatus(Loc.Instance["transfers.openFailed"]);
        }
    }

    private void OpenFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            ShowStatus(Loc.Instance["transfers.openFailed"]);
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { folder }, UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao abrir a pasta.", ex);
            ShowStatus(Loc.Instance["transfers.openFailed"]);
        }
    }
}
