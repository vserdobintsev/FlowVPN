using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace FlowVPN;

internal static class ComponentPresentation
{
    public static FrameworkElement Create(AppRule rule, bool existing = false)
    {
        var grid = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(30)
        });
        grid.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        var (symbol, color) = rule.ComponentRole switch
        {
            "engine" => (Symbol.Repair, Windows.UI.Color.FromArgb(255, 126, 88, 195)),
            "extension" => (Symbol.Link, Windows.UI.Color.FromArgb(255, 24, 128, 155)),
            "shared-service" => (Symbol.Setting, Windows.UI.Color.FromArgb(255, 164, 114, 32)),
            _ => (Symbol.AllApps, Windows.UI.Color.FromArgb(255, 29, 133, 118))
        };
        var shade = color;
        shade.A = 24;
        grid.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(shade),
            Child = new SymbolIcon(symbol) { Foreground = new SolidColorBrush(color) }
        });
        var text = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        text.Children.Add(new TextBlock { Text = ComponentRoles.Label(rule.ComponentRole) + (existing ? " · уже в профиле" : ""), FontSize = 11, Opacity = .7 });
        text.Children.Add(new TextBlock { Text = rule.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (rule.ScopeNote.Length > 0)
        {
            var note = new StackPanel { Spacing = 3 };
            note.Children.Add(new TextBlock { Text = "Область действия шире одного компонента", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            note.Children.Add(new TextBlock { Text = rule.ScopeNote, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new Border
            {
                Padding = new Thickness(9, 7, 9, 7),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 190, 132, 36)),
                Child = note
            });
        }
        text.Children.Add(new TextBlock { Text = rule.Path, FontSize = 11, Opacity = .6, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }
}
