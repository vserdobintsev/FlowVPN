using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    static readonly (string Key, string Name, string Glyph)[] Icons ={
  ("auto","Автоматически","\uE713"),("code","Терминал","\uE756"),("browser","Браузер","\uE774"),
  ("chat","Сообщения","\uE8F2"),("network","Сеть / VPN","\uE705"),("rules","Правила","\uE8FD"),
  ("cloud","Облако","\uE753"),("game","Игры","\uE7FC"),("star","Избранное","\uE734"),
  ("work","Работа","\uE821"),("home","Дом","\uE80F"),("education","Учёба","\uE7BE"),
  ("books","Чтение","\uE7BC"),("developer","Разработка","\uE943"),("robot","ИИ / боты","\uE99A"),
  ("mail","Почта","\uE715"),("people","Команда","\uE716"),("video","Видеосвязь","\uE714"),
  ("movies","Кино","\uE8B2"),("music","Музыка","\uEC4F"),("headphones","Наушники","\uE7F6"),
  ("photo","Фотографии","\uE91B"),("camera","Камера","\uE722"),("microphone","Микрофон","\uE720"),
  ("download","Загрузки","\uE896"),("upload","Передача","\uE898"),("folder","Файлы","\uE8B7"),
  ("wifi","Wi-Fi","\uE701"),("ethernet","Ethernet","\uE839"),("lock","Приватность","\uE72E"),
  ("key","Доступ","\uE8D7"),("bolt","Молния","\uE945"),("location","Место","\uE707"),
  ("world","Весь мир","\uE909"),("heart","Личное","\uEB51"),("clock","Время","\uE917")};
    static readonly (string Name, byte R, byte G, byte B)[] Palette ={
  ("Бирюзовый",78,210,183),("Голубой",108,179,255),("Лавандовый",189,159,255),
  ("Янтарный",247,189,94),("Розовый",245,143,181),("Серебристый",184,199,215),
  ("Мятный",139,231,201),("Изумрудный",52,211,153),("Зелёный",111,207,111),
  ("Лаймовый",181,218,93),("Жёлтый",246,216,102),("Золотистый",228,181,64),
  ("Апельсиновый",255,164,82),("Коралловый",255,139,118),("Красный",247,112,125),
  ("Малиновый",238,106,159),("Фуксия",226,123,218),("Фиолетовый",168,134,249),
  ("Сиреневый",206,171,239),("Синий",124,153,246),("Небесный",95,204,242),
  ("Аквамарин",89,220,223),("Песочный",211,185,148),("Жемчужный",224,229,235)};
    readonly List<(string Id, Ellipse Dot)> profileDots = [];
    string IconKey(VpnProfile p)
    {
        if (p.Icon != "auto" && Icons.Any(x => x.Key == p.Icon))
            return p.Icon;
        var groups = ApplicationGroups.Create(p.Apps, detectedPresets, resolveIcons: false);
        var key = groups.Select(g => AppPresets.Definitions.FirstOrDefault(d => "preset:" + d.Id == g.Id)?.ProfileIcon).FirstOrDefault(k => k != null && Icons.Any(i => i.Key == k));
        return key ?? (p.Apps.Count == 0 ? "rules" : "network");
    }
    int ProfileColor(VpnProfile p, string? iconKey = null) => p.IconColor >= 0 && p.IconColor < Palette.Length ? p.IconColor : (iconKey ?? IconKey(p)) switch { "browser" or "chat" => 1, "network" => 2, "rules" => 5, "game" => 3, "star" => 4, _ => 0 };
    Grid ProfileGlyph(VpnProfile p, bool track = true, bool adaptToTheme = false)
    {
        var key = IconKey(p);
        var glyph = Icons.First(x => x.Key == key).Glyph;
        var color = Palette[ProfileColor(p, key)];
        var grid = new Grid { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = new FontIcon { Glyph = glyph, FontSize = 17, Foreground = Color(color.R, color.G, color.B) };
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, color.R, color.G, color.B)), Child = icon });
        if (adaptToTheme)
        {
            void UpdateContrast() => icon.Foreground = grid.ActualTheme == ElementTheme.Light
                ? Color((byte)(color.R * .48), (byte)(color.G * .48), (byte)(color.B * .48)) : Color(color.R, color.G, color.B);
            grid.ActualThemeChanged += (_, _) => UpdateContrast();
            UpdateContrast();
        }
        if (track)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Color(83, 235, 174), Stroke = Color(23, 37, 53), StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -1, -1), Visibility = Visibility.Collapsed };
            grid.Children.Add(dot);
            profileDots.Add((p.Id, dot));
        }
        return grid;
    }
    Grid ProfileNormal(VpnProfile p)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(34)
        });
        grid.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.Children.Add(ProfileGlyph(p));
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(p.Name, 13, true));
        text.Children.Add(new TextBlock { Text = settings.Sources.FirstOrDefault(s => s.Id == p.SourceId)?.Kind ?? "Нет подключения", FontSize = 11, Foreground = Color(158, 178, 198) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }
    void UpdateProfileDots()
    {
        foreach (var item in profileDots)
        {
            item.Dot.Visibility = engine.Running && engine.ActiveProfileId == item.Id ? Visibility.Visible : Visibility.Collapsed;
            item.Dot.Fill = engine.Healthy ? Color(0, 145, 125) : Color(210, 135, 35);
        }
    }
    Task EditProfileIcon() => Profile == null ? Task.CompletedTask : EditProfileIdentity(Profile);
    async Task EditProfileIdentity(VpnProfile profile)
    {
        if (dialogOpen)
            return;
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
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "Название и оформление", Content = scroll, PrimaryButtonText = "Сохранить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary };
        nameInput.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = nameInput.Text.Trim().Length > 0;
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = nameInput.Text.Trim().Length == 0;
        ReviewDialog(dialog);
        dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                profile.Name = nameInput.Text.Trim();
                profile.Icon = example.Icon;
                profile.IconColor = example.IconColor;
                Store.Save(settings);
                BindAll();
                SelectPage(page);
            }
        }
        finally { dialogOpen = false; }
    }
}
