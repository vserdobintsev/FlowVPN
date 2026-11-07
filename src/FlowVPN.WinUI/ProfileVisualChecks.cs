using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task CheckEmptyApplicationsLayout(double scale, string folder)
    {
        var profile = Profile ?? throw new InvalidOperationException("Application layout check requires a profile fixture");
        var originalApps = profile.Apps;
        int previousPage = page;
        var reports = new List<object>();
        try
        {
            foreach (int height in new[] { 620, 720 })
            {
                AppWindow.Resize(new((int)(800 * scale), (int)(height * scale)));
                profile.Apps = [];
                BindProfile();
                SelectPage(0);
                await Task.Delay(250);
                root.UpdateLayout(); Responsive(); root.UpdateLayout();
                var content = C<Grid>("AppsContent");
                var scroll = C<ScrollViewer>("MainScroll");
                var list = C<ListView>("AppList");
                var empty = C<TextBlock>("EmptyApps");
                var textBounds = VisualBounds(empty, content);
                if (list.Visibility != Visibility.Collapsed || list.Items.Count != 0 || empty.Visibility != Visibility.Visible ||
                    empty.ActualHeight + empty.Margin.Top + empty.Margin.Bottom < empty.DesiredSize.Height - .5 || empty.ActualHeight < empty.FontSize ||
                    content.RowDefinitions[4].ActualHeight < empty.ActualHeight + empty.Margin.Top - .5 ||
                    textBounds.Top < VisualBounds(C<Button>("AddApp"), content).Bottom + 9 || textBounds.Bottom > content.ActualHeight + .5)
                    throw new InvalidOperationException("Empty applications are clipped or still reserve a list: " + height);
                await CaptureElement(root, "applications-empty-initial-" + height);
                var commands = (FrameworkElement)C<Button>("AddApp").Parent;
                var commandBounds = VisualBounds(commands, scroll);
                if (commandBounds.Top < 0 || commandBounds.Bottom > scroll.ViewportHeight)
                    commands.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                await Task.Delay(100);
                root.UpdateLayout();
                commandBounds = VisualBounds(commands, scroll);
                if (commandBounds.Top < -1 || commandBounds.Bottom > scroll.ViewportHeight + 1)
                    throw new InvalidOperationException("Application command row is clipped in the outer viewport: " + height);
                empty.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                await Task.Delay(100);
                root.UpdateLayout();
                var visibleText = VisualBounds(empty, scroll);
                if (visibleText.Top < -1 || visibleText.Bottom > scroll.ViewportHeight + 1 || scroll.ExtentWidth > scroll.ViewportWidth + 1)
                    throw new InvalidOperationException("Empty applications cannot be reached by scrolling: " + height);
                await CaptureElement(root, "applications-empty-" + height);
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                await Task.Delay(100);
                await CaptureElement(root, "applications-empty-bottom-" + height);
                profile.Apps = [new AppRule { Name = "Проверочное приложение", Pattern = "^visual-layout-test$", GroupId = "visual-layout-test" }];
                BindProfile();
                await Task.Delay(100);
                root.UpdateLayout();
                if (list.Visibility != Visibility.Visible || list.Items.Count != 1 || list.ActualHeight <= 0 || empty.Visibility != Visibility.Collapsed ||
                    content.RowDefinitions[4].ActualHeight < list.ActualHeight - .5)
                    throw new InvalidOperationException("Populated applications did not recover after the empty state: " + height);
                reports.Add(new { Width = 800, Height = height, EmptyText = textBounds, VisibleText = visibleText, PopulatedListHeight = list.ActualHeight });
                scroll.ChangeView(null, 0, null, true);
            }
            File.WriteAllText(Path.Combine(folder, "empty-applications-layout-checks.json"), JsonSerializer.Serialize(reports, Store.JsonOptions));
        }
        finally
        {
            profile.Apps = originalApps;
            BindProfile();
            C<ScrollViewer>("MainScroll").ChangeView(null, 0, null, true);
            AppWindow.Resize(new((int)(1180 * scale), (int)(840 * scale)));
            SelectPage(previousPage);
            await Task.Delay(150);
        }
    }

    async Task CheckProfileSettingsLayout(double scale, string folder)
    {
        int previousPage = page;
        var previousTheme = root.RequestedTheme;
        var reports = new List<object>();
        try
        {
            foreach (var size in new[] { (Width: 1180, Height: 840), (Width: 1600, Height: 900), (Width: 800, Height: 620) })
            {
                AppWindow.Resize(new((int)(size.Width * scale), (int)(size.Height * scale)));
                root.RequestedTheme = size.Width == 800 ? ElementTheme.Dark : ElementTheme.Light;
                SelectPage(4);
                await Task.Delay(300);
                root.UpdateLayout(); Responsive(); root.UpdateLayout();
                var scroll = C<ScrollViewer>("ProfileSettingsScroll");
                var mainScroll = C<ScrollViewer>("MainScroll");
                var content = C<StackPanel>("ProfileSettingsContent");
                var source = C<Border>("ProfileConnectionCard");
                var route = C<Border>("ProfileRoutingCard");
                var sourceBounds = VisualBounds(source, content);
                var routeBounds = VisualBounds(route, content);
                bool twoColumns = content.ActualWidth >= 720;
                if (content.ActualWidth > 961 || content.ActualWidth < 500 || scroll.ExtentWidth > scroll.ViewportWidth + 1 ||
                    Math.Abs(VisualBounds(content, scroll).Left) > 1)
                    throw new InvalidOperationException("Profile settings content exceeds its width or loses left alignment: " + size.Width);
                if (Grid.GetColumn(route) != (twoColumns ? 1 : 0) || Grid.GetRow(route) != (twoColumns ? 0 : 1) ||
                    (twoColumns ? Math.Abs(sourceBounds.Top - routeBounds.Top) > 1 || routeBounds.Left < sourceBounds.Right + 15
                        : Math.Abs(sourceBounds.Left - routeBounds.Left) > 1 || routeBounds.Top < sourceBounds.Bottom + 15))
                    throw new InvalidOperationException("Profile settings cards do not follow their measured breakpoint: " + size.Width);
                foreach (string name in new[] { "ProfileSourceCombo", "ProfileModeCombo" })
                    if (C<ComboBox>(name).ActualWidth < 260)
                        throw new InvalidOperationException("Profile settings field is too narrow: " + name);
                foreach (string name in new[] { "ProfileIdentityCard", "ProfileRoutingGrid", "ProfileLinksGrid", "ProfileDiagnosticsCard" })
                {
                    var sectionBounds = VisualBounds(C<FrameworkElement>(name), content);
                    if (Math.Abs(sectionBounds.Left) > 1 || Math.Abs(sectionBounds.Right - content.ActualWidth) > 1)
                        throw new InvalidOperationException("Profile settings sections do not share their outer edges: " + name);
                }
                var appsBounds = VisualBounds(C<Button>("ProfileAppsButton"), content);
                var sitesBounds = VisualBounds(C<Button>("ProfileSitesButton"), content);
                if (Math.Abs(appsBounds.Left - sourceBounds.Left) > 1 || Math.Abs(appsBounds.Right - sourceBounds.Right) > 1 ||
                    Math.Abs(sitesBounds.Left - routeBounds.Left) > 1 || Math.Abs(sitesBounds.Right - routeBounds.Right) > 1 ||
                    Grid.GetColumn(C<Button>("ProfileSitesButton")) != Grid.GetColumn(route) || Grid.GetRow(C<Button>("ProfileSitesButton")) != Grid.GetRow(route) ||
                    (twoColumns ? Math.Abs(appsBounds.Top - sitesBounds.Top) > 1 || Math.Abs(appsBounds.Height - sitesBounds.Height) > 1
                        : Math.Abs(sitesBounds.Top - appsBounds.Bottom - 16) > 1))
                    throw new InvalidOperationException("Profile settings pairs do not share their columns or rhythm: " + size.Width);
                var validation = C<Button>("ValidateButton");
                if (Grid.GetColumn(validation) != (twoColumns ? 1 : 0) || Grid.GetRow(validation) != (twoColumns ? 0 : 1) || validation.ActualWidth > 260)
                    throw new InvalidOperationException("Profile diagnostics action does not adapt without stretching");
                if (twoColumns)
                {
                    var hint = C<TextBlock>("ProfileConnectionHint");
                    string originalHint = hint.Text;
                    try
                    {
                        hint.Text += ". Выбранное подключение можно изменить в любой момент, сохранив остальные настройки профиля.";
                        root.UpdateLayout();
                        if (Math.Abs(source.ActualHeight - route.ActualHeight) > 1 ||
                            Math.Abs(VisualBounds(C<ComboBox>("ProfileSourceCombo"), content).Bottom - VisualBounds(C<ComboBox>("ProfileModeCombo"), content).Bottom) > 1)
                            throw new InvalidOperationException("Routing fields lose their common baseline when a description wraps");
                    }
                    finally { hint.Text = originalHint; root.UpdateLayout(); }
                }
                await CaptureElement(root, "profile-settings-layout-" + size.Width);
                foreach (string name in new[] { "ValidateButton", "DuplicateProfileButton", "DeleteProfileButton" })
                {
                    var action = C<Button>(name);
                    action.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                    await Task.Delay(100);
                    root.UpdateLayout();
                    var bounds = VisualBounds(action, mainScroll);
                    if (bounds.Left < -1 || bounds.Right > mainScroll.ViewportWidth + 1 || bounds.Top < -1 || bounds.Bottom > mainScroll.ViewportHeight + 1)
                        throw new InvalidOperationException("Profile settings action is unreachable by scrolling: " + name);
                    if (name != "ValidateButton" && action.ActualWidth > 260)
                        throw new InvalidOperationException("Profile footer action stretches unnecessarily: " + name);
                }
                reports.Add(new { size.Width, size.Height, ContentWidth = content.ActualWidth, TwoColumns = twoColumns, Source = sourceBounds, Routing = routeBounds, Applications = appsBounds, Sites = sitesBounds, scroll.ExtentWidth, scroll.ViewportWidth });
                mainScroll.ChangeView(null, mainScroll.ScrollableHeight, null, true);
                await Task.Delay(100);
                await CaptureElement(root, "profile-settings-bottom-" + size.Width);
                mainScroll.ChangeView(null, 0, null, true);
            }
            File.WriteAllText(Path.Combine(folder, "profile-settings-layout-checks.json"), JsonSerializer.Serialize(reports, Store.JsonOptions));
        }
        finally
        {
            root.RequestedTheme = previousTheme;
            C<ScrollViewer>("MainScroll").ChangeView(null, 0, null, true);
            AppWindow.Resize(new((int)(1180 * scale), (int)(840 * scale)));
            SelectPage(previousPage);
            await Task.Delay(150);
        }
    }

    async Task CheckSidebarNavigation(double scale, string folder)
    {
        var reports = new List<object>();
        int previousPage = page;
        var previousTheme = root.RequestedTheme;
        try
        {
            root.RequestedTheme = ElementTheme.Light;
            foreach (bool narrow in new[] { false, true })
            {
                AppWindow.Resize(new((int)((narrow ? 800 : 1180) * scale), (int)((narrow ? 620 : 840) * scale)));
                foreach (int targetPage in new[] { 8, 1 })
                {
                    SelectPage(targetPage);
                    await Task.Delay(250);
                    root.UpdateLayout();
                    Responsive();
                    root.UpdateLayout();
                    var measured = new List<object>();
                    double? iconLeft = null, textLeft = null;
                    foreach (var item in new[] { (Button: "ProfileSuggestionsButton", Text: "ProfileSuggestionsText", Selected: targetPage == 8), (Button: "SettingsButton", Text: "SettingsText", Selected: targetPage == 1) })
                    {
                        var button = C<Button>(item.Button);
                        var icon = ((StackPanel)button.Content).Children.OfType<FontIcon>().Single();
                        var text = C<TextBlock>(item.Text);
                        var buttonBounds = VisualBounds(button, root);
                        var iconBounds = VisualBounds(icon, root);
                        var textBounds = VisualBounds(text, root);
                        var color = (button.Background as SolidColorBrush)?.Color;
                        if (button.RequestedTheme != ElementTheme.Dark || color == null ||
                            (item.Selected ? color.Value is not { A: 255, R: 0, G: 105, B: 98 } : color.Value.A != 0))
                            throw new InvalidOperationException("Sidebar navigation theme or selected background differs: " + item.Button);
                        if (Microsoft.UI.Xaml.Automation.AutomationProperties.GetItemStatus(button) != (item.Selected ? "Выбранный раздел" : ""))
                            throw new InvalidOperationException("Sidebar navigation accessibility state differs: " + item.Button);
                        if (narrow)
                        {
                            if (text.Visibility != Visibility.Collapsed || Math.Abs(iconBounds.Left + iconBounds.Width / 2 - buttonBounds.Left - buttonBounds.Width / 2) > 1)
                                throw new InvalidOperationException("Compact navigation icon is not centered: " + item.Button);
                        }
                        else if (text.Visibility != Visibility.Visible ||
                            iconLeft is { } x && Math.Abs(x - iconBounds.Left) > 1 ||
                            textLeft is { } labelX && Math.Abs(labelX - textBounds.Left) > 1)
                            throw new InvalidOperationException("Sidebar navigation labels do not align: " + item.Button);
                        iconLeft = iconBounds.Left;
                        textLeft = textBounds.Left;
                        measured.Add(new { Control = item.Button, item.Selected, Button = buttonBounds, Icon = iconBounds, Label = textBounds });
                    }
                    reports.Add(new { Compact = narrow, Page = targetPage, Controls = measured });
                }
            }
            File.WriteAllText(Path.Combine(folder, "sidebar-navigation-checks.json"), JsonSerializer.Serialize(reports, Store.JsonOptions));
        }
        finally
        {
            root.RequestedTheme = previousTheme;
            AppWindow.Resize(new((int)(1180 * scale), (int)(840 * scale)));
            SelectPage(previousPage);
            await Task.Delay(150);
        }
    }

    static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var descendant in VisualDescendants<T>(child))
                yield return descendant;
        }
    }

    async Task CheckProfileButtonStates(ListViewItem item, Button gear, string folder)
    {
        var themeBefore = root.RequestedTheme;
        var reports = new List<object>();
        try
        {
            foreach (var appearance in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = appearance;
                await Task.Delay(150);
                var presenter = VisualDescendants<ContentPresenter>(gear).First(p => p.Name == "ContentPresenter");
                var label = (FrameworkElement)((Grid)item.Content).Children[0];
                double labelWidth = label.ActualWidth;
                var colors = new List<Windows.UI.Color>();
                foreach (string state in new[] { "Normal", "PointerOver", "Pressed", "PointerOver", "Normal" })
                {
                    if (!VisualStateManager.GoToState(gear, state, true))
                        throw new Exception("Native profile button state missing: " + state);
                    await Task.Delay(150);
                    if (Math.Abs(label.ActualWidth - labelWidth) > .1 || gear.Visibility != Visibility.Visible)
                        throw new Exception("Profile button interaction shifted the label or hid the button");
                    if (presenter.Background is not SolidColorBrush brush)
                        throw new Exception("Profile button background missing");
                    colors.Add(brush.Color);
                    await CaptureElement(root, $"profile-button-{appearance.ToString().ToLowerInvariant()}-{reports.Count}-{state}");
                    reports.Add(new
                    {
                        Theme = appearance.ToString(),
                        State = state,
                        Background = brush.Color.ToString(),
                        BackgroundTransitionMilliseconds = presenter.BackgroundTransition?.Duration.TotalMilliseconds,
                        LabelWidth = labelWidth
                    });
                }
                if (colors[0] != colors[^1] || colors[1] != colors[3] || colors[0] == colors[1] || colors[1] == colors[2])
                    throw new Exception("Profile button hover/press states are indistinguishable or fail to return to rest");
                for (int i = 0; i < 20; i++)
                    VisualStateManager.GoToState(gear, i % 2 == 0 ? "PointerOver" : "Normal", true);
                await Task.Delay(150);
                if (presenter.Background is not SolidColorBrush restored || restored.Color != colors[0])
                    throw new Exception("Rapid hover transitions left a stale profile button state");
            }
            File.WriteAllText(Path.Combine(folder, "profile-button-states.json"), JsonSerializer.Serialize(new
            {
                Passed = reports.Count + 2,
                RapidTransitionsRestored = true,
                Checks = reports
            }, Store.JsonOptions));
        }
        finally
        {
            VisualStateManager.GoToState(gear, "Normal", false);
            root.RequestedTheme = themeBefore;
        }
    }

    async Task CheckProfileCapacity(double scale, string folder)
    {
        var originalProfiles = settings.Profiles;
        string originalSelected = settings.SelectedProfileId;
        var originalTheme = root.RequestedTheme;
        bool originalBinding = binding;
        var reports = new List<object>();
        try
        {
            binding = true;
            foreach (int count in new[] { 10, 15 })
            {
                settings.Profiles = Enumerable.Range(1, count).Select(i => new VpnProfile
                {
                    Name = $"{i:00} · " + (i % 3 == 0 ? "Очень длинное название профиля для проверки обрезки текста" : i % 3 == 1 ? "Работа и приложения" : "Браузеры и расширения"),
                    Icon = Icons[1 + (i - 1) % (Icons.Length - 1)].Key,
                    IconColor = (i - 1) % Palette.Length,
                    SourceId = settings.Sources.FirstOrDefault()?.Id ?? ""
                }).ToList();
                settings.SelectedProfileId = settings.Profiles[^1].Id;
                BindAll();
                SelectPage(0);
                var list = C<ListView>("ProfileList");
                var first = (ListViewItem)list.Items[0];
                var last = (ListViewItem)list.Items[^1];
                foreach (var appearance in new[] { ElementTheme.Light, ElementTheme.Dark })
                    foreach (bool narrow in new[] { false, true })
                    {
                        root.RequestedTheme = appearance;
                        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)((narrow ? 800 : 1180) * scale), (int)((narrow ? 620 : 840) * scale)));
                        await Task.Delay(350);
                        string name = $"profiles-{count}-{appearance.ToString().ToLowerInvariant()}-{(narrow ? "compact" : "wide")}";
                        var scroll = VisualDescendants<ScrollViewer>(list).First();
                        if (list.Items.Count != count || (list.SelectedItem as ListViewItem) != last || compact != narrow || scroll.ViewportHeight <= 0 || scroll.ScrollableHeight <= 0)
                            throw new Exception("Profile list size, selection or scrolling failed: " + name);
                        var listBottom = list.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, list.ActualHeight)).Y;
                        var footerTop = C<Button>("NewProfile").TransformToVisual(root).TransformPoint(new Windows.Foundation.Point()).Y;
                        if (listBottom > footerTop || scroll.ExtentWidth > scroll.ViewportWidth + 1)
                            throw new Exception("Profile list overlaps its footer or overflows horizontally: " + name);
                        foreach (var edge in new[] { (Item: first, Name: "first"), (Item: last, Name: "last") })
                        {
                            list.ScrollIntoView(edge.Item, ScrollIntoViewAlignment.Leading);
                            await Task.Delay(300);
                            if (!edge.Item.Focus(FocusState.Keyboard))
                                throw new Exception("Profile is unreachable by keyboard: " + name);
                            await Task.Delay(150);
                            var point = edge.Item.TransformToVisual(list).TransformPoint(new Windows.Foundation.Point());
                            if (point.Y < -1 || point.Y + edge.Item.ActualHeight > list.ActualHeight + 1 || point.X < -1 || point.X + edge.Item.ActualWidth > list.ActualWidth + 1)
                                throw new Exception("Profile is clipped after scrolling: " + name + " / " + edge.Name);
                            var host = (Grid)edge.Item.Content;
                            var gear = host.Children.OfType<Button>().Single();
                            if (gear.Visibility != (narrow ? Visibility.Collapsed : Visibility.Visible))
                                throw new Exception("Profile action visibility failed after scrolling: " + name);
                            await CaptureElement(root, name + "-" + edge.Name);
                        }
                        if ((list.SelectedItem as ListViewItem) != last || Profile?.Id != settings.Profiles[^1].Id)
                            throw new Exception("Scrolling or keyboard focus changed the selected profile: " + name);
                        reports.Add(new
                        {
                            Count = count,
                            Theme = appearance.ToString(),
                            Compact = narrow,
                            FirstAndLastVisible = true,
                            FooterClear = true,
                            SelectionPreserved = true,
                            scroll.ViewportHeight,
                            scroll.ExtentHeight
                        });
                    }
            }
            File.WriteAllText(Path.Combine(folder, "profile-capacity-checks.json"), JsonSerializer.Serialize(new
            {
                Passed = reports.Count,
                Checks = reports
            }, Store.JsonOptions));
        }
        finally
        {
            settings.Profiles = originalProfiles;
            settings.SelectedProfileId = originalSelected;
            root.RequestedTheme = originalTheme;
            BindAll();
            binding = originalBinding;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(840 * scale)));
        }
    }
}
