using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using System.ComponentModel;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    bool switchingCategory;
    BaseDomainSelection? baseDraft;
    string baseDraftSavedIdentity = "";
    static bool PrimaryDomainCategory(string id) => id is "ru-blocked" or "ru-blocked-all";
    void BindBaseCategories()
    {
        var saved = BaseDomainSelection.From(settings);
        if (baseDraft == null || baseDraft.Identity == baseDraftSavedIdentity) baseDraft = saved;
        baseDraftSavedIdentity = saved.Identity;
        var combo = C<ComboBox>("DomainCategoryCombo");
        bool previous = binding;
        binding = true;
        try
        {
            combo.ItemsSource = new[] { DomainCategories.Describe("ru-blocked"), DomainCategories.Describe("ru-blocked-all"),
                new DomainCategory("none", "Только выбранные категории", "Без основного доменного списка. IP-база продолжает действовать, пока включены общие правила.") };
            combo.DisplayMemberPath = nameof(DomainCategory.Name);
            combo.SelectedItem = ((IEnumerable<DomainCategory>)combo.ItemsSource).First(c => c.Id == baseDraft.BaseCategory);
        }
        finally { binding = previous; }
        bool dirty = baseDraft.Identity != saved.Identity, busy = updating || switchingCategory;
        C<TextBlock>("DomainCategoryHint").Text = ((DomainCategory)combo.SelectedItem).Description +
            (dirty ? "\nНовый выбор ещё не применён." : "");
        C<Button>("ApplyDomainCategories").IsEnabled = dirty && !busy;
        C<Button>("ResetDomainCategories").Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        C<Button>("ResetDomainCategories").IsEnabled = !busy;
        C<Button>("AddDomainCategories").IsEnabled = !busy;
        C<TextBlock>("DomainSelectionSummary").Text = baseDraft.Categories.Count == 0 ? "Доменные списки не выбраны. Из общей базы действуют только IP-адреса и подсети." :
            $"Доменных списков: {baseDraft.Categories.Count}. Совпадающие домены объединяются без дубликатов.";
        C<InfoBar>("DomainSelectionPending").IsOpen = dirty;
        var caution = C<InfoBar>("DomainSelectionCaution");
        bool privateDomains = baseDraft.AdditionalCategories.Contains("private"), ads = baseDraft.AdditionalCategories.Contains("category-ads-all");
        caution.IsOpen = privateDomains || ads;
        caution.Severity = privateDomains ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        caution.Message = string.Join(" ", new[] { privateDomains ? "Категория «Частные домены» может направить локальные адреса через VPN." : "",
            ads ? "Рекламные домены будут направляться через VPN. Это не блокировщик рекламы." : "" }.Where(s => s.Length > 0));
        var chips = C<Grid>("DomainCategoryChips");
        chips.Children.Clear(); chips.RowDefinitions.Clear(); chips.ColumnDefinitions.Clear();
        int columns = chips.ActualWidth >= 560 ? 2 : 1;
        for (int column = 0; column < columns; column++) chips.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        for (int index = 0; index < baseDraft.AdditionalCategories.Count; index++)
        {
            if (index % columns == 0) chips.RowDefinitions.Add(new() { Height = GridLength.Auto });
            string id = baseDraft.AdditionalCategories[index];
            var content = new Grid { ColumnSpacing = 10 };
            content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.Children.Add(new TextBlock { Text = DomainCategories.Describe(id).Name, TextTrimming = TextTrimming.CharacterEllipsis });
            var close = new FontIcon { Glyph = "\uE711", FontSize = 12 };
            Grid.SetColumn(close, 1); content.Children.Add(close);
            var chip = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, CornerRadius = new(8), IsEnabled = !busy };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, "Убрать категорию «" + DomainCategories.Describe(id).Name + "»");
            ToolTipService.SetToolTip(chip, DomainCategories.Describe(id).Description + "\nНажмите, чтобы убрать из выбора.");
            chip.Click += (_, _) => { baseDraft = BaseDomainSelection.Create(baseDraft!.BaseCategory, baseDraft.AdditionalCategories.Where(c => c != id)); BindBaseCategories(); };
            Grid.SetRow(chip, index / columns); Grid.SetColumn(chip, index % columns); chips.Children.Add(chip);
        }
    }
    void HookBaseCategories()
    {
        Hook("DomainRepositoryLink", () => { Process.Start(new ProcessStartInfo(DomainRepository.Repository) { UseShellExecute = true }); return Task.CompletedTask; });
        C<ComboBox>("DomainCategoryCombo").SelectionChanged += (_, _) =>
        {
            if (binding || switchingCategory || updating || C<ComboBox>("DomainCategoryCombo").SelectedItem is not DomainCategory chosen) return;
            baseDraft = BaseDomainSelection.Create(chosen.Id, (baseDraft?.AdditionalCategories ?? []).Where(id => id != chosen.Id));
            BindBaseCategories();
        };
        Hook("AddDomainCategories", ShowAdditionalCategories);
        Hook("ApplyDomainCategories", () => RunBaseOperation(baseDraft ?? BaseDomainSelection.From(settings), true));
        Hook("ResetDomainCategories", () => { baseDraft = BaseDomainSelection.From(settings); BindBaseCategories(); return Task.CompletedTask; });
        C<Grid>("DomainCategoryChips").SizeChanged += (_, _) =>
        {
            var chips = C<Grid>("DomainCategoryChips");
            if (chips.ColumnDefinitions.Count != (chips.ActualWidth >= 560 ? 2 : 1)) BindBaseCategories();
        };
    }

    sealed class CategoryChoice(DomainCategory category, bool selected, Func<CategoryChoice, bool, bool> change) : INotifyPropertyChanged
    {
        public string Id => category.Id;
        public string Name => category.Name;
        public DomainCategoryGroup Group => category.Group;
        public string SelectionHint => category.Id is "private" or "category-ads-all" or "win-spy" or "ru-available-only-inside" ? category.Description : "";
        public Visibility HintVisibility => SelectionHint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        public bool Selected
        {
            get => selected;
            set
            {
                if (selected == value) return;
                if (change(this, value)) selected = value;
                PropertyChanged?.Invoke(this, new(nameof(Selected)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    async Task ShowAdditionalCategories()
    {
        if (dialogOpen || updating || switchingCategory || windowClosed) return;
        var catalog = DomainCategories.Read();
        if (!catalog.Any(c => !PrimaryDomainCategory(c.Id) && c.Id != "none"))
        {
            await RunBaseOperation(null, true, catalogOnly: true);
            if (windowClosed) return;
            catalog = DomainCategories.Read();
        }
        var draft = baseDraft ?? BaseDomainSelection.From(settings);
        var picker = CreateCategoryPicker(catalog, draft);
        if (await Dialog("Дополнительные категории", picker.Panel, "Сохранить выбор", configure: picker.BindDialog) == ContentDialogResult.Primary && !windowClosed)
        {
            baseDraft = BaseDomainSelection.Create(draft.BaseCategory, picker.Selected);
            BindBaseCategories();
            RevealBaseSelection();
        }
    }

    sealed record CategoryFilter(string Name, DomainCategoryGroup? Group = null, bool SelectedOnly = false);
    static readonly CategoryFilter[] CategoryFilters = [new("Все категории"), new("Выбранные", SelectedOnly: true),
        new("Сайты и другие списки", DomainCategoryGroup.SitesAndOther), new("Тематические наборы", DomainCategoryGroup.Themes),
        new("Страны и регионы", DomainCategoryGroup.Regions), new("Технические", DomainCategoryGroup.Technical), new("Списки блокировок", DomainCategoryGroup.Blocklists)];
    sealed record CategoryPickerView(StackPanel Panel, TextBox Search, ComboBox Group, ListView List, CategoryChoice[] Choices,
        HashSet<string> Selected, Action<ContentDialog> BindDialog);

    CategoryPickerView CreateCategoryPicker(IReadOnlyList<DomainCategory> catalog, BaseDomainSelection draft)
    {
        var selected = draft.AdditionalCategories.ToHashSet(StringComparer.Ordinal);
        var panel = new StackPanel { Spacing = 12, Width = Math.Clamp(root.ActualWidth - 180, 320, 480) };
        var search = new TextBox { PlaceholderText = "Название или категория" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Найти дополнительную доменную категорию");
        var group = new ComboBox { ItemsSource = CategoryFilters, DisplayMemberPath = nameof(CategoryFilter.Name), SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 300 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(group, "Группа доменных категорий");
        var count = new TextBlock { FontSize = 12, Opacity = .75 };
        var warning = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Warning };
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = Math.Clamp(root.ActualHeight - 454, 160, 380) };
        list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
             <CheckBox IsChecked="{Binding Selected, Mode=TwoWay}" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch" VerticalContentAlignment="Center" MinHeight="48" AutomationProperties.Name="{Binding Name}" AutomationProperties.HelpText="{Binding SelectionHint}" Padding="0">
              <StackPanel Spacing="4" Margin="12,8,8,8"><TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextWrapping="Wrap"/><TextBlock Text="{Binding SelectionHint}" Visibility="{Binding HintVisibility}" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextWrapping="Wrap"/></StackPanel>
             </CheckBox>
            </DataTemplate>
            """);
        ContentDialog? modal = null;
        bool pickerClosed = false;
        CategoryChoice[] choices = [];
        var empty = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .75 };
        void Count()
        {
            count.Text = $"Выбрано: {selected.Count} из {BaseDomainSelection.MaxAdditionalCategories}. Изменения применяются на странице базы.";
            if (modal != null) modal.PrimaryButtonText = selected.Count == 0 ? "Сохранить выбор" : $"Выбрать · {selected.Count}";
        }
        bool Change(CategoryChoice choice, bool value)
        {
            if (value && selected.Count >= BaseDomainSelection.MaxAdditionalCategories)
            {
                warning.Message = $"Можно выбрать не больше {BaseDomainSelection.MaxAdditionalCategories} дополнительных категорий.";
                warning.IsOpen = true;
                return false;
            }
            if (value) selected.Add(choice.Id); else selected.Remove(choice.Id);
            warning.IsOpen = false;
            Count();
            // Defer filtering until the two-way binding setter has committed the new selection.
            if (group.SelectedItem is CategoryFilter { SelectedOnly: true })
                DispatcherQueue.TryEnqueue(() => { if (!windowClosed && !pickerClosed) Filter(); });
            return true;
        }
        choices = catalog.Where(c => !PrimaryDomainCategory(c.Id) && c.Id != "none" && c.Id != draft.BaseCategory).Concat(selected.Select(DomainCategories.Describe))
            .DistinctBy(c => c.Id).OrderByDescending(c => selected.Contains(c.Id)).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new CategoryChoice(c, selected.Contains(c.Id), Change)).ToArray();
        void Filter()
        {
            string query = search.Text.Trim();
            var active = (CategoryFilter?)group.SelectedItem ?? CategoryFilters[0];
            var visible = choices.Where(c => (!active.SelectedOnly || selected.Contains(c.Id)) &&
                (active.Group == null || c.Group == active.Group) &&
                (query.Length == 0 || (c.Name + " " + c.Id).Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
            list.ItemsSource = visible;
            empty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Text = choices.Length == 0 ? "Каталог пока недоступен. Обновите общую базу и попробуйте снова." :
                active.SelectedOnly && selected.Count == 0 ? "Пока ничего не выбрано." : "В этой группе совпадений не найдено.";
        }
        search.TextChanged += (_, _) => Filter();
        group.SelectionChanged += (_, _) => Filter();
        panel.Children.Add(new TextBlock { Text = "Списки RunetFreedom дополняют основной: выбранные домены направляются через VPN для всех профилей.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(group); panel.Children.Add(search); panel.Children.Add(count); panel.Children.Add(warning); panel.Children.Add(list); panel.Children.Add(empty);
        Count(); Filter();
        return new(panel, search, group, list, choices, selected, dialog => { modal = dialog; dialog.Closed += (_, _) => pickerClosed = true; Count(); });
    }

    void RevealBaseSelection()
    {
        // A one-shot user action, after the dialog has closed and the new chips have been measured.
        // Bottom alignment keeps Apply visible together with the selection summary above it.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (windowClosed || page != 1 || dialogOpen || !C<Button>("ApplyDomainCategories").IsEnabled) return;
            C<FrameworkElement>("BasePage").UpdateLayout();
            C<FrameworkElement>("DomainSelectionActions").StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = motion.TestEnabled ?? motion.Enabled,
                VerticalAlignmentRatio = 1
            });
        });
    }
}
