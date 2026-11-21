// Dono: g11. Assinaturas públicas FIXAS (escritas pelo main).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace ConnectDesktop.App.Services;

/// <summary>
/// Modo claro/escuro e cor de destaque, aplicados ao vivo em Application.Current.
/// - Modo: "System" | "Light" | "Dark" → Application.RequestedThemeVariant (Default/Light/Dark).
/// - Destaque: define os recursos do Fluent "SystemAccentColor", "SystemAccentColorLight1..3", "SystemAccentColorDark1..3"
///   (variações clareando/escurecendo a cor) e os recursos próprios do app:
///   "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentSoftBrush" (destaque com ~15% de opacidade),
///   "AccentGlowBrush" (destaque com ~40% de opacidade, para brilhos do cabeçalho).
/// - Paleta do app (recursos dependentes do tema, definidos aqui em ThemeDictionaries Light/Dark de Application.Resources):
///   "AppBackgroundBrush", "SidebarBrush", "CardBrush", "CardBorderBrush", "TextPrimaryBrush", "TextSecondaryBrush",
///   "InputBrush", "DividerBrush". Escuro no estilo da imagem de referência (azul-marinho #0B1220 / cartões #111A2E);
///   claro equivalente (#F4F6FB / cartões #FFFFFF).
/// Use estes nomes como DynamicResource no XAML.
/// </summary>
public static class ThemeService
{
    /// <summary>Azul, roxo, verde, laranja, vermelho (o 6º botão da UI é o seletor livre).</summary>
    public static IReadOnlyList<string> PresetAccents { get; } = new[] { "#2563EB", "#7C3AED", "#16A34A", "#EA580C", "#DC2626" };

    private const string DefaultAccent = "#2563EB";

    /// <summary>Registra os ThemeDictionaries da paleta. Chamar uma vez em App.Initialize, depois de AvaloniaXamlLoader.Load.</summary>
    public static void Initialize()
    {
        var app = Application.Current;
        if (app is null)
            return;

        app.Resources.ThemeDictionaries[ThemeVariant.Light] = BuildPalette(
            appBackground: "#F4F6FB",
            sidebar: "#FFFFFF",
            card: "#FFFFFF",
            cardBorder: "#E2E8F0",
            textPrimary: "#0F172A",
            textSecondary: "#64748B",
            input: "#F8FAFC",
            divider: "#E2E8F0");

        app.Resources.ThemeDictionaries[ThemeVariant.Dark] = BuildPalette(
            appBackground: "#0B1220",
            sidebar: "#0E1626",
            card: "#111A2E",
            cardBorder: "#1E2B45",
            textPrimary: "#E8EEF8",
            textSecondary: "#94A3B8",
            input: "#0B1424",
            divider: "#1E2B45");

        // Destaque padrão já disponível após a inicialização; o modo não é alterado aqui.
        ApplyAccent(app, DefaultAccent);
    }

    /// <summary>Aplica modo e destaque. Hex inválido → usa o azul padrão. Pode ser chamado a qualquer momento (UI thread).</summary>
    public static void Apply(string mode, string accentHex)
    {
        var app = Application.Current;
        if (app is null)
            return;

        app.RequestedThemeVariant = ParseMode(mode);
        ApplyAccent(app, accentHex);
    }

    private static ThemeVariant ParseMode(string mode)
    {
        if (string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase))
            return ThemeVariant.Light;
        if (string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase))
            return ThemeVariant.Dark;
        return ThemeVariant.Default;
    }

    private static ResourceDictionary BuildPalette(
        string appBackground,
        string sidebar,
        string card,
        string cardBorder,
        string textPrimary,
        string textSecondary,
        string input,
        string divider)
    {
        return new ResourceDictionary
        {
            ["AppBackgroundBrush"] = new SolidColorBrush(Color.Parse(appBackground)),
            ["SidebarBrush"] = new SolidColorBrush(Color.Parse(sidebar)),
            ["CardBrush"] = new SolidColorBrush(Color.Parse(card)),
            ["CardBorderBrush"] = new SolidColorBrush(Color.Parse(cardBorder)),
            ["TextPrimaryBrush"] = new SolidColorBrush(Color.Parse(textPrimary)),
            ["TextSecondaryBrush"] = new SolidColorBrush(Color.Parse(textSecondary)),
            ["InputBrush"] = new SolidColorBrush(Color.Parse(input)),
            ["DividerBrush"] = new SolidColorBrush(Color.Parse(divider)),
        };
    }

    private static void ApplyAccent(Application app, string accentHex)
    {
        if (!Color.TryParse(accentHex, out var accent))
            accent = Color.Parse(DefaultAccent);

        // Fluent: cor base e variações clareando (Light) e escurecendo (Dark).
        var resources = app.Resources;
        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorLight1"] = Mix(accent, white: true, 0.15);
        resources["SystemAccentColorLight2"] = Mix(accent, white: true, 0.30);
        resources["SystemAccentColorLight3"] = Mix(accent, white: true, 0.45);
        resources["SystemAccentColorDark1"] = Mix(accent, white: false, 0.15);
        resources["SystemAccentColorDark2"] = Mix(accent, white: false, 0.30);
        resources["SystemAccentColorDark3"] = Mix(accent, white: false, 0.45);

        // Recursos próprios do app.
        resources["AccentBrush"] = new SolidColorBrush(accent);
        resources["AccentHoverBrush"] = new SolidColorBrush(Mix(accent, white: true, 0.15));
        resources["AccentPressedBrush"] = new SolidColorBrush(Mix(accent, white: false, 0.15));
        resources["AccentSoftBrush"] = new SolidColorBrush(accent, 0.15);
        resources["AccentGlowBrush"] = new SolidColorBrush(accent, 0.40);
    }

    /// <summary>Mistura com branco (white = true) ou preto, na proporção informada (0..1).</summary>
    private static Color Mix(Color color, bool white, double amount)
    {
        var target = white ? 255.0 : 0.0;
        byte Channel(byte value) => (byte)Math.Round(value + (target - value) * amount);

        return new Color(255, Channel(color.R), Channel(color.G), Channel(color.B));
    }
}
