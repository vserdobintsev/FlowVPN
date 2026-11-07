using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    sealed record ProfileIdentity(string Name, string Icon, int IconColor);

    async Task NewProfile()
    {
        if (dialogOpen || windowClosed) return;
        string name = "Новый профиль";
        var existingNames = settings.Profiles.Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (int suffix = 2; existingNames.Contains(name); suffix++) name = $"Новый профиль ({suffix})";
        var profile = new VpnProfile { Name = name, SourceId = PreferredProfileSource()?.Id ?? "" };
        if (await ShowProfileIdentity(profile, creating: true) is not { } identity) return;

        profile.Name = identity.Name;
        profile.Icon = identity.Icon;
        profile.IconColor = identity.IconColor;
        string previousSelection = settings.SelectedProfileId;
        bool previousOnboarding = settings.ProfileOnboardingCompleted;
        settings.Profiles.Add(profile);
        settings.SelectedProfileId = profile.Id;
        settings.ProfileOnboardingCompleted = true;
        try { Store.Save(settings); }
        catch
        {
            settings.Profiles.Remove(profile);
            settings.SelectedProfileId = previousSelection;
            settings.ProfileOnboardingCompleted = previousOnboarding;
            throw;
        }
        BindAll();
        SelectPage(4);
    }

    Task EditProfileIcon() => Profile == null ? Task.CompletedTask : EditProfileIdentity(Profile);

    async Task EditProfileIdentity(VpnProfile profile)
    {
        if (await ShowProfileIdentity(profile, creating: false) is not { } identity || !settings.Profiles.Contains(profile)) return;
        var previous = new ProfileIdentity(profile.Name, profile.Icon, profile.IconColor);
        profile.Name = identity.Name;
        profile.Icon = identity.Icon;
        profile.IconColor = identity.IconColor;
        try { Store.Save(settings); }
        catch
        {
            profile.Name = previous.Name;
            profile.Icon = previous.Icon;
            profile.IconColor = previous.IconColor;
            throw;
        }
        BindAll();
        SelectPage(page);
    }

    async Task<ProfileIdentity?> ShowProfileIdentity(VpnProfile profile, bool creating)
    {
        if (dialogOpen || windowClosed)
            return null;
        var example = new VpnProfile { Name = profile.Name, Apps = profile.Apps, SourceId = profile.SourceId, Icon = profile.Icon, IconColor = profile.IconColor };
        var preview = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}" BorderThickness="1"
                CornerRadius="10" Padding="14,10" />
            """);
        var colorLabel = new TextBlock { FontSize = 13 };
        void RefreshExample()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(ProfileGlyph(example, track: false, adaptToTheme: true));
            row.Children.Add(new TextBlock { Text = example.Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 345, TextTrimming = TextTrimming.CharacterEllipsis });
            preview.Child = row;
            colorLabel.Text = "Цвет · " + (example.IconColor < 0 ? "Автоматически" : Palette[ProfileColor(example)].Name);
        }
        var search = new TextBox { PlaceholderText = $"Найти значок · {Icons.Length} вариантов" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Поиск значка профиля");
        var icons = new GridView { SelectionMode = ListViewSelectionMode.Single, Height = 210, HorizontalContentAlignment = HorizontalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(icons, "Значок профиля");
        bool filling = false;
        void FillIcons()
        {
            filling = true;
            icons.Items.Clear();
            foreach (var spec in Icons.Where(s => s.Name.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)))
            {
                var panel = new StackPanel { Width = 86, Spacing = 7, Padding = new Thickness(4, 10, 4, 8), HorizontalAlignment = HorizontalAlignment.Center };
                panel.Children.Add(new FontIcon { Glyph = spec.Glyph, FontSize = 22 });
                panel.Children.Add(new TextBlock { Text = spec.Name, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
                var item = new GridViewItem { Content = panel, Tag = spec.Key, MinWidth = 0, Padding = new Thickness(2) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, spec.Name);
                icons.Items.Add(item);
                if (example.Icon == spec.Key)
                    icons.SelectedItem = item;
            }
            filling = false;
        }
        icons.SelectionChanged += (_, _) => { if (!filling && icons.SelectedItem is GridViewItem item) { example.Icon = (string)item.Tag; RefreshExample(); } };
        search.TextChanged += (_, _) => FillIcons();
        FillIcons();
        var colors = new GridView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 140 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(colors, "Цвет профиля");
        for (int i = -1; i < Palette.Length; i++)
        {
            UIElement swatch = i < 0 ? new FontIcon { Glyph = "\uE713", FontSize = 17 } : new Ellipse { Width = 22, Height = 22, Fill = Color(Palette[i].R, Palette[i].G, Palette[i].B), Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 70, 80, 90)), StrokeThickness = 1 };
            var item = new GridViewItem { Content = swatch, Tag = i, Width = 40, Height = 36, MinWidth = 0, MinHeight = 0, Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            string name = i < 0 ? "Автоматически" : Palette[i].Name;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, name);
            ToolTipService.SetToolTip(item, name);
            colors.Items.Add(item);
            if (i == example.IconColor)
                colors.SelectedItem = item;
        }
        if (colors.SelectedIndex < 0)
            colors.SelectedIndex = 0;
        colors.SelectionChanged += (_, _) => { if (colors.SelectedItem is GridViewItem item) { example.IconColor = (int)item.Tag; RefreshExample(); } };
        var nameInput = new TextBox { Header = "Название профиля", Text = profile.Name, MaxLength = 80 };
        nameInput.TextChanged += (_, _) => { example.Name = nameInput.Text.Trim(); RefreshExample(); };
        RefreshExample();
        var content = new StackPanel { Width = 440, Spacing = 12 };
        content.Children.Add(nameInput);
        content.Children.Add(new TextBlock { Text = "Предпросмотр", FontSize = 12, Opacity = .7 });
        content.Children.Add(preview);
        content.Children.Add(search);
        content.Children.Add(icons);
        content.Children.Add(colorLabel);
        content.Children.Add(colors);
        var scroll = new ScrollViewer { Content = content, MaxHeight = Math.Clamp(root.ActualHeight - 220, 220, 600), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = creating ? "Новый профиль" : "Название и оформление", Content = scroll, PrimaryButtonText = creating ? "Создать профиль" : "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = profile.Name.Trim().Length > 0 };
        nameInput.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = nameInput.Text.Trim().Length > 0;
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = nameInput.Text.Trim().Length == 0;
        ReviewDialog(dialog, () => CheckProfileIdentityDialog(profile, creating, dialog, nameInput, icons, colors));
        dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !windowClosed)
                return new(nameInput.Text.Trim(), example.Icon, example.IconColor);
            return null;
        }
        finally { dialogOpen = false; }
    }
}
