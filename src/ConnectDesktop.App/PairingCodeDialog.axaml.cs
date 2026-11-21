using Avalonia.Controls;
using Avalonia.Interactivity;
using ConnectDesktop.App.Services;

namespace ConnectDesktop.App;

/// <summary>Janela modal para digitar o código de 6 dígitos mostrado no PC secundário.</summary>
public partial class PairingCodeDialog : Window
{
    private const int CodeLength = 6;

    public PairingCodeDialog()
    {
        InitializeComponent();
    }

    /// <summary>Código digitado (somente dígitos), ou null se cancelado.</summary>
    public string? Code { get; private set; }

    /// <summary>Mostra o diálogo modal sobre <paramref name="owner"/> e devolve o código, ou null se cancelado.</summary>
    public static async Task<string?> AskAsync(Window? owner)
    {
        if (owner is null)
            return null;

        var dialog = new PairingCodeDialog();
        await dialog.ShowDialog(owner);
        return dialog.Code;
    }

    private void OnCodeChanged(object? sender, TextChangedEventArgs e)
    {
        var text = CodeBox.Text ?? string.Empty;
        var digits = new string(text.Where(c => char.IsDigit(c)).Take(CodeLength).ToArray());
        var formatted = digits.Length > 3 ? $"{digits[..3]} {digits[3..]}" : digits;
        if (formatted != text)
        {
            CodeBox.Text = formatted;
            CodeBox.CaretIndex = formatted.Length;
        }
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        var text = CodeBox.Text ?? string.Empty;
        var digits = new string(text.Where(c => char.IsDigit(c)).ToArray());
        if (digits.Length != CodeLength)
        {
            ErrorText.Text = Loc.Instance["dialog.pair_digits"];
            ErrorText.IsVisible = true;
            return;
        }

        Code = digits;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Code = null;
        Close();
    }
}
