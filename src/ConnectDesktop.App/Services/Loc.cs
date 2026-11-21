// Dono: g11. Assinaturas públicas FIXAS (escritas pelo main).
using System.ComponentModel;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using ConnectDesktop.App.Localization;

namespace ConnectDesktop.App.Services;

/// <summary>
/// Textos da interface em pt-BR, en e es, com troca ao vivo.
/// Tabelas: Localization.PtShell + PtPages (pt-BR), EnShell + EnPages (en), EsShell + EsPages (es).
/// Busca: idioma atual → pt-BR → a própria chave.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    /// <summary>Códigos aceitos e nome de exibição (no próprio idioma): ("pt-BR","Português (BR)"), ("en","English"), ("es","Español").</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = new (string Code, string Name)[]
    {
        ("pt-BR", "Português (BR)"),
        ("en", "English"),
        ("es", "Español"),
    };

    private string _currentLanguage;

    private Loc()
    {
        _currentLanguage = Resolve(null);
    }

    /// <summary>"pt-BR", "en" ou "es".</summary>
    public string CurrentLanguage => _currentLanguage;

    /// <summary>Texto da chave no idioma atual.</summary>
    public string this[string key] => Lookup(key);

    /// <summary>string.Format(this[key], args) no idioma atual.</summary>
    public string Format(string key, params object?[] args) => string.Format(this[key], args);

    /// <summary>
    /// "" ou null = idioma do sistema (CultureInfo.CurrentUICulture: pt* → pt-BR, es* → es, senão en).
    /// Dispara PropertyChanged (para bindings de indexador) e LanguageChanged.
    /// </summary>
    public void SetLanguage(string? code)
    {
        _currentLanguage = Resolve(code);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

        LanguageChanged?.Invoke();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Para textos montados em código (status, mensagens): reaplique quando disparar.</summary>
    public event Action? LanguageChanged;

    private string Lookup(string key)
    {
        foreach (var table in TablesOf(_currentLanguage))
        {
            if (table.TryGetValue(key, out var value))
                return value;
        }

        foreach (var table in TablesOf("pt-BR"))
        {
            if (table.TryGetValue(key, out var value))
                return value;
        }

        return key;
    }

    private static Dictionary<string, string>[] TablesOf(string language) => language switch
    {
        "en" => new[] { EnShell.Values, EnPages.Values },
        "es" => new[] { EsShell.Values, EsPages.Values },
        _ => new[] { PtShell.Values, PtPages.Values },
    };

    private static string Resolve(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            var requested = code.Trim().ToLowerInvariant();
            if (requested.StartsWith("pt"))
                return "pt-BR";
            if (requested.StartsWith("en"))
                return "en";
            if (requested.StartsWith("es"))
                return "es";
        }

        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return system switch
        {
            "pt" => "pt-BR",
            "es" => "es",
            _ => "en",
        };
    }
}

/// <summary>
/// Uso no XAML: xmlns:s="using:ConnectDesktop.App.Services" e Text="{s:Tr conn.title}".
/// Deve atualizar o texto sozinho quando o idioma muda (sem reabrir a janela).
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) { Key = key; }
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
