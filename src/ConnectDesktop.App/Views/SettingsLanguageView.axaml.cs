// Dono: g13.
using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Layout;
using ConnectDesktop.App.Services;

namespace ConnectDesktop.App.Views;

public partial class SettingsLanguageView : UserControl
{

    /// <summary>Geometria de ícone do dicionário do app (inclui os ResourceInclude mesclados).</summary>
    private static Avalonia.Media.Geometry? IconGeometry(string key)
        => Application.Current is { } app && app.TryGetResource(key, null, out var resource)
            ? resource as Avalonia.Media.Geometry
            : null;
    public SettingsLanguageView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        if (App.Controller is not null)
            App.Controller.StateChanged += Rebuild;
        Loc.Instance.LanguageChanged += Rebuild;
        Rebuild();
    }

    private void Unsubscribe()
    {
        if (App.Controller is not null)
            App.Controller.StateChanged -= Rebuild;
        Loc.Instance.LanguageChanged -= Rebuild;
    }

    /// <summary>Lista "Usar idioma do sistema" seguida de Loc.Languages, marcando o idioma escolhido.</summary>
    private void Rebuild()
    {
        LanguageList.Children.Clear();

        var controller = App.Controller;
        if (controller is null)
            return;

        var current = controller.Settings.Language ?? "";

        var systemIcon = new Path { Classes = { "icon", "accent" }, Data = IconGeometry("IconGlobe"), VerticalAlignment = VerticalAlignment.Center };
        LanguageList.Children.Add(BuildItem(
            "",
            systemIcon,
            Loc.Instance["settings.language.system"],
            Loc.Instance["settings.language.systemHint"],
            current.Length == 0));

        foreach (var (code, name) in Loc.Languages)
        {
            var badge = new Border
            {
                Classes = { "badge" },
                Child = new TextBlock { Text = code.ToUpperInvariant(), Classes = { "badge" } }
            };
            LanguageList.Children.Add(BuildItem(code, badge, name, null, current == code));
        }
    }

    private Button BuildItem(string code, Control leading, string title, string? subtitle, bool selected)
    {
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, Classes = { "rowTitle" } });
        if (!string.IsNullOrEmpty(subtitle))
            texts.Children.Add(new TextBlock { Text = subtitle, Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(leading, 0);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(leading);
        grid.Children.Add(texts);

        var button = new Button { Classes = { "langItem" }, Content = grid };
        if (selected)
            button.Classes.Add("selected");

        button.Click += (_, _) => App.Controller?.SetLanguage(code);
        return button;
    }
}
