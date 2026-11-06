using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task PickApplications()
    {
        var profile = Profile;
        if (profile == null)
            throw new Exception("Создайте профиль.");
        if (dialogOpen)
            return;
        C<Button>("AddApp").IsEnabled = false;
        List<CatalogApp> all;
        try
        {
            var snapshot = await Task.Run(() => { var found = AppPresets.Discover(); return (Presets: found, Apps: AppCatalog.Read(found)); });
            all = snapshot.Apps;
            detectedPresets = snapshot.Presets;
        }
        finally { if (!windowClosed) C<Button>("AddApp").IsEnabled = true; }
        if (windowClosed || Profile != profile || dialogOpen)
            return;
        var presets = detectedPresets.Select(p => all.FirstOrDefault(a => a.PresetId == p.Definition.Id) ?? p.ToApp()).ToList();
        var selection = new ApplicationSelection(profile);
        var browser = new PresetBrowser(presets);
        bool Existing(CatalogApp app) => app.Rules.Count > 0 && app.Rules.All(selection.Existing);
        var query = new TextBox { PlaceholderText = "Найти приложение", HorizontalAlignment = HorizontalAlignment.Stretch };
        var filter = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        filter.Items.Add("Готовые наборы");
        filter.Items.Add("Все приложения");
        filter.Items.Add("Только запущенные");
        filter.SelectedIndex = 0;
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Height = Math.Clamp(root.ActualHeight - 370, 190, 390)
        };
        var count = new TextBlock { FontSize = 12, Opacity = .7 };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 };
        var navigation = new Grid { ColumnSpacing = 10, Visibility = Visibility.Collapsed };
        navigation.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        navigation.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        var back = new Button { Content = new FontIcon { Glyph = "\uE72B", FontSize = 15 }, Width = 36, Height = 32, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(back, "Назад к наборам");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(back, "Назад к наборам");
        var breadcrumb = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        navigation.Children.Add(back);
        Grid.SetColumn(breadcrumb, 1);
        navigation.Children.Add(breadcrumb);
        var panel = new StackPanel { Width = Math.Min(480, Math.Max(320, root.ActualWidth - 120)), Spacing = 12 };
        panel.Children.Add(query);
        panel.Children.Add(filter);
        panel.Children.Add(navigation);
        panel.Children.Add(note);
        panel.Children.Add(list);
        panel.Children.Add(count);
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.ActualTheme,
            Title = "Выбрать приложения",
            Content = panel,
            PrimaryButtonText = "Добавить выбранные",
            CloseButtonText = "Отмена",
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Primary
        };
        var applicationChecks = new Dictionary<string, CheckBox>();
        using var cancel = new CancellationTokenSource();
        CancellationTokenSource? iconBatch = null;
        var imageRows = new Dictionary<string, Image>();
        var rowMetrics = new List<(string Name, FrameworkElement Title, FrameworkElement Icon)>();
        void UpdateCount()
        {
            count.Text = $"Выбрано: {selection.Count}  ·  Компонентов: {selection.Components}";
            dialog.IsPrimaryButtonEnabled = selection.Count > 0;
            dialog.PrimaryButtonText = selection.Count > 0 ? $"Добавить · {selection.Count}" : "Добавить выбранные";
        }
        void Fill()
        {
            list.Items.Clear();
            imageRows.Clear();
            applicationChecks.Clear();
            rowMetrics.Clear();
            bool nested = filter.SelectedIndex == 0 && browser.Parent != null;
            bool small = root.ActualHeight < 700;
            navigation.Visibility = nested ? Visibility.Visible : Visibility.Collapsed;
            filter.Visibility = nested ? Visibility.Collapsed : Visibility.Visible;
            note.Visibility = nested && small ? Visibility.Collapsed : Visibility.Visible;
            breadcrumb.Text = "Готовые наборы  ›  " + browser.Parent?.Name;
            list.Height = Math.Clamp(root.ActualHeight - (nested ? (small ? 300 : 376) : 380), 150, 390);
            note.Text = filter.SelectedIndex == 0
                ? nested ? "Выбирайте основное приложение и расширения независимо. Раскройте карточку для выбора компонентов."
                    : "Откройте приложение с расширениями или выберите отдельный набор. Добавляются только найденные компоненты."
                : "Отметьте несколько приложений. Для программ с несколькими компонентами используйте готовый набор.";
            var visible = (filter.SelectedIndex == 0 ? browser.Visible(query.Text) : all.Where(a => (filter.SelectedIndex != 2 || a.Running) && PresetHierarchy.Matches(a, query.Text, presets))).ToList();
            bool extensionHeading = false;
            foreach (var app in visible)
            {
                if (nested && query.Text.Length == 0)
                {
                    if (app.PresetId == browser.ParentId)
                    {
                        if (!small)
                            list.Items.Add(new TextBlock { Text = "Основное приложение", FontSize = 12, Opacity = .7, Margin = new Thickness(2, 0, 0, 8) });
                    }
                    else if (!extensionHeading)
                    {
                        extensionHeading = true;
                        list.Items.Add(new TextBlock { Text = "Расширения", FontSize = 12, Opacity = .7, Margin = new Thickness(2, 12, 0, 8) });
                    }
                }
                bool installed = app.Rules.Count > 0, existing = Existing(app);
                bool navigable = filter.SelectedIndex == 0 && browser.IsNavigation(app, query.Text);
                var children = PresetHierarchy.Children(app.PresetId, presets);
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(2, 6, 2, 6), MinHeight = 62 };
                row.ColumnDefinitions.Add(new()
                {
                    Width = new GridLength(36)
                });
                row.ColumnDefinitions.Add(new()
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
                var placeholder = new FontIcon { Glyph = "\uE71D", FontSize = 20, Opacity = .45 };
                var icon = new Image { Width = 30, Height = 30, Tag = placeholder };
                var iconHost = new Grid { Width = 36, Height = 36 };
                iconHost.Children.Add(placeholder);
                iconHost.Children.Add(icon);
                row.Children.Add(iconHost);
                if (installed)
                    imageRows[app.Id] = icon;
                string context = PresetHierarchy.Context(app, presets);
                string title = app.Name + (context.Length > 0 && (!nested || query.Text.Length > 0) ? " · " + context : "");
                var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                var titleText = Text(title, 14, true);
                titleText.TextWrapping = TextWrapping.Wrap;
                labels.Children.Add(titleText);
                rowMetrics.Add((app.Id, titleText, iconHost));
                int missing = app.Rules.Count(r => !selection.Existing(r));
                string description = !installed ? "Не найдено на этом устройстве" : existing ? "Уже в профиле" : app.PresetId.Length > 0
                    ? $"Найдено компонентов: {app.Rules.Count}" + (missing < app.Rules.Count ? $" · добавить: {missing}" : "") : app.Origin;
                if (app.Running && !existing)
                    description = "Запущено · " + description;
                if (navigable)
                    description = $"Расширений найдено: {children.Count(c => c.Rules.Count > 0)} из {children.Count}" + (children.Any(c => selection.Selected(c) > 0) ? " · есть выбранные" : "");
                labels.Children.Add(new TextBlock { Text = description, FontSize = 11, Opacity = .65, TextWrapping = TextWrapping.Wrap });
                if (app.PresetId.Length > 0)
                    labels.Children.Add(new TextBlock { Text = navigable ? string.Join(" · ", children.Select(c => c.Name).Distinct()) : app.Origin, FontSize = 11, Opacity = .75, TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(labels, 1);
                row.Children.Add(labels);
                var heading = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
                heading.ColumnDefinitions.Add(new()
                {
                    Width = new GridLength(30)
                });
                heading.ColumnDefinitions.Add(new()
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
                var checkbox = new CheckBox { IsChecked = existing || selection.Selected(app) > 0, IsEnabled = installed && !existing, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(checkbox, "Выбрать приложение «" + app.Name + "»");
                heading.Children.Add(navigable ? new SymbolIcon(Symbol.Link) { Opacity = .6, VerticalAlignment = VerticalAlignment.Center } : checkbox);
                Grid.SetColumn(row, 1);
                heading.Children.Add(row);
                if (navigable)
                {
                    heading.ColumnDefinitions.Add(new()
                    {
                        Width = new GridLength(24)
                    });
                    var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(chevron, 2);
                    heading.Children.Add(chevron);
                    var open = new Button
                    {
                        Content = heading,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(16, 0, 16, 0),
                        CornerRadius = new CornerRadius(8)
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(open, app.Name + ": приложение и расширения");
                    open.Click += (_, _) => { browser.Enter(app.PresetId); query.Text = ""; Fill(); motion.Enter(list, horizontal: true); back.Focus(FocusState.Programmatic); };
                    list.Items.Add(new ListViewItem { Content = open, Tag = app, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), Margin = new Thickness(0, 3, 0, 3) });
                    continue;
                }
                applicationChecks[app.Id] = checkbox;
                bool changing = false;
                var components = new StackPanel { Spacing = 12 };
                var componentChecks = new List<(AppRule Rule, CheckBox Check)>();
                foreach (var rule in app.Rules)
                {
                    var content = ComponentPresentation.Create(rule, selection.Existing(rule));
                    var check = new CheckBox { Content = content, IsChecked = selection.Existing(rule) || selection.Contains(app, rule), IsEnabled = !selection.Existing(rule), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
                    void Changed()
                    {
                        if (changing)
                            return;
                        selection.SetComponent(app, rule, check.IsChecked == true);
                        changing = true;
                        checkbox.IsChecked = selection.Selected(app) > 0 || existing;
                        changing = false;
                        UpdateCount();
                    }
                    check.Checked += (_, _) => Changed();
                    check.Unchecked += (_, _) => Changed();
                    components.Children.Add(check);
                    componentChecks.Add((rule, check));
                }
                void SelectApplication()
                {
                    if (changing)
                        return;
                    changing = true;
                    selection.SetApplication(app, checkbox.IsChecked == true);
                    foreach (var component in componentChecks)
                        component.Check.IsChecked = selection.Existing(component.Rule) || selection.Contains(app, component.Rule);
                    checkbox.IsChecked = existing || selection.Selected(app) > 0;
                    changing = false;
                    UpdateCount();
                }
                checkbox.Checked += (_, _) => SelectApplication();
                checkbox.Unchecked += (_, _) => SelectApplication();
                object presentation = app.PresetId.Length > 0 ? new Expander { Header = heading, Content = components, IsEnabled = installed, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch } : new Border { Child = heading, Padding = new Thickness(16, 0, 16, 0) };
                var item = new ListViewItem { Content = presentation, Tag = app, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(0, 3, 0, 3) };
                ToolTipService.SetToolTip(item, app.PresetId.Length > 0 ? app.Origin + "\n" + string.Join("\n", app.Rules.Select(r => r.Name + ": " + r.Path)) : app.Path);
                list.Items.Add(item);
            }
            UpdateCount();
            iconBatch?.Cancel();
            iconBatch?.Dispose();
            iconBatch = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            _ = LoadCatalogIcons(imageRows.ToArray(), visible, iconBatch.Token);
        }
        back.Click += (_, _) => { browser.Back(); query.Text = ""; Fill(); motion.Enter(list, backwards: true, horizontal: true); };
        query.TextChanged += (_, _) => Fill();
        filter.SelectionChanged += (_, _) => Fill();
        Fill();
        if (Entry.VisualTest)
        {
            filter.SelectedIndex = 1;
            Fill();
            var candidate = applicationChecks.First(i => i.Value.IsEnabled && ((CatalogApp)list.Items.OfType<ListViewItem>().First(r => ((CatalogApp)r.Tag).Id == i.Key).Tag).PresetId.Length == 0);
            string id = candidate.Key;
            candidate.Value.IsChecked = true;
            if (selection.Count != 1)
                throw new Exception("Application selection failed");
            query.Text = "flowvpn-no-match-test-12345";
            Fill();
            await Task.Delay(100);
            if (list.Items.Count != 0 || selection.Count != 1)
                throw new Exception("Search lost the selection");
            query.Text = "";
            filter.SelectedIndex = 0;
            Fill();
            if (selection.Count != 1)
                throw new Exception("Preset filter lost the selection");
            if (list.Items.OfType<ListViewItem>().Any(i => ((CatalogApp)i.Tag).Rules.Count == 0 && applicationChecks.TryGetValue(((CatalogApp)i.Tag).Id, out var check) && check.IsEnabled))
                throw new Exception("Missing preset is selectable");
            filter.SelectedIndex = 1;
            Fill();
            await Task.Delay(100);
            if (applicationChecks[id].IsChecked != true)
                throw new Exception("Selection was not restored");
            applicationChecks[id].IsChecked = false;
            filter.SelectedIndex = 2;
            Fill();
            await Task.Delay(100);
            if (list.Items.OfType<ListViewItem>().Any(i => !((CatalogApp)i.Tag).Running))
                throw new Exception("Running filter failed");
            filter.SelectedIndex = 0;
            Fill();
            if (selection.Count != 0)
                throw new Exception("Application deselection failed");
            var parent = presets.FirstOrDefault(p => PresetHierarchy.HasChildren(p, presets) && PresetHierarchy.Children(p.PresetId, presets).Any(c => c.Rules.Count > 0));
            if (parent != null)
            {
                browser.Enter(parent.PresetId);
                Fill();
                var child = PresetHierarchy.Children(parent.PresetId, presets).First(c => c.Rules.Count > 0);
                applicationChecks[child.Id].IsChecked = true;
                if (selection.Selected(parent) > 0 || selection.Selected(child) == 0)
                    throw new Exception("Extension selection selected its host");
                browser.Back();
                Fill();
                browser.Enter(parent.PresetId);
                Fill();
                if (applicationChecks[child.Id].IsChecked != true)
                    throw new Exception("Nested navigation lost extension selection");
                applicationChecks[child.Id].IsChecked = false;
                browser.Home();
                Fill();
            }
            File.WriteAllText(Path.Combine(Store.Root, "design-preview", "picker-checks.json"), "{\"selection\":true,\"selectionSurvivesSearchAndTabs\":true,\"deselection\":true,\"runningFilter\":true,\"missingPresetsDisabled\":true}");
            if (visualDialogName?.StartsWith("preset-components") == true)
            {
                browser.Enter("vscode");
                query.Text = "";
                Fill();
                if (list.Items.OfType<ListViewItem>().FirstOrDefault(i => ((CatalogApp)i.Tag).PresetId == "codex-vscode")?.Content is Expander expander)
                    expander.IsExpanded = true;
            }
            if (visualDialogName == "application-extensions")
            {
                browser.Enter("vscode");
                query.Text = "";
                Fill();
            }
        }
        if (Entry.VisualTest)
            dialog.Opened += async (_, _) =>
        {
            try
            {
                await Task.Delay(450);
                var positions = rowMetrics.Where(r => r.Title.ActualWidth > 0).Select(r => new
                {
                    r.Name,
                    TitleX = r.Title.TransformToVisual(list).TransformPoint(new Windows.Foundation.Point()).X,
                    IconX = r.Icon.TransformToVisual(list).TransformPoint(new Windows.Foundation.Point()).X
                }).ToArray();
                if (positions.Length > 1 && (positions.Max(p => p.TitleX) - positions.Min(p => p.TitleX) > 2 || positions.Max(p => p.IconX) - positions.Min(p => p.IconX) > 2))
                    throw new Exception("Catalogue row columns do not align");
                File.WriteAllText(Path.Combine(Store.Root, "design-preview", (visualDialogName ?? "picker") + "-columns.json"), System.Text.Json.JsonSerializer.Serialize(positions, Store.JsonOptions));
            }
            catch (Exception e) { visualDialogError = e; }
        };
        ReviewDialog(dialog);
        dialog.Opened += (_, _) => query.Focus(FocusState.Programmatic);
        dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var fresh = await Task.Run(AppPresets.Discover);
                var rules = selection.Resolve(fresh);
                if (windowClosed || !settings.Profiles.Contains(profile))
                    return;
                ApplicationGroups.Add(profile, rules);
                Save();
                BindProfile();
            }
        }
        finally { motion.Stop(list); dialogOpen = false; cancel.Cancel(); iconBatch?.Cancel(); iconBatch?.Dispose(); }
    }
    async Task LoadCatalogIcons(KeyValuePair<string, Image>[] images, List<CatalogApp> apps, CancellationToken token)
    {
        var byId = new Dictionary<string, CatalogApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
            byId.TryAdd(app.Id, app);
        foreach (var row in images)
        {
            if (token.IsCancellationRequested)
                return;
            try
            {
                if (!byId.TryGetValue(row.Key, out var app))
                    continue;
                var png = await AppIcons.Load(app, token);
                if (png == null || token.IsCancellationRequested)
                    continue;
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(png);
                    await writer.StoreAsync();
                }
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                if (!token.IsCancellationRequested)
                {
                    row.Value.Source = bitmap;
                    if (row.Value.Tag is FontIcon placeholder)
                        placeholder.Visibility = Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }
}
