using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace FlowVPN;

internal sealed partial class FluentWindow
{
    List<DetectedPreset> detectedPresets = AppPresets.Discover();
    CancellationTokenSource? groupIcons;
    readonly HashSet<ApplicationGroup> selectedApplications = [];
    void BindApplications(VpnProfile? profile)
    {
        groupIcons?.Cancel();
        groupIcons?.Dispose();
        groupIcons = new();
        var list = C<ListView>("AppList");
        selectedApplications.Clear();
        list.Items.Clear();
        var groups = ApplicationGroups.Create(profile?.Apps ?? [], detectedPresets);
        foreach (var group in groups)
            list.Items.Add(ApplicationCard(group, groupIcons.Token));
        C<TextBlock>("RuleCount").Text = (groups.Any(g => detectedPresets.Any(p => p.Id == g.Id && p.Definition.ParentId.Length > 0)) ? "Приложений и расширений: " : "Приложений: ") + groups.Count;
        ToolTipService.SetToolTip(C<TextBlock>("RuleCount"), "Компоненты каждого приложения собраны в одну раскрываемую карточку.");
        list.Visibility = groups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        C<TextBlock>("EmptyApps").Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        C<Button>("RemoveApps").IsEnabled = false;
    }
    ListViewItem ApplicationCard(ApplicationGroup group, CancellationToken token)
    {
        var header = new Grid { ColumnSpacing = 12, MinHeight = 42, HorizontalAlignment = HorizontalAlignment.Stretch };
        header.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(30)
        });
        header.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(36)
        });
        header.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        var selection = new CheckBox { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(selection, "Выбрать приложение «" + group.Name + "»");
        selection.Checked += (_, _) => { selectedApplications.Add(group); C<Button>("RemoveApps").IsEnabled = true; };
        selection.Unchecked += (_, _) => { selectedApplications.Remove(group); C<Button>("RemoveApps").IsEnabled = selectedApplications.Count > 0; };
        header.Children.Add(selection);
        var placeholder = new FontIcon { Glyph = "\uE71D", FontSize = 22, Opacity = .55 };
        var image = new Image { Width = 32, Height = 32, Tag = placeholder };
        var icon = new Grid { Width = 36, Height = 36 };
        icon.Children.Add(placeholder);
        icon.Children.Add(image);
        Grid.SetColumn(icon, 1);
        header.Children.Add(icon);
        var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Text(group.Name, 14, true));
        labels.Children.Add(new TextBlock { Text = "Компонентов: " + group.Rules.Count, FontSize = 12, Opacity = .65 });
        Grid.SetColumn(labels, 2);
        header.Children.Add(labels);
        var detail = new StackPanel { Spacing = 14 };
        foreach (var rule in group.Rules)
        {
            var component = new Grid { ColumnSpacing = 10 };
            component.ColumnDefinitions.Add(new()
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            component.ColumnDefinitions.Add(new()
            {
                Width = GridLength.Auto
            });
            component.Children.Add(ComponentPresentation.Create(rule));
            var remove = new Button
            {
                Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 },
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            ToolTipService.SetToolTip(remove, "Убрать компонент из профиля");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Убрать компонент: " + rule.Name);
            remove.Click += (_, _) => { if (Profile != null) { Profile.Apps.Remove(rule); Save(); BindProfile(); } };
            Grid.SetColumn(remove, 1);
            component.Children.Add(remove);
            detail.Children.Add(component);
        }
        var expander = new Expander
        {
            Header = header,
            Content = detail,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        // Expander has a fixed header template width unless explicitly stretched to its list item.
        var item = new ListViewItem
        {
            Content = expander,
            Tag = group,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 3, 0, 3)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, group.Name + ", компонентов: " + group.Rules.Count);
        if (group.IconPath.Length > 0)
        {
            var app = new CatalogApp { Name = group.Name, Path = group.IconPath, IconPath = group.IconPath, Rules = group.Rules };
            _ = LoadCatalogIcons([new(app.Id, image)], [app], token);
        }
        return item;
    }
    void RemoveApplications()
    {
        if (Profile == null)
            return;
        var groups = selectedApplications.ToArray();
        foreach (var group in groups)
            foreach (var rule in group.Rules)
                Profile.Apps.Remove(rule);
        Save();
        BindProfile();
    }
}
