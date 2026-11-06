using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    sealed class ProfileSetsView
    {
        public readonly StackPanel Commands = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
        public readonly ToggleButton Compose = new() { Content = "Скомпоновать набор", CornerRadius = new(8) };
        public readonly Button Profiles = new() { Content = "Создать профили…", CornerRadius = new(8) };
        public readonly Button Refresh = new() { Content = new FontIcon { Glyph = "\uE72C", FontSize = 16 }, CornerRadius = new(8) };
        public readonly InfoBar Status = new() { IsOpen = false, IsClosable = true };
        public readonly TextBox Search = new() { PlaceholderText = "Найти набор", Visibility = Visibility.Collapsed };
        public readonly TextBlock Selection = new() { FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap };
        public readonly Grid Layout = new() { ColumnSpacing = 18, RowSpacing = 18 };
        public readonly Grid Cards = new() { ColumnSpacing = 10, RowSpacing = 10 };
        public readonly StackPanel Catalogue = new() { Spacing = 10 };
        public readonly ScrollViewer CardScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        public readonly TextBlock EmptyCatalogue = new() { TextWrapping = TextWrapping.Wrap, Opacity = .75 };
        public readonly StackPanel Detail = new() { Spacing = 12 };
        public readonly TextBlock Title = new() { FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        public readonly TextBlock Description = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75 };
        public readonly StackPanel Composer = new() { Spacing = 12, Visibility = Visibility.Collapsed };
        public readonly ComboBox Operation = new() { Header = "Как объединить приложения", HorizontalAlignment = HorizontalAlignment.Stretch };
        public readonly TextBlock OperationHint = new() { FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap };
        public readonly ComboBox Base = new() { Header = "Исходный набор", PlaceholderText = "Из какого набора вычесть остальные?", HorizontalAlignment = HorizontalAlignment.Stretch };
        public readonly TextBlock Count = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        public readonly StackPanel Members = new() { Spacing = 10 };
        public readonly ScrollViewer MemberScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        public readonly TextBlock EmptyResult = new() { TextWrapping = TextWrapping.Wrap, Opacity = .75 };
        public readonly StackPanel SaveForm = new() { Spacing = 10, Visibility = Visibility.Collapsed };
        public readonly TextBox Name = new() { Header = "Название нового набора", PlaceholderText = "Например, рабочие приложения", MaxLength = ProfileSetCatalog.MaximumNameLength };
        public readonly Button Save = new() { Content = "Сохранить набор", HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new(8), IsEnabled = false };
        public readonly StackPanel CustomActions = new() { Orientation = Orientation.Horizontal, Spacing = 10, Visibility = Visibility.Collapsed };
        public readonly Button Rename = new() { Content = "Переименовать", CornerRadius = new(8) };
        public readonly Button Delete = new() { Content = "Удалить", CornerRadius = new(8) };
        public readonly HashSet<string> Selected = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ToggleButton> Buttons = new(StringComparer.Ordinal);
        public IReadOnlyList<ProfileSetEntry> Entries = [];
        public IReadOnlyList<string> Result = [];
        public string? ViewedId, BaseId;
        public bool Composing, Binding, Busy, Loaded;
        public int Columns, CardColumns;
        public CancellationTokenSource? CardIcons, MemberIcons;
    }

    readonly ProfileSetsView sets = new();

    void HookProfileSets()
    {
        ApplyProfileCardColors(sets.Cards);
        var body = C<StackPanel>("ProfileSetsBody");
        body.Children.Add(new TextBlock { Text = "Наборы помогают собрать приложения для профиля. Выберите готовый вариант или создайте свой из нескольких наборов.", TextWrapping = TextWrapping.Wrap, Opacity = .8 });
        sets.Commands.Children.Add(sets.Compose);
        sets.Commands.Children.Add(sets.Profiles);
        sets.Commands.Children.Add(sets.Refresh);
        body.Children.Add(sets.Commands);
        body.Children.Add(sets.Status);
        body.Children.Add(sets.Layout);
        sets.Catalogue.Children.Add(sets.Search);
        sets.Catalogue.Children.Add(sets.Selection);
        sets.CardScroll.Content = sets.Cards;
        sets.Catalogue.Children.Add(sets.CardScroll);
        sets.Catalogue.Children.Add(sets.EmptyCatalogue);
        sets.Layout.Children.Add(sets.Catalogue);
        sets.Layout.Children.Add(sets.Detail);
        foreach (var item in new UIElement[] { sets.Title, sets.Description, sets.Composer, sets.Count, sets.MemberScroll, sets.EmptyResult, sets.SaveForm, sets.CustomActions })
            sets.Detail.Children.Add(item);
        sets.MemberScroll.Content = sets.Members;
        sets.Operation.Items.Add(new ComboBoxItem { Content = "Объединить", Tag = ProfileSetOperation.Union });
        sets.Operation.Items.Add(new ComboBoxItem { Content = "Оставить общее", Tag = ProfileSetOperation.Intersection });
        sets.Operation.Items.Add(new ComboBoxItem { Content = "Вычесть", Tag = ProfileSetOperation.Difference });
        sets.Operation.Items.Add(new ComboBoxItem { Content = "Симметрическая разность", Tag = ProfileSetOperation.SymmetricDifference });
        sets.Operation.SelectedIndex = 0;
        sets.Composer.Children.Add(sets.Operation);
        sets.Composer.Children.Add(sets.OperationHint);
        sets.Composer.Children.Add(sets.Base);
        sets.Composer.Children.Add(new TextBlock { Text = "База блокировок остаётся общей для всех профилей. Здесь выбираются только приложения.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 });
        sets.SaveForm.Children.Add(sets.Name);
        sets.SaveForm.Children.Add(sets.Save);
        sets.SaveForm.Children.Add(new TextBlock { Text = "Состав сохранится как отдельный набор.", FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        sets.CustomActions.Children.Add(sets.Rename);
        sets.CustomActions.Children.Add(sets.Delete);
        foreach (var (control, name) in new (DependencyObject, string)[]
        {
            (sets.Compose, "Скомпоновать набор"), (sets.Profiles, "Создать профили из готовых наборов"),
            (sets.Refresh, "Обновить найденные приложения"), (sets.Search, "Найти набор"),
            (sets.Operation, "Операция над наборами"), (sets.Base, "Исходный набор для вычитания"),
            (sets.Name, "Название нового набора"), (sets.Save, "Сохранить отдельный набор")
        })
            AutomationProperties.SetName(control, name);
        ToolTipService.SetToolTip(sets.Refresh, "Проверить, какие приложения доступны на этом компьютере");
        sets.Compose.Checked += (_, _) => SetComposition(true);
        sets.Compose.Unchecked += (_, _) => SetComposition(false);
        sets.Profiles.Click += async (_, _) =>
        {
            if (settings.Sources.Count == 0)
            {
                SelectPage(2);
                C<Button>("AddConnectionButton").Focus(FocusState.Programmatic);
            }
            else
                await ShowProfileSuggestions();
        };
        sets.Refresh.Click += async (_, _) => await RefreshProfileSets();
        sets.Search.TextChanged += (_, _) => { if (!sets.Binding) BuildProfileSetCards(); };
        sets.Operation.SelectionChanged += (_, _) =>
        {
            if (sets.Binding) return;
            sets.BaseId = null;
            RefreshProfileSetResult();
        };
        sets.Base.SelectionChanged += (_, _) =>
        {
            if (sets.Binding) return;
            sets.BaseId = (sets.Base.SelectedItem as ComboBoxItem)?.Tag as string;
            RefreshProfileSetResult();
        };
        sets.Name.TextChanged += (_, _) => UpdateSetSaveButton();
        sets.Save.Click += (_, _) => SaveComposedSet();
        sets.Rename.Click += async (_, _) => await RunProfileSetAction(RenameProfileSet);
        sets.Delete.Click += async (_, _) => await RunProfileSetAction(DeleteProfileSet);
        sets.Layout.SizeChanged += (_, _) => ArrangeProfileSets();
        sets.Cards.SizeChanged += (_, _) => ArrangeProfileSetCards();
        root.SizeChanged += (_, _) => ArrangeProfileSets();
    }

    void ShowProfileSetsPage()
    {
        sets.Profiles.Content = settings.Sources.Count == 0 ? "Добавить подключение…" : "Создать профили…";
        AutomationProperties.SetName(sets.Profiles, settings.Sources.Count == 0 ? "Добавить подключение для создания профилей" : "Создать профили из готовых наборов");
        if (!sets.Loaded)
            _ = RefreshProfileSets();
        ArrangeProfileSets();
    }

    async Task RefreshProfileSets()
    {
        if (sets.Busy || windowClosed) return;
        sets.Busy = true;
        sets.Refresh.IsEnabled = false;
        sets.Compose.IsEnabled = false;
        UpdateSetSaveButton();
        SetCatalogStatus("Проверяем приложения", "Обновляем доступность готовых наборов…");
        sets.Status.IsClosable = false;
        sets.Status.Content = new ProgressRing { IsActive = true, Width = 20, Height = 20 };
        try
        {
            var fresh = await Task.Run(AppPresets.Discover, windowLifetime.Token);
            if (windowClosed) return;
            int selectedCount = sets.Selected.Count;
            detectedPresets = fresh;
            BindProfileSets();
            sets.Loaded = true;
            if (sets.Selected.Count < selectedCount)
                SetCatalogStatus("Некоторые наборы изменились", "Проверьте выбор: обновлённые или удалённые наборы больше не участвуют в композиции.", InfoBarSeverity.Warning);
            else sets.Status.IsOpen = false;
        }
        catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!windowClosed)
                SetCatalogStatus("Не удалось обновить наборы", "Повторите проверку. Текущий выбор сохранён.", InfoBarSeverity.Error);
        }
        finally
        {
            sets.Busy = false;
            if (!windowClosed)
            {
                sets.Status.Content = null;
                sets.Status.IsClosable = true;
                sets.Refresh.IsEnabled = true;
                sets.Compose.IsEnabled = true;
                UpdateSetSaveButton();
            }
        }
    }

    void BindProfileSets(string? preferredCustomId = null)
    {
        sets.Entries = ProfileSetCatalog.Entries(settings, detectedPresets);
        sets.Selected.RemoveWhere(id => sets.Entries.All(entry => entry.Id != id));
        if (preferredCustomId != null)
            sets.ViewedId = sets.Entries.FirstOrDefault(entry => entry.CustomSetId == preferredCustomId)?.Id;
        if (sets.Entries.All(entry => entry.Id != sets.ViewedId))
            sets.ViewedId = sets.Entries.FirstOrDefault()?.Id;
        sets.Search.Visibility = sets.Entries.Count >= 12 || sets.Search.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildProfileSetCards();
        RefreshProfileSetResult();
    }

    void BuildProfileSetCards()
    {
        sets.CardIcons?.Cancel();
        sets.CardIcons?.Dispose();
        sets.CardIcons = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        sets.Cards.Children.Clear();
        sets.Buttons.Clear();
        string query = sets.Search.Text.Trim();
        foreach (var entry in sets.Entries.Where(entry => query.Length == 0 || entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || entry.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
        {
            var card = ProfileSetCard(entry, sets.CardIcons.Token);
            sets.Cards.Children.Add(card);
            sets.Buttons.Add(entry.Id, card);
        }
        sets.EmptyCatalogue.Text = sets.Entries.Count == 0
            ? "Поддерживаемые приложения пока не найдены. Установите нужные приложения и обновите список."
            : "По этому запросу наборов не найдено.";
        sets.EmptyCatalogue.Visibility = sets.Buttons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        sets.CardColumns = 0;
        ArrangeProfileSetCards();
        UpdateSetCardSelection();
    }

    ToggleButton ProfileSetCard(ProfileSetEntry entry, CancellationToken token)
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var preset = !entry.IsCustom && entry.PresetIds.Count == 1 ? detectedPresets.FirstOrDefault(p => p.Definition.Id == entry.PresetIds[0]) : null;
        grid.Children.Add(ProfileSetIcon(entry.Icon, preset, token));
        var title = new TextBlock { Text = entry.Name, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var mark = new FontIcon { Glyph = sets.Composing ? "\uE73E" : "\uE76C", FontSize = 12, Opacity = sets.Composing ? 0 : .6 };
        Grid.SetColumn(mark, 2);
        grid.Children.Add(mark);
        string detail = (entry.IsCustom ? "Мой набор · " : "") + $"Приложений: {entry.PresetIds.Count}"
            + (entry.MissingCount > 0 ? $" · Не найдено: {entry.MissingCount}" : "");
        var subtitle = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 };
        Grid.SetRow(subtitle, 1);
        Grid.SetColumn(subtitle, 1);
        Grid.SetColumnSpan(subtitle, 2);
        grid.Children.Add(subtitle);
        var button = new ToggleButton { Content = grid, CornerRadius = new(10), Padding = new(12), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Tag = mark };
        AutomationProperties.SetName(button, entry.Name + ". " + detail);
        button.Click += (_, _) =>
        {
            if (sets.Composing)
            {
                if (button.IsChecked == true) sets.Selected.Add(entry.Id);
                else sets.Selected.Remove(entry.Id);
            }
            else sets.ViewedId = entry.Id;
            UpdateSetCardSelection();
            RefreshProfileSetResult();
        };
        return button;
    }

    FrameworkElement ProfileSetIcon(string iconKey, DetectedPreset? preset, CancellationToken token)
    {
        if (preset == null)
            return ProfileGlyph(new VpnProfile { Icon = iconKey }, track: false, adaptToTheme: true);
        var placeholder = new FontIcon { Glyph = Icons.FirstOrDefault(icon => icon.Key == iconKey).Glyph ?? "\uE71D", FontSize = 24, Opacity = .6 };
        var image = new Image { Width = 32, Height = 32, Tag = placeholder };
        var grid = new Grid { Width = 34, Height = 34 };
        grid.Children.Add(placeholder);
        grid.Children.Add(image);
        var app = preset.ToApp();
        _ = LoadCatalogIcons([new(app.Id, image)], [app], token);
        return grid;
    }

    void SetComposition(bool enabled)
    {
        if (sets.Binding) return;
        sets.Composing = enabled;
        sets.Selected.Clear();
        sets.BaseId = null;
        BuildProfileSetCards();
        RefreshProfileSetResult();
    }

    void UpdateSetCardSelection()
    {
        foreach (var (id, button) in sets.Buttons)
        {
            button.IsChecked = sets.Composing ? sets.Selected.Contains(id) : id == sets.ViewedId;
            if (button.Tag is FontIcon mark && sets.Composing)
                mark.Opacity = button.IsChecked == true ? 1 : 0;
        }
        sets.Selection.Text = sets.Composing ? $"Выбрано наборов: {sets.Selected.Count}" + (sets.Selected.Count < 2 ? " · нужно не меньше двух" : "") : "Выберите набор, чтобы посмотреть состав";
    }

    void RefreshProfileSetResult()
    {
        sets.Composer.Visibility = sets.SaveForm.Visibility = sets.Composing ? Visibility.Visible : Visibility.Collapsed;
        var entry = sets.Entries.FirstOrDefault(item => item.Id == sets.ViewedId);
        sets.Title.Text = sets.Composing ? "Новый набор" : entry?.Name ?? "Состав набора";
        sets.Description.Text = sets.Composing ? "Отметьте несколько наборов. Результат обновляется сразу." : entry?.Description ?? "Выберите готовый набор.";
        sets.CustomActions.Visibility = !sets.Composing && entry?.IsCustom == true ? Visibility.Visible : Visibility.Collapsed;
        string instruction = "";
        if (sets.Composing)
        {
            var operation = (sets.Operation.SelectedItem as ComboBoxItem)?.Tag is ProfileSetOperation selected ? selected : ProfileSetOperation.Union;
            sets.OperationHint.Text = operation switch
            {
                ProfileSetOperation.Intersection => "Только приложения, которые есть в каждом наборе.",
                ProfileSetOperation.Difference => "Приложения исходного набора без приложений остальных.",
                ProfileSetOperation.SymmetricDifference => sets.Selected.Count == 2 ? "Приложения только из одного набора, без общих." : "Приложения, входящие в 1, 3, 5… выбранных наборов.",
                _ => "Все приложения из выбранных наборов."
            };
            sets.Binding = true;
            try
            {
                sets.Base.Items.Clear();
                foreach (var operand in sets.Entries.Where(item => sets.Selected.Contains(item.Id)))
                    sets.Base.Items.Add(new ComboBoxItem { Content = operand.Name, Tag = operand.Id });
                sets.Base.SelectedItem = sets.Base.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == sets.BaseId);
                if (sets.Base.SelectedItem == null) sets.BaseId = null;
                sets.Base.Visibility = operation == ProfileSetOperation.Difference ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { sets.Binding = false; }
            if (sets.Selected.Count < 2) instruction = "Выберите не меньше двух наборов для композиции.";
            else if (operation == ProfileSetOperation.Difference && sets.BaseId == null) instruction = "Выберите исходный набор: из него будут вычтены приложения остальных.";
            try { sets.Result = instruction.Length > 0 ? [] : ProfileSetCatalog.Evaluate(sets.Entries, sets.Selected, operation, sets.BaseId); }
            catch (InvalidDataException exception) { sets.Result = []; instruction = exception.Message; }
        }
        else sets.Result = entry?.PresetIds ?? [];
        ShowProfileSetMembers(sets.Result, instruction);
        UpdateSetSaveButton();
    }

    void ShowProfileSetMembers(IEnumerable<string> ids, string instruction)
    {
        sets.MemberIcons?.Cancel();
        sets.MemberIcons?.Dispose();
        sets.MemberIcons = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        var members = ProfileSetCatalog.Members(ids, detectedPresets);
        sets.Count.Text = $"Приложений: {members.Count}";
        sets.EmptyResult.Text = instruction.Length > 0 ? instruction : sets.Composing
            ? "Результат пуст. Попробуйте другую операцию или измените выбранные наборы."
            : "В наборе пока нет приложений.";
        sets.EmptyResult.Visibility = members.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        sets.MemberScroll.Visibility = members.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        sets.Members.Children.Clear();
        foreach (var member in members)
        {
            var row = new Grid { ColumnSpacing = 10, Margin = new(0, 2, 4, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(ProfileSetIcon(member.Icon, detectedPresets.FirstOrDefault(preset => preset.Definition.Id == member.Id && member.Available), sets.MemberIcons.Token));
            var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = member.Name == member.Id ? "Неизвестное приложение · " + member.Id : member.Name, TextWrapping = TextWrapping.Wrap });
            if (!member.Available)
                text.Children.Add(new TextBlock { Text = "Не найдено на этом компьютере", FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            sets.Members.Children.Add(row);
        }
    }

    void UpdateSetSaveButton() => sets.Save.IsEnabled = sets.Composing && !sets.Busy && sets.Result.Count > 0 && !string.IsNullOrWhiteSpace(sets.Name.Text);

    void SaveComposedSet()
    {
        if (!sets.Save.IsEnabled) return;
        int originalCount = settings.ApplicationSets.Count;
        try
        {
            var saved = ProfileSetCatalog.Create(settings, sets.Name.Text, sets.Result);
            try { Store.Save(settings); }
            catch { settings.ApplicationSets.RemoveRange(originalCount, settings.ApplicationSets.Count - originalCount); throw; }
            sets.Composing = false;
            sets.Selected.Clear();
            sets.Binding = true;
            sets.Compose.IsChecked = false;
            sets.Binding = false;
            sets.Name.Text = "";
            sets.Search.Text = "";
            BindProfileSets(saved.Id);
            SetCatalogStatus("Набор сохранён", $"«{saved.Name}» можно использовать при создании профилей.", InfoBarSeverity.Success);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!windowClosed && sets.ViewedId is { } id && sets.Buttons.TryGetValue(id, out var button))
                {
                    button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                    button.Focus(FocusState.Programmatic);
                }
            });
        }
        catch (Exception exception) { SetCatalogError(exception); }
    }

    async Task RenameProfileSet()
    {
        var entry = sets.Entries.FirstOrDefault(item => item.Id == sets.ViewedId && item.IsCustom);
        var saved = settings.ApplicationSets.FirstOrDefault(item => item.Id == entry?.CustomSetId);
        if (saved == null) return;
        var name = new TextBox { Header = "Название набора", Text = saved.Name, MaxLength = ProfileSetCatalog.MaximumNameLength, MinWidth = 300 };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(name);
        form.Children.Add(error);
        bool changed = false;
        await Dialog("Переименовать набор", form, "Сохранить", configure: dialog =>
        {
            name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
            dialog.PrimaryButtonClick += (_, args) =>
            {
                string previous = saved.Name;
                try
                {
                    ProfileSetCatalog.Rename(settings, saved.Id, name.Text);
                    Store.Save(settings);
                    changed = true;
                }
                catch (Exception exception)
                {
                    saved.Name = previous;
                    args.Cancel = true;
                    error.Message = ProfileSetError(exception);
                    error.IsOpen = true;
                }
            };
        });
        if (!windowClosed && changed)
        {
            BindProfileSets(saved.Id);
            SetCatalogStatus("Набор переименован", $"Новое название: «{saved.Name}».", InfoBarSeverity.Success);
        }
    }

    async Task DeleteProfileSet()
    {
        var entry = sets.Entries.FirstOrDefault(item => item.Id == sets.ViewedId && item.IsCustom);
        if (entry == null) return;
        if (await Dialog("Удалить набор?", new TextBlock { Text = $"«{entry.Name}» будет удалён из библиотеки. Уже созданные профили сохранят свой состав.", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, "Удалить") != ContentDialogResult.Primary || windowClosed) return;
        int index = settings.ApplicationSets.FindIndex(item => item.Id == entry.CustomSetId);
        if (index < 0) return;
        var saved = settings.ApplicationSets[index];
        try
        {
            if (!ProfileSetCatalog.Delete(settings, saved.Id)) return;
            try { Store.Save(settings); }
            catch { settings.ApplicationSets.Insert(index, saved); throw; }
            BindProfileSets();
            SetCatalogStatus("Набор удалён", $"«{saved.Name}» удалён из библиотеки.");
        }
        catch (Exception exception) { SetCatalogError(exception); }
    }

    static string ProfileSetError(Exception exception) => exception is InvalidDataException ? exception.Message : "Не удалось сохранить настройки. Проверьте доступ к папке данных и повторите попытку.";
    async Task RunProfileSetAction(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { if (!windowClosed) SetCatalogError(exception); }
    }
    void SetCatalogError(Exception exception) => SetCatalogStatus("Не удалось сохранить набор", ProfileSetError(exception), InfoBarSeverity.Error);
    void SetCatalogStatus(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        sets.Status.Title = title;
        sets.Status.Message = message;
        sets.Status.Severity = severity;
        sets.Status.IsOpen = true;
    }

    void ArrangeProfileSets()
    {
        int columns = sets.Layout.ActualWidth >= 740 ? 2 : 1;
        if (sets.Columns != columns)
        {
            sets.Columns = columns;
            sets.Layout.ColumnDefinitions.Clear();
            sets.Layout.RowDefinitions.Clear();
            sets.Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (columns == 2) sets.Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sets.Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (columns == 1) sets.Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(sets.Detail, columns == 2 ? 1 : 0);
            Grid.SetRow(sets.Detail, columns == 2 ? 0 : 1);
        }
        sets.Commands.Orientation = sets.Layout.ActualWidth >= 610 ? Orientation.Horizontal : Orientation.Vertical;
        sets.CardScroll.MaxHeight = columns == 2 ? Math.Clamp(root.ActualHeight - 330, 240, 580) : Math.Clamp(root.ActualHeight * .36, 180, 320);
        sets.MemberScroll.MaxHeight = columns == 2 ? Math.Clamp(root.ActualHeight - 540, 160, 340) : 220;
    }

    void ArrangeProfileSetCards()
    {
        int columns = sets.Cards.ActualWidth >= 520 ? 2 : 1;
        if (sets.CardColumns == columns) return;
        sets.CardColumns = columns;
        sets.Cards.ColumnDefinitions.Clear();
        sets.Cards.RowDefinitions.Clear();
        for (int index = 0; index < columns; index++)
            sets.Cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int index = 0; index < sets.Cards.Children.Count; index++)
        {
            if (index % columns == 0) sets.Cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn((FrameworkElement)sets.Cards.Children[index], index % columns);
            Grid.SetRow((FrameworkElement)sets.Cards.Children[index], index / columns);
        }
    }

    void DisposeProfileSets()
    {
        sets.CardIcons?.Cancel();
        sets.CardIcons?.Dispose();
        sets.MemberIcons?.Cancel();
        sets.MemberIcons?.Dispose();
    }
}
