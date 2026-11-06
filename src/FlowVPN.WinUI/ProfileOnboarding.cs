using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    bool profileSetupBusy;

    VpnSource? PreferredProfileSource() => (page == 2 ? Source : null)
        ?? settings.Sources.FirstOrDefault(source => source.Id == Profile?.SourceId)
        ?? settings.Sources.FirstOrDefault();

    void UpdateProfileOnboarding()
    {
        bool connectedSource = settings.Sources.Count > 0;
        C<InfoBar>("ProfileOnboardingReminder").IsOpen = connectedSource
            && settings.Profiles.Count == 0 && !settings.ProfileOnboardingCompleted;
        C<TextBlock>("EmptyProfileHint").Text = connectedSource
            ? "Подключение уже добавлено. Создайте профиль с нужными приложениями и сайтами или выберите готовые варианты для программ на этом компьютере."
            : "Сначала добавьте подключение. Затем вы сможете выбрать приложения и сайты, которые должны идти через VPN.";
        C<Button>("EmptyAddConnectionButton").Visibility = connectedSource ? Visibility.Collapsed : Visibility.Visible;
        C<Button>("EmptyNewProfileButton").Visibility = connectedSource ? Visibility.Visible : Visibility.Collapsed;
        C<Button>("EmptyProfileSuggestionsButton").Visibility = connectedSource ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task ShowProfileSuggestions(bool onboarding = false)
    {
        if (profileSetupBusy || dialogOpen || windowClosed)
            return;
        if (settings.Sources.Count == 0)
        {
            SelectPage(2);
            C<Button>("AddConnectionButton").Focus(FocusState.Programmatic);
            return;
        }
        profileSetupBusy = true;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        var feedback = C<InfoBar>("ProfileSetupFeedback");
        feedback.Title = "Проверяем приложения";
        feedback.Message = "Подбираем готовые профили для этого компьютера…";
        feedback.Severity = InfoBarSeverity.Informational;
        feedback.Content = new ProgressRing { IsActive = true, Width = 20, Height = 20 };
        feedback.IsClosable = false;
        feedback.IsOpen = true;
        try
        {
            var presets = await Task.Run(AppPresets.Discover, lifetime.Token);
            if (windowClosed || dialogOpen)
                return;
            if (settings.Sources.Count == 0)
            {
                SelectPage(2);
                return;
            }
            var suggestions = PresetProfileFactory.Suggestions(settings, presets);
            var source = new ComboBox { Header = "Подключение для выбранных профилей", HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(source, "Подключение для готовых профилей");
            foreach (var item in settings.Sources)
                source.Items.Add(new ComboBoxItem { Content = item.Name + " · " + ConnectionTypeLabel(item), Tag = item.Id });
            string? sourceId = PreferredProfileSource()?.Id;
            source.SelectedItem = source.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == sourceId);
            if (source.SelectedIndex < 0)
                source.SelectedIndex = 0;

            var cards = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
            var choices = new List<(ProfileSuggestion Suggestion, ToggleButton Button)>();
            ContentDialog? activeDialog = null;
            bool submitting = false, dialogClosing = false;
            ProfileCreationResult? creation = null;
            void UpdateSelection()
            {
                if (activeDialog == null)
                    return;
                int count = choices.Count(choice => choice.Button.IsChecked == true);
                activeDialog.PrimaryButtonText = submitting ? "Создание…" : count == 1 ? "Создать профиль" : $"Создать профили · {count}";
                activeDialog.IsPrimaryButtonEnabled = !submitting && count > 0 && source.SelectedItem != null;
            }
            foreach (var suggestion in suggestions)
            {
                var card = ProfileSuggestionCard(suggestion, presets.FirstOrDefault(preset => preset.Id == suggestion.Id), lifetime.Token);
                choices.Add((suggestion, card));
                cards.Children.Add(card);
                card.Checked += (_, _) => UpdateSelection();
                card.Unchecked += (_, _) => UpdateSelection();
            }
            source.SelectionChanged += (_, _) => UpdateSelection();
            var content = new StackPanel { Spacing = 16 };
            content.Children.Add(new TextBlock
            {
                Text = "Выберите один или несколько вариантов. Изменить их можно позже.",
                TextWrapping = TextWrapping.Wrap
            });
            var error = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error, Title = "Не удалось создать профили" };
            content.Children.Add(error);
            var search = new TextBox { PlaceholderText = "Найти готовый вариант", Visibility = suggestions.Count >= 12 ? Visibility.Visible : Visibility.Collapsed };
            AutomationProperties.SetName(search, "Найти вариант профиля");
            content.Children.Add(search);
            if (settings.Sources.Count > 1)
                content.Children.Add(source);
            else
                content.Children.Add(new TextBlock { Text = "Подключение: " + settings.Sources[0].Name, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75 });
            content.Children.Add(cards);
            var noMatches = new TextBlock { Text = "Подходящих вариантов не найдено. Попробуйте другой запрос.", TextWrapping = TextWrapping.Wrap, Opacity = .75, Visibility = Visibility.Collapsed };
            content.Children.Add(noMatches);
            if (suggestions.Count == 0)
                content.Children.Add(new TextBlock { Text = "Подходящих готовых вариантов пока нет. Создайте собственный профиль кнопкой «Новый профиль».", TextWrapping = TextWrapping.Wrap });
            var formHost = new ContentControl { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var scroll = new ScrollViewer
            {
                Content = formHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            int columns = 0;
            void ArrangeCards()
            {
                int next = content.ActualWidth >= 520 ? 2 : 1;
                if (next == columns)
                    return;
                columns = next;
                cards.ColumnDefinitions.Clear();
                cards.RowDefinitions.Clear();
                for (int index = 0; index < columns; index++)
                    cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var visible = choices.Where(choice => choice.Button.Visibility == Visibility.Visible).ToArray();
                for (int index = 0; index < visible.Length; index++)
                {
                    if (index % columns == 0)
                        cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetColumn(visible[index].Button, index % columns);
                    Grid.SetRow(visible[index].Button, index / columns);
                }
            }
            search.TextChanged += (_, _) =>
            {
                string query = search.Text.Trim();
                foreach (var choice in choices)
                    choice.Button.Visibility = query.Length == 0 || choice.Suggestion.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || choice.Suggestion.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
                noMatches.Visibility = choices.Count > 0 && choices.All(choice => choice.Button.Visibility == Visibility.Collapsed) ? Visibility.Visible : Visibility.Collapsed;
                columns = 0;
                ArrangeCards();
            };
            void ResizeDialog(object? sender, SizeChangedEventArgs args)
            {
                scroll.Width = Math.Clamp(root.ActualWidth - 180, 300, 640);
                scroll.MaxHeight = Math.Clamp(root.ActualHeight - 240, 220, 580);
            }
            content.SizeChanged += (_, _) => ArrangeCards();
            root.SizeChanged += ResizeDialog;
            scroll.Width = Math.Clamp(root.ActualWidth - 180, 300, 640);
            scroll.MaxHeight = Math.Clamp(root.ActualHeight - 240, 220, 580);
            ArrangeCards();
            ContentDialogResult response;
            try
            {
                feedback.IsOpen = false;
                feedback.Content = null;
                response = await Dialog(onboarding ? "С чего начнём?" : "Готовые профили", scroll, "Создать профили · 0", configure: dialog =>
                {
                    activeDialog = dialog;
                    dialog.CloseButtonText = "Позже";
                    dialog.DefaultButton = ContentDialogButton.Close;
                    dialog.Resources["ContentDialogMaxWidth"] = 720d;
                    UpdateSelection();
                    dialog.CloseButtonClick += (_, _) => { dialogClosing = true; lifetime.Cancel(); };
                    dialog.Closing += (_, args) =>
                    {
                        if (args.Result != ContentDialogResult.Primary)
                        {
                            dialogClosing = true;
                            lifetime.Cancel();
                        }
                    };
                    dialog.Closed += (_, _) => { dialogClosing = true; lifetime.Cancel(); };
                    dialog.PrimaryButtonClick += async (_, args) =>
                    {
                        args.Cancel = true;
                        if (submitting || !dialog.IsPrimaryButtonEnabled)
                            return;
                        var deferral = args.GetDeferral();
                        submitting = true;
                        formHost.IsEnabled = false;
                        error.IsOpen = false;
                        UpdateSelection();
                        try
                        {
                            var selected = choices.Where(choice => choice.Button.IsChecked == true).Select(choice => choice.Suggestion.Id).ToArray();
                            string chosenSource = (source.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                            var freshPresets = await Task.Run(AppPresets.Discover, lifetime.Token);
                            lifetime.Token.ThrowIfCancellationRequested();
                            if (windowClosed)
                                return;
                            int originalCount = settings.Profiles.Count;
                            string originalSelection = settings.SelectedProfileId;
                            bool originalCompleted = settings.ProfileOnboardingCompleted;
                            try
                            {
                                creation = PresetProfileFactory.CreateSelected(settings, chosenSource, selected, freshPresets);
                                if (creation.SourceMissing)
                                    throw new InvalidOperationException("Подключение больше недоступно. Выберите другое подключение.");
                                settings.ProfileOnboardingCompleted = true;
                                Store.Save(settings);
                            }
                            catch
                            {
                                settings.Profiles.RemoveRange(originalCount, settings.Profiles.Count - originalCount);
                                settings.SelectedProfileId = originalSelection;
                                settings.ProfileOnboardingCompleted = originalCompleted;
                                creation = null;
                                throw;
                            }
                            detectedPresets = freshPresets;
                            args.Cancel = false;
                        }
                        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                        catch (Exception exception)
                        {
                            if (!windowClosed && !dialogClosing)
                            {
                                error.Message = exception is IOException or UnauthorizedAccessException
                                    ? "Не удалось сохранить настройки. Проверьте доступ к папке данных и повторите попытку. Выбор сохранён."
                                    : "Не удалось завершить проверку. Повторите попытку; выбранные карточки останутся отмеченными.";
                                error.IsOpen = true;
                                scroll.ChangeView(null, 0, null, true);
                            }
                        }
                        finally
                        {
                            submitting = false;
                            try
                            {
                                if (!windowClosed && !dialogClosing)
                                {
                                    formHost.IsEnabled = true;
                                    UpdateSelection();
                                }
                            }
                            finally { deferral.Complete(); }
                        }
                    };
                });
            }
            finally { root.SizeChanged -= ResizeDialog; }
            if (windowClosed)
                return;
            if (response == ContentDialogResult.Primary && creation is { } result)
            {
                BindAll();
                feedback.Title = result.Added > 0 ? "Профили созданы" : "Новые профили не добавлены";
                feedback.Message = result.SourceMissing ? "Подключение больше недоступно. Добавьте его и повторите выбор."
                    : $"Создано: {result.Added}. Уже существуют: {result.Duplicates}."
                        + (result.Unavailable > 0 ? $" Больше не доступны на этом устройстве: {result.Unavailable}." : "")
                        + (result.Added > 0 ? " Выберите профиль слева, чтобы настроить или подключить его." : "");
                feedback.Severity = result.SourceMissing || result.Unavailable > 0 ? InfoBarSeverity.Warning : result.Added > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
                feedback.IsOpen = true;
                if (!onboarding && result.Added > 0)
                    SelectPage(0);
            }
            else
            {
                bool wasCompleted = settings.ProfileOnboardingCompleted;
                settings.ProfileOnboardingCompleted = true;
                try { Store.Save(settings); }
                catch { settings.ProfileOnboardingCompleted = wasCompleted; throw; }
                UpdateProfileOnboarding();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!windowClosed)
            {
                feedback.Title = "Не удалось подготовить профили";
                feedback.Message = "Повторите попытку кнопкой «Готовые профили…».";
                feedback.Severity = InfoBarSeverity.Error;
                feedback.IsOpen = true;
            }
        }
        finally
        {
            profileSetupBusy = false;
            lifetime.Cancel();
            if (!windowClosed)
            {
                feedback.Content = null;
                feedback.IsClosable = true;
                if (feedback.Title == "Проверяем приложения")
                    feedback.IsOpen = false;
            }
        }
    }

    ToggleButton ProfileSuggestionCard(ProfileSuggestion suggestion, DetectedPreset? preset, CancellationToken token)
    {
        var grid = new Grid { RowSpacing = 10, ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int index = 0; index < 3; index++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (preset != null)
        {
            var placeholder = new FontIcon { Glyph = Icons.FirstOrDefault(icon => icon.Key == suggestion.Icon).Glyph ?? "\uE71D", FontSize = 24, Opacity = .6 };
            var image = new Image { Width = 32, Height = 32, Tag = placeholder };
            var icon = new Grid { Width = 34, Height = 34 };
            icon.Children.Add(placeholder);
            icon.Children.Add(image);
            grid.Children.Add(icon);
            var app = preset.ToApp();
            _ = LoadCatalogIcons([new(app.Id, image)], [app], token);
        }
        else
            grid.Children.Add(ProfileGlyph(new VpnProfile { Icon = suggestion.Icon, Apps = suggestion.Apps.ToList() }, track: false, adaptToTheme: true));
        var title = new TextBlock { Text = suggestion.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var selected = new FontIcon { Glyph = "\uE73E", FontSize = 14, Width = 18, Opacity = 0 };
        Grid.SetColumn(selected, 2);
        grid.Children.Add(selected);
        var description = new TextBlock { Text = suggestion.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .85 };
        Grid.SetRow(description, 1);
        Grid.SetColumnSpan(description, 3);
        grid.Children.Add(description);
        string count = suggestion.AppCount == 0 ? "Без дополнительных приложений" : $"Приложений: {suggestion.AppCount}";
        var detail = new TextBlock { Text = count, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 };
        Grid.SetRow(detail, 2);
        Grid.SetColumnSpan(detail, 3);
        grid.Children.Add(detail);
        var button = new ToggleButton
        {
            Content = grid, IsChecked = false, CornerRadius = new CornerRadius(12), Padding = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(button, suggestion.Name + ". " + suggestion.Description + ". " + count);
        button.Checked += (_, _) => selected.Opacity = 1;
        button.Unchecked += (_, _) => selected.Opacity = 0;
        return button;
    }
}
