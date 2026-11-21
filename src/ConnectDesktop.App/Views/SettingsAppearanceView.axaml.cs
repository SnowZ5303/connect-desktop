// Dono: g13.
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;
using ConnectDesktop.App.Services;

namespace ConnectDesktop.App.Views;

public partial class SettingsAppearanceView : UserControl
{
    private static readonly Regex HexPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    public SettingsAppearanceView()
    {
        InitializeComponent();

        ThemeSystemButton.Click += (_, _) => ChooseMode("System");
        ThemeLightButton.Click += (_, _) => ChooseMode("Light");
        ThemeDarkButton.Click += (_, _) => ChooseMode("Dark");

        foreach (var hex in ThemeService.PresetAccents)
        {
            var swatch = new Button
            {
                Classes = { "swatch" },
                Tag = hex,
                Background = new SolidColorBrush(Color.Parse(hex))
            };
            var presetHex = hex;
            swatch.Click += (_, _) => ChooseAccent(presetHex);
            PresetPanel.Children.Add(swatch);
        }

        HexBox.TextChanged += (_, _) => OnHexTyped();
        ApplyHexButton.Click += (_, _) => ApplyHex();
        ResetNavButton.Click += (_, _) => App.Controller?.ResetNavOrder();

        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        if (App.Controller is not null)
            App.Controller.StateChanged += Refresh;
        HexBox.Text = App.Controller?.Settings.AccentColor ?? "";
        Refresh();
    }

    private void Unsubscribe()
    {
        if (App.Controller is not null)
            App.Controller.StateChanged -= Refresh;
    }

    /// <summary>Marca o tema e a cor atuais nos cartões e nos botões de cor.</summary>
    private void Refresh()
    {
        var controller = App.Controller;
        if (controller is null)
            return;

        var mode = controller.Settings.ThemeMode;
        SetSelected(ThemeSystemButton, mode == "System");
        SetSelected(ThemeLightButton, mode == "Light");
        SetSelected(ThemeDarkButton, mode == "Dark");

        var accent = controller.Settings.AccentColor;
        foreach (var child in PresetPanel.Children.OfType<Button>())
            SetSelected(child, string.Equals(child.Tag as string, accent, StringComparison.OrdinalIgnoreCase));
    }

    private static void SetSelected(Button button, bool selected)
    {
        if (selected)
            button.Classes.Add("selected");
        else
            button.Classes.Remove("selected");
    }

    private void ChooseMode(string mode)
    {
        var controller = App.Controller;
        if (controller is null)
            return;
        controller.SetTheme(mode, controller.Settings.AccentColor);
        Refresh();
    }

    private void ChooseAccent(string hex)
    {
        var controller = App.Controller;
        if (controller is null)
            return;
        controller.SetTheme(controller.Settings.ThemeMode, hex);
        HexStatusText.Text = "";
        HexBox.Text = hex;
        Refresh();
    }

    private void OnHexTyped()
    {
        HexStatusText.Text = "";
        if (TryNormalizeHex(HexBox.Text, out var hex))
            CustomPreview.Background = new SolidColorBrush(Color.Parse(hex));
    }

    private void ApplyHex()
    {
        if (!TryNormalizeHex(HexBox.Text, out var hex))
        {
            HexStatusText.Text = Loc.Instance["settings.appearance.invalidHex"];
            return;
        }

        var controller = App.Controller;
        if (controller is null)
            return;

        controller.SetTheme(controller.Settings.ThemeMode, hex);
        HexBox.Text = hex;
        HexStatusText.Text = Loc.Instance["settings.appearance.applied"];
        Refresh();
    }

    /// <summary>Aceita "#RRGGBB" (o # é opcional na digitação) e devolve em maiúsculas.</summary>
    private static bool TryNormalizeHex(string? text, out string hex)
    {
        var candidate = (text ?? "").Trim();
        if (candidate.Length > 0 && candidate[0] != '#')
            candidate = "#" + candidate;

        if (HexPattern.IsMatch(candidate))
        {
            hex = candidate.ToUpperInvariant();
            return true;
        }

        hex = "";
        return false;
    }
}
