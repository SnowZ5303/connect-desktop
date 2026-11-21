// Dono: g15. Faixa de "soltar" usada pelo EdgeDropController (Services/EdgeDropController.cs).
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace ConnectDesktop.App.Views;

/// <summary>
/// Faixa de 14 px nas bordas da tela. Aceita arquivos/pastas, links e texto soltos.
/// Não ativa nem toma foco (ShowActivated=false). Os eventos são levantados na UI thread.
/// </summary>
public partial class EdgeDropWindow : Window
{
    private const double StripThickness = 14;
    private static readonly string[] UrlFormats = { "text/uri-list", "UniformResourceLocatorW" };

    internal event Action? DragEntered;
    internal event Action? DragLeft;
    internal event Action<IReadOnlyList<string>>? PathsDropped;
    internal event Action<string>? UrlDropped;
    internal event Action<string>? TextDropped;
    internal event Action? DropFinished;

    public EdgeDropWindow()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>
    /// Faixa vertical (Left/Right) gira o rótulo; horizontal (Top/Bottom) fica reta.
    /// Limita a espessura do conteúdo a 14 px: sem isso o Window cresce até o tamanho desejado do rótulo.
    /// </summary>
    internal void SetVertical(bool vertical)
    {
        LabelPanel.RenderTransform = vertical ? new RotateTransform(90) : null;
        Strip.MaxWidth = vertical ? StripThickness : double.PositiveInfinity;
        Strip.MaxHeight = vertical ? double.PositiveInfinity : StripThickness;
    }

    /// <summary>
    /// Põe a faixa exatamente na borda (x, y, espessura e comprimento em pixels físicos) e recorta a janela a essa faixa.
    /// Dois limites do Avalonia/Windows obrigam este caminho: (1) Position não deixa a janela encostar na borda da tela
    /// (o Avalonia limita o X a ~30 px da borda), então usamos SetWindowPos no hwnd; (2) o Windows não deixa uma janela
    /// sem borda ficar menor que ~32x39 px, então a janela pode passar da faixa e a região a recorta.
    /// Para Right/Bottom a janela passa um pouco para fora da tela; a parte fora não aparece nem recebe mouse.
    /// A faixa fica no início da janela (esquerda em Left/Right, topo em Top/Bottom). Chamar depois de Show.
    /// </summary>
    internal void PlaceStrip(int x, int y, int thicknessPx, int lengthPx, bool vertical)
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return;

        if (!_toolStylesApplied)
        {
            ApplyToolWindowStyles(hwnd);
            _toolStylesApplied = true;
        }

        var cx = vertical ? thicknessPx : lengthPx;
        var cy = vertical ? lengthPx : thicknessPx;
        SetWindowPos(hwnd, HwndTopmost, x, y, cx, cy, SwpNoActivate);

        if (!GetWindowRect(hwnd, out var rect))
            return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var region = vertical
            ? CreateRectRgn(0, 0, thicknessPx, height)
            : CreateRectRgn(0, 0, width, thicknessPx);
        SetWindowRgn(hwnd, region, true);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        Highlight(true);
        DragEntered?.Invoke();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        Highlight(true);
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        Highlight(false);
        DragLeft?.Invoke();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            Deliver(e.Data);
        }
        catch (Exception ex)
        {
            Log.Error("EdgeDrop: falha ao ler o conteúdo solto.", ex);
        }
        finally
        {
            Highlight(false);
            DropFinished?.Invoke();
        }
    }

    /// <summary>Arquivos/pastas → PathsDropped; senão texto: link http/https/mailto de uma linha → UrlDropped, resto → TextDropped.</summary>
    private void Deliver(IDataObject data)
    {
        var items = data.GetFiles();
        if (items is not null)
        {
            var paths = items
                .Select(item => item.TryGetLocalPath())
                .OfType<string>()
                .Where(path => path.Length > 0)
                .ToList();

            if (paths.Count > 0)
            {
                PathsDropped?.Invoke(paths);
                return;
            }
        }

        var text = ReadText(data);
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (TryGetWebLink(text, out var link))
            UrlDropped?.Invoke(link);
        else
            TextDropped?.Invoke(text);
    }

    private static string? ReadText(IDataObject data)
    {
        var text = data.GetText();
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        foreach (var format in UrlFormats)
        {
            if (!data.Contains(format))
                continue;

            var value = data.Get(format);
            var read = value switch
            {
                string s => s,
                byte[] bytes when format == "UniformResourceLocatorW" => Encoding.Unicode.GetString(bytes).TrimEnd('\0'),
                byte[] bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(read))
                return read;
        }

        return null;
    }

    private static bool TryGetWebLink(string text, out string link)
    {
        link = text.Trim();
        if (link.Contains('\n') || link.Contains('\r'))
            return false;

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme is "http" or "https" or "mailto";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WinRect rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);

    /// <summary>Não ativa e não aparece na barra de tarefas/Alt+Tab (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW).</summary>
    private static void ApplyToolWindowStyles(IntPtr hwnd)
    {
        const int GwlExStyle = -20;
        const long WsExNoActivate = 0x08000000;
        const long WsExToolWindow = 0x00000080;

        var current = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(current | WsExNoActivate | WsExToolWindow));
    }

    private bool _toolStylesApplied;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;

    private void Highlight(bool on)
    {
        if (on)
        {
            if (!Strip.Classes.Contains("hot"))
                Strip.Classes.Add("hot");
        }
        else
        {
            Strip.Classes.Remove("hot");
        }
    }
}
