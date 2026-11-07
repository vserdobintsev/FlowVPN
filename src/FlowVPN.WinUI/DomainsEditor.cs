using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    sealed class DomainEntry(string text)
    {
        public string Text = text;
    }
    async Task EditDomains()
    {
        var profile = Profile;
        if (profile == null || dialogOpen)
            return;
        var domainSelection = BaseDomainSelection.From(settings);
        DomainCoverage? coverage = settings.BaseRulesEnabled ? DomainRepository.Current(domainSelection.Categories) : null;
        var entries = profile.Domains.Select(d => new DomainEntry(d)).ToList();
        var rows = new Dictionary<DomainEntry, (Border Card, TextBox Input, FontIcon Indicator, Button Remove)>();
        var input = new TextBox { PlaceholderText = "Домен или ссылка", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(input, "Добавить адрес сайта");
        var add = new Button { Content = new FontIcon { Glyph = "\uE710", FontSize = 16 }, Width = 36, Height = 32 };
        ToolTipService.SetToolTip(add, "Добавить адрес");
        AutomationProperties.SetName(add, "Добавить адрес");
        var inputRow = new Grid { ColumnSpacing = 8 };
        inputRow.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        inputRow.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        inputRow.Children.Add(input);
        Grid.SetColumn(add, 1);
        inputRow.Children.Add(add);
        var paste = new Button { Content = CommandContent("\uE77F", "Вставить список") };
        var removeDuplicates = new Button { Content = CommandContent("\uE74D", "Удалить дубликаты"), Visibility = Visibility.Collapsed };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(paste);
        toolbar.Children.Add(removeDuplicates);
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            MaxHeight = Math.Clamp(root.ActualHeight - 370, 155, 300)
        };
        var hint = new TextBlock { Text = "Вставляйте ссылки целиком или списком. Поддомены включаются автоматически.", FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap };
        var status = new TextBlock { FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Width = Math.Min(480, Math.Max(320, root.ActualWidth - 120)), Spacing = 12 };
        panel.Children.Add(hint);
        panel.Children.Add(inputRow);
        panel.Children.Add(toolbar);
        panel.Children.Add(list);
        panel.Children.Add(status);
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.ActualTheme,
            Title = "Сайты через VPN",
            Content = panel,
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };
        int duplicates = 0;
        bool rebuilding = false, closed = false;
        string? Host(DomainEntry entry)
        {
            var parsed = DomainInput.Parse(entry.Text);
            return parsed.InvalidLines.Count == 0 && parsed.Domains.Count == 1 ? parsed.Domains[0] : null;
        }
        void Validate()
        {
            if (rebuilding || closed)
                return;
            int invalid = 0;
            duplicates = 0;
            foreach (var entry in entries)
            {
                string? host = Host(entry), match = host == null ? null : coverage?.Match(host);
                var row = rows[entry];
                if (host == null)
                    invalid++;
                if (match != null)
                    duplicates++;
                bool marked = match != null || host == null;
                row.Card.Background = marked ? (root.ActualTheme == ElementTheme.Dark ? Color(67, 42, 45) : Color(255, 235, 238)) : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                row.Indicator.Visibility = marked ? Visibility.Visible : Visibility.Collapsed;
                string message = host == null ? "Не удалось распознать домен. Можно вставить полный адрес сайта."
                    : match != null ? $"Уже покрыт общими доменными правилами: {match}. Дополнительное правило не требуется, пока база включена." : "";
                ToolTipService.SetToolTip(row.Card, message.Length > 0 ? message : null);
                AutomationProperties.SetHelpText(row.Input, message);
            }
            removeDuplicates.Visibility = coverage != null && duplicates > 0 ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = invalid == 0;
            status.Text = invalid > 0 ? $"Проверьте адреса: {invalid}" : duplicates > 0 ? $"Уже в общей базе: {duplicates} · в sing-box повторно не отправляются"
                : $"Домены профиля: {entries.Count}" + (!settings.BaseRulesEnabled ? " · общая база выключена" : domainSelection.Categories.Count == 0 ? " · общие доменные категории не выбраны" : coverage == null ? " · доменная база ещё не загружена" : "");
        }
        void Fill()
        {
            rebuilding = true;
            try
            {
                list.Items.Clear();
                rows.Clear();
                foreach (var entry in entries)
                {
                    var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 6, 0, 6) };
                    row.ColumnDefinitions.Add(new()
                    {
                        Width = new GridLength(1, GridUnitType.Star)
                    });
                    row.ColumnDefinitions.Add(new()
                    {
                        Width = GridLength.Auto
                    });
                    var edit = new TextBox { Text = entry.Text, HorizontalAlignment = HorizontalAlignment.Stretch };
                    row.Children.Add(edit);
                    AutomationProperties.SetName(edit, "Домен профиля");
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                    var indicator = new FontIcon { Glyph = "\uE946", FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Foreground = root.ActualTheme == ElementTheme.Dark ? Color(255, 175, 183) : Color(155, 38, 53) };
                    actions.Children.Add(indicator);
                    var remove = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 }, Width = 36, Height = 32, Padding = new Thickness(0), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
                    ToolTipService.SetToolTip(remove, "Удалить домен");
                    AutomationProperties.SetName(remove, "Удалить " + entry.Text);
                    actions.Children.Add(remove);
                    Grid.SetColumn(actions, 1);
                    row.Children.Add(actions);
                    var card = new Border { Child = row, CornerRadius = new CornerRadius(7) };
                    rows[entry] = (card, edit, indicator, remove);
                    edit.TextChanged += (_, _) => { entry.Text = edit.Text; if (rows.Count == entries.Count) Validate(); };
                    edit.LostFocus += (_, _) => { if (!rebuilding && !closed && rows.ContainsKey(entry) && Host(entry) is { } host) edit.Text = host; };
                    remove.Click += (_, _) => { entries.Remove(entry); Fill(); };
                    list.Items.Add(new ListViewItem { Content = card, Padding = new Thickness(0), BorderThickness = new Thickness(0), Margin = new Thickness(0, 3, 0, 3), HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
                if (entries.Count == 0)
                    list.Items.Add(new TextBlock { Text = "Добавьте сайты, которые должны открываться через VPN.", FontSize = 13, Opacity = .7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) });
            }
            finally { rebuilding = false; }
            Validate();
        }
        void AddText(string value)
        {
            if (closed)
                return;
            var parsed = DomainInput.Parse(value);
            var existing = entries.Select(Host).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string line in parsed.Lines)
                if (!existing.Contains(line))
                {
                    entries.Add(new(line));
                    existing.Add(line);
                }
            input.Text = "";
            Fill();
        }
        async Task Paste()
        {
            try
            {
                var clipboard = Clipboard.GetContent();
                if (clipboard.Contains(StandardDataFormats.Text))
                    AddText(await clipboard.GetTextAsync());
            }
            catch { status.Text = "Не удалось прочитать буфер обмена."; }
        }
        add.Click += (_, _) => AddText(input.Text);
        paste.Click += async (_, _) => await Paste();
        input.Paste += async (_, args) => { args.Handled = true; await Paste(); };
        input.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { AddText(input.Text); args.Handled = true; } };
        removeDuplicates.Click += (_, _) => { entries.RemoveAll(e => Host(e) is { } host && coverage?.Match(host) != null); Fill(); };
        dialog.PrimaryButtonClick += (_, args) => { if (input.Text.Trim().Length > 0) AddText(input.Text); Validate(); args.Cancel = entries.Any(e => Host(e) == null); };
        dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
        if (Entry.VisualTest)
            entries = [new("chatgpt.com"), new("sub.chatgpt.com"), new("example.com")];
        Fill();
        ReviewDialog(dialog, () => CheckDomainEditorLayout(panel, input, rows.Values));
        dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                profile.Domains = entries.Select(Host).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Save();
                BindProfile();
            }
        }
        finally { closed = true; dialogOpen = false; }
    }
}
