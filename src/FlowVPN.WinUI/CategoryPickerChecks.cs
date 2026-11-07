using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task CheckCategoryPicker()
    {
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Category picker: " + name);
            checks.Add(name);
        }
        foreach (string id in new[] { "ap", "dw", "ea", "ft", "hm", "hp", "lg", "qt", "st", "vk", "unknown-provider" })
            Check(DomainCategories.GroupOf(id) == DomainCategoryGroup.SitesAndOther, "No country inference for " + id);
        foreach (string id in new[] { "cn", "category-ru", "category-ir", "category-tm", "geolocation-cn", "ru-available-only-inside" })
            Check(DomainCategories.GroupOf(id) == DomainCategoryGroup.Regions, "Explicit regional category " + id);
        Check(DomainCategories.GroupOf("category-pt") == DomainCategoryGroup.Themes, "Private trackers are not inferred as Portugal");
        foreach (string id in new[] { "private", "tld-ru", "win-spy", "category-doh", "category-ddns", "category-ntp-ru", "category-stun", "category-ipfs" })
            Check(DomainCategories.GroupOf(id) == DomainCategoryGroup.Technical, "Technical category " + id);
        foreach (string id in new[] { "ru-blocked", "ru-blocked-all", "antifilter-download", "refilter" })
            Check(DomainCategories.GroupOf(id) == DomainCategoryGroup.Blocklists, "Blocklist category " + id);
        var fixture = new[] { "google", "discord", "fixture-other", "private", "category-ads-all" }.Select(DomainCategories.Describe).ToArray();
        var picker = CreateCategoryPicker(fixture, BaseDomainSelection.Create("ru-blocked"));
        Check(picker.Choices.Length == 5 && picker.Selected.Count == 0, "Synthetic fixture starts without selections");
        Check(picker.Choices.Count(c => c.HintVisibility == Visibility.Visible) == 2 &&
            picker.Choices.Where(c => c.HintVisibility == Visibility.Collapsed).All(c => c.SelectionHint.Length == 0), "Only meaningful warnings are shown");
        var chosen = picker.Choices.Single(c => c.Id == "google");
        var limited = CreateCategoryPicker(Enumerable.Range(0, 33).Select(i => DomainCategories.Describe("fixture-" + i)).ToArray(), BaseDomainSelection.Create("ru-blocked"));
        foreach (var choice in limited.Choices) choice.Selected = true;
        Check(limited.Selected.Count == 32 && limited.Choices.Count(c => c.Selected) == 32, "Selection limit remains 32 across choices");

        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "Дополнительные категории",
            Content = picker.Panel, PrimaryButtonText = "Сохранить выбор", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary
        };
        picker.BindDialog(dialog);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        async Task WaitForState(Func<bool> condition, string description)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(3))
                    throw new InvalidOperationException($"Category picker did not reach {description}: loaded={picker.Search.IsLoaded}, width={picker.Search.ActualWidth}, text='{picker.Search.Text}', visible={picker.List.Items.Count}");
                await Task.Delay(25);
                dialog.UpdateLayout();
            }
        }
        bool VisibleIds(params string[] expected) => picker.List.Items.OfType<CategoryChoice>().Select(c => c.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        async Task SetSearch(string text, params string[] expected)
        {
            picker.Search.Text = text;
            await WaitForState(() => VisibleIds(expected), "search result");
        }
        async Task SetGroup(CategoryFilter filter, params string[] expected)
        {
            picker.Group.SelectedItem = filter;
            await WaitForState(() => VisibleIds(expected), "group result");
        }
        dialog.Opened += async (_, _) =>
        {
            try
            {
                // Opened can precede the child's first layout. Exercise the real, loaded input;
                // assert filtering output rather than depending on native event dispatch order.
                await WaitForState(() => picker.Search.IsLoaded && picker.Search.ActualWidth > 0 && picker.List.ActualHeight > 0, "loaded controls");
                DependencyObject? ancestor = picker.Panel;
                while ((ancestor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ancestor)) != null)
                    if (ancestor is ScrollViewer viewport)
                    {
                        var panelBounds = VisualBounds(picker.Panel, viewport);
                        var searchBounds = VisualBounds(picker.Search, viewport);
                        Check(panelBounds.Left >= -1 && panelBounds.Right <= viewport.ViewportWidth + 1 &&
                            searchBounds.Left >= -1 && searchBounds.Right <= viewport.ViewportWidth + 1, "Panel and search fit the actual dialog content viewport");
                    }
                chosen.Selected = true;
                await SetSearch("discord", "discord");
                Check(picker.List.Items.Count == 1 && picker.Selected.Contains(chosen.Id) && chosen.Selected, "Search preserves hidden selection");
                await SetGroup(CategoryFilters.Single(f => f.Group == DomainCategoryGroup.Technical));
                Check(picker.List.Items.Count == 0, "Search stays inside the selected group");
                await SetSearch("", "private");
                Check(picker.List.Items.Count == 1 && ((CategoryChoice)picker.List.Items[0]).Id == "private", "Group filter keeps flat list semantics");
                await SetGroup(CategoryFilters.Single(f => f.SelectedOnly), "google");
                Check(picker.List.Items.Count == 1 && ReferenceEquals(picker.List.Items[0], chosen), "Selected filter uses the same choice instances");
                await SetGroup(CategoryFilters[0], "google", "discord", "fixture-other", "private", "category-ads-all");
                Check(picker.List.Items.Count == 5 && chosen.Selected, "All categories restores the complete list and selection");
                foreach (var choice in picker.Choices)
                {
                    picker.List.ScrollIntoView(choice);
                    await Task.Delay(80);
                    dialog.UpdateLayout();
                    var container = picker.List.ContainerFromItem(choice) as ListViewItem ?? throw new InvalidOperationException("Category fixture row was not realized");
                    var checkbox = VisualDescendants<CheckBox>(container).Single();
                    var labels = (StackPanel)checkbox.Content;
                    var hint = (TextBlock)labels.Children[1];
                    var bounds = VisualBounds(labels, checkbox);
                    Check(checkbox.ActualHeight >= 48 && checkbox.VerticalContentAlignment == VerticalAlignment.Center, "Native row height and vertical alignment: " + choice.Id);
                    Check(labels.Margin.Left == 12 && bounds.Left >= 31 && bounds.Right <= checkbox.ActualWidth + 1, "Checkbox text has a clear gap: " + choice.Id);
                    Check(hint.Visibility == choice.HintVisibility && hint.Text == choice.SelectionHint, "Rendered hint matches curated metadata: " + choice.Id);
                    Check(checkbox.IsChecked == choice.Selected, "Rendered selection survives filtering: " + choice.Id);
                }
                Check(picker.Panel.Children.OfType<TextBlock>().Count(t => t.Text.Contains("RunetFreedom", StringComparison.Ordinal)) == 1, "One shared repository hint");
                picker.List.ScrollIntoView(picker.Choices[0]);
                await Task.Delay(150);
                dialog.UpdateLayout();
                await CaptureElement(dialog, "domain-categories");
                File.WriteAllText(Path.Combine(Store.Root, "design-preview", "category-picker-checks.json"), JsonSerializer.Serialize(checks, Store.JsonOptions));
            }
            catch (Exception error)
            {
                failure = error;
                try { await CaptureElement(dialog, "domain-categories-failure"); }
                catch { /* Keep the original assertion if the failed visual cannot be captured. */ }
            }
            finally { finished.TrySetResult(); dialog.Hide(); }
        };
        dialogOpen = true;
        try
        {
            await dialog.ShowAsync();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (failure != null) throw new InvalidOperationException("Category picker visual check failed", failure);
        }
        finally { dialogOpen = false; }
    }
}
