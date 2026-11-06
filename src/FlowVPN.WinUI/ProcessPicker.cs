using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task PickProcesses()
    {
        var profile = Profile;
        if (profile == null)
            throw new InvalidOperationException("Создайте профиль.");
        if (dialogOpen)
            return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        CancellationTokenSource? iconBatch = null;
        var selection = new ApplicationSelection(profile);
        ProcessInventory? inventory = null;
        bool refreshing = false, finished = false;
        var search = new TextBox { PlaceholderText = "Приложение, компания, путь или PID" };
        var filter = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (string name in new[] { "Приложения", "Все процессы", "С готовыми наборами", "Фоновые", "Системные" })
            filter.Items.Add(name);
        filter.SelectedIndex = 0;
        var refresh = new Button { Content = CommandContent("\uE72C", "Обновить"), MinHeight = 32, Padding = new Thickness(12, 5, 12, 5) };
        ToolTipService.SetToolTip(refresh, "Обновить список, сохранив выбор");
        AutomationProperties.SetName(refresh, "Обновить список процессов");
        var toolbar = new Grid { ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        toolbar.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        toolbar.Children.Add(filter);
        Grid.SetColumn(refresh, 1);
        toolbar.Children.Add(refresh);
        var status = new TextBlock { Text = "Собираем сведения о приложениях…", FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap };
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Height = Math.Clamp(root.ActualHeight - 380, 155, 390)
        };
        var count = new TextBlock { FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Width = Math.Min(480, Math.Max(320, root.ActualWidth - 120)), Spacing = 12 };
        panel.Children.Add(search);
        panel.Children.Add(toolbar);
        panel.Children.Add(status);
        panel.Children.Add(list);
        panel.Children.Add(count);
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.ActualTheme,
            Title = "Запущенные приложения",
            Content = panel,
            PrimaryButtonText = "Добавить выбранные",
            CloseButtonText = "Отмена",
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Primary
        };

        void UpdateCount()
        {
            count.Text = selection.Count > 0 ? $"Выбрано приложений: {selection.Count} · компонентов: {selection.Components}" : "Выберите приложения. Состав набора можно изменить в карточке.";
            dialog.IsPrimaryButtonEnabled = selection.Count > 0 && !refreshing;
            dialog.PrimaryButtonText = selection.Count > 0 ? $"Добавить · {selection.Count}" : "Добавить выбранные";
        }
        void Fill()
        {
            list.Items.Clear();
            iconBatch?.Cancel();
            iconBatch?.Dispose();
            iconBatch = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            if (inventory == null)
            {
                UpdateCount();
                return;
            }
            var visible = ProcessCatalog.Filter(inventory.Applications, (ProcessFilter)filter.SelectedIndex, search.Text);
            var images = new List<KeyValuePair<string, Image>>();
            ProcessCategory? section = null;
            foreach (var running in visible)
            {
                var app = running.App;
                if (section != running.Category)
                {
                    section = running.Category;
                    string title = section switch
                    {
                        ProcessCategory.Known => "Распознаны · готовые наборы",
                        ProcessCategory.Application => "Приложения с окнами",
                        ProcessCategory.Background => "Фоновые приложения и службы",
                        _ => "Компоненты Windows"
                    };
                    list.Items.Add(new TextBlock { Text = title, FontSize = 12, Opacity = .7, Margin = new Thickness(2, 10, 0, 6) });
                }
                list.Items.Add(ProcessCard(running, selection, UpdateCount, images));
            }
            if (visible.Count == 0)
                list.Items.Add(new TextBlock { Text = "В этой категории ничего не найдено. Измените поиск или выберите «Все процессы».", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) });
            var visibleIds = visible.Select(a => a.App.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int hiddenSelection = selection.SelectedIds.Count(id => !visibleIds.Contains(id));
            status.Text = $"Показано приложений: {visible.Count} · обновлено в {inventory.CapturedAt:HH:mm:ss}";
            if (hiddenSelection > 0)
                status.Text += $"\nВыбрано вне текущего списка: {hiddenSelection}";
            ToolTipService.SetToolTip(status, $"Проверено процессов: {inventory.Inspected}. Недоступных или завершившихся: {inventory.Unavailable}.\nКатегория определяется по наличию окна и расположению файла.");
            _ = LoadCatalogIcons(images.ToArray(), visible.Select(a => a.App).ToList(), iconBatch.Token);
            UpdateCount();
        }
        async Task Refresh()
        {
            if (refreshing || finished)
                return;
            refreshing = true;
            refresh.IsEnabled = false;
            UpdateCount();
            status.Text = "Обновляем список…";
            try
            {
                var result = await Task.Run(() => { var presets = AppPresets.Discover(); return (Presets: presets, Inventory: ProcessCatalog.Read(presets, cancel.Token)); }, cancel.Token);
                if (finished)
                    return;
                detectedPresets = result.Presets;
                inventory = result.Inventory;
                Fill();
                motion.Enter(list);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception e) { if (!finished) status.Text = "Не удалось обновить список. " + e.Message; }
            finally { refreshing = false; if (!finished) { refresh.IsEnabled = true; UpdateCount(); } }
        }
        search.TextChanged += (_, _) => Fill();
        filter.SelectionChanged += (_, _) => Fill();
        refresh.Click += async (_, _) => await Refresh();
        dialog.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        dialog.Closed += (_, _) => { finished = true; cancel.Cancel(); };
        dialogOpen = true;
        try
        {
            // Show immediately; slow metadata/icon discovery never blocks typing or closing the dialog.
            Task<ContentDialogResult> shown = dialog.ShowAsync().AsTask();
            await Refresh();
            if (Entry.VisualTest)
            {
                if (inventory == null)
                    throw new InvalidOperationException("Process enumeration failed");
                if (visualDialogName?.Contains("details") == true && list.Items.OfType<ListViewItem>().FirstOrDefault()?.Content is Expander first)
                    first.IsExpanded = true;
                if (visualDialogName?.Contains("system") == true)
                    filter.SelectedIndex = (int)ProcessFilter.System;
                await Task.Delay(650);
                await CaptureElement(dialog, visualDialogName ?? "running-applications");
                File.WriteAllText(Path.Combine(Store.Root, "design-preview", "process-inventory.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    inventory.Inspected,
                    inventory.Unavailable,
                    Groups = inventory.Applications.Select(a => new { a.App.Name, Category = a.Category.ToString(), Processes = a.Processes.Count, Components = a.App.Rules.Count })
                }, Store.JsonOptions));
                dialog.Hide();
            }
            if (await shown == ContentDialogResult.Primary && settings.Profiles.Contains(profile))
            {
                var fresh = await Task.Run(AppPresets.Discover, windowLifetime.Token);
                if (windowClosed || !settings.Profiles.Contains(profile))
                    return;
                ApplicationGroups.Add(profile, selection.Resolve(fresh));
                Save();
                BindProfile();
            }
        }
        finally { finished = true; motion.Stop(list); dialog.Hide(); dialogOpen = false; cancel.Cancel(); iconBatch?.Cancel(); iconBatch?.Dispose(); }
    }

    ListViewItem ProcessCard(RunningApplication running, ApplicationSelection selection, Action updateCount, List<KeyValuePair<string, Image>> images)
    {
        var app = running.App;
        bool existing = app.Rules.All(selection.Existing), changing = false;
        var header = new Grid { ColumnSpacing = 12, MinHeight = 68, Padding = new Thickness(0, 6, 0, 6) };
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
        var check = new CheckBox { IsChecked = existing || selection.Selected(app) > 0, IsEnabled = !existing, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(check, "Выбрать «" + app.Name + "»");
        header.Children.Add(check);
        var fallback = new FontIcon { Glyph = running.Category == ProcessCategory.System ? "\uE713" : "\uE71D", FontSize = 23, Opacity = .5 };
        var image = new Image { Width = 32, Height = 32, Tag = fallback };
        var icon = new Grid { Width = 36, Height = 36 };
        icon.Children.Add(fallback);
        icon.Children.Add(image);
        Grid.SetColumn(icon, 1);
        header.Children.Add(icon);
        images.Add(new(app.Id, image));
        var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var name = Text(app.Name, 14, true);
        name.TextWrapping = TextWrapping.Wrap;
        labels.Children.Add(name);
        string description = existing ? "Уже в профиле" : app.PresetId.Length > 0 ? "Готовый набор · компонентов: " + app.Rules.Count : Path.GetFileName(app.Path);
        labels.Children.Add(new TextBlock { Text = description + " · процессов: " + running.Processes.Count, FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        string company = string.Join(" · ", running.Processes.Select(p => p.Company).Where(s => s.Length > 0).Distinct());
        if (company.Length > 0 && app.PresetId.Length == 0)
            labels.Children.Add(new TextBlock { Text = company, FontSize = 11, Opacity = .55, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(labels, 2);
        header.Children.Add(labels);
        var details = new StackPanel { Spacing = 14 };
        var components = new List<(AppRule Rule, CheckBox Check)>();
        if (app.PresetId.Length > 0)
        {
            details.Children.Add(new TextBlock { Text = "В набор входят найденные компоненты, в том числе сейчас не запущенные.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 });
            foreach (var rule in app.Rules)
            {
                var content = new StackPanel { Spacing = 5 };
                content.Children.Add(ComponentPresentation.Create(rule, selection.Existing(rule)));
                var active = running.Processes.Where(p => ProcessCatalog.Matches(p, rule)).ToArray();
                content.Children.Add(new TextBlock { Text = active.Length > 0 ? $"Запущен · PID: {string.Join(", ", active.Select(p => p.Id))}" : "Сейчас не запущен", FontSize = 11, Opacity = .65, TextWrapping = TextWrapping.Wrap });
                var component = new CheckBox { Content = content, IsChecked = selection.Existing(rule) || selection.Contains(app, rule), IsEnabled = !selection.Existing(rule), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
                void ChangeComponent()
                {
                    if (changing)
                        return;
                    selection.SetComponent(app, rule, component.IsChecked == true);
                    changing = true;
                    check.IsChecked = existing || selection.Selected(app) > 0;
                    changing = false;
                    updateCount();
                }
                component.Checked += (_, _) => ChangeComponent();
                component.Unchecked += (_, _) => ChangeComponent();
                details.Children.Add(component);
                components.Add((rule, component));
            }
        }
        else
        {
            string versions = string.Join(", ", running.Processes.Select(p => p.Version).Where(s => s.Length > 0).Distinct());
            AddDetail("Исполняемый файл", app.Path);
            if (company.Length > 0)
                AddDetail("Компания из сведений файла", company);
            if (versions.Length > 0)
                AddDetail("Версия файла", versions);
            AddDetail("PID", string.Join(", ", running.Processes.Select(p => p.Id)));
            var titles = running.Processes.Select(p => p.WindowTitle).Where(t => t.Length > 0).Distinct().ToArray();
            if (titles.Length > 0)
                AddDetail("Окна", string.Join("\n", titles));
            details.Children.Add(new TextBlock { Text = "Добавится только этот EXE. Другие файлы в его папке не включаются.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 });
        }
        void AddDetail(string label, string value)
        {
            var text = new StackPanel { Spacing = 3 };
            text.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = .65 });
            text.Children.Add(new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            details.Children.Add(text);
        }
        void ChangeApplication()
        {
            if (changing)
                return;
            changing = true;
            selection.SetApplication(app, check.IsChecked == true);
            foreach (var component in components)
                component.Check.IsChecked = selection.Existing(component.Rule) || selection.Contains(app, component.Rule);
            check.IsChecked = existing || selection.Selected(app) > 0;
            changing = false;
            updateCount();
        }
        check.Checked += (_, _) => ChangeApplication();
        check.Unchecked += (_, _) => ChangeApplication();
        return new ListViewItem
        {
            Content = new Expander { Header = header, Content = details, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch },
            Tag = running,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 3, 0, 3)
        };
    }
}
