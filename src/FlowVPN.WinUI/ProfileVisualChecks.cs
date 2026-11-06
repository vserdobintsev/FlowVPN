using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
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
