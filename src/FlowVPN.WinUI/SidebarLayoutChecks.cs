using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task CheckSidebarLayout(double scale, string folder)
    {
        var originalProfiles = settings.Profiles.ToList();
        string originalSelection = settings.SelectedProfileId;
        int originalPage = page;
        bool originalBinding = binding;
        var originalSize = AppWindow.Size;
        var reports = new List<object>();
        var fixture = new[] { "Codex + Telegram", "Outline · Codex + Telegram", "Яндекс Браузер" }
            .Select(name => new VpnProfile { Name = name, SourceId = settings.Sources.FirstOrDefault()?.Id ?? "", Icon = "network" }).ToArray();
        var baselineWidths = new Dictionary<string, double>(StringComparer.Ordinal);
        double previousWidth = 0, previousSidebar = 0;
        try
        {
            binding = true;
            settings.Profiles.Clear();
            settings.Profiles.AddRange(fixture);
            settings.SelectedProfileId = fixture[0].Id;
            BindAll();
            SelectPage(0);
            int step = 0;
            foreach (int width in new[] { 800, 1180, 1360, 1600, 2200, 800, 1180 })
            {
                AppWindow.Resize(new((int)(width * scale), (int)(840 * scale)));
                await Task.Delay(180);
                root.UpdateLayout(); Responsive(); root.UpdateLayout();
                double actualWidth = root.ActualWidth;
                double sidebar = C<ColumnDefinition>("SidebarColumn").ActualWidth;
                var scroll = C<ScrollViewer>("MainScroll");
                var main = C<Grid>("MainArea");
                var mainBounds = VisualBounds(main, root);
                var cardBounds = VisualBounds(C<Border>("ConnectionCard"), root);
                if (compact ? Math.Abs(sidebar - 80) > 1 : sidebar is < 247 or > 361)
                    throw new InvalidOperationException("Sidebar width is outside its compact or expanded bounds");
                if (actualWidth >= 1050 && actualWidth <= 1250 && Math.Abs(sidebar - 248) > 1)
                    throw new InvalidOperationException("The medium window unnecessarily narrows its page for the sidebar");
                if (actualWidth >= 1500 && Math.Abs(sidebar - 360) > 1)
                    throw new InvalidOperationException("A wide window does not expose the sidebar's full useful width");
                if (actualWidth > previousWidth + 1 && sidebar + 1 < previousSidebar)
                    throw new InvalidOperationException("The sidebar shrinks while the window grows");
                if (Math.Abs(mainBounds.Left - sidebar) > 1 || Math.Abs(cardBounds.Left - sidebar - main.Padding.Left) > 1 ||
                    scroll.ScrollableWidth > 1 || mainBounds.Width > WorkspaceContentWidth + 1)
                    throw new InvalidOperationException("A gap or horizontal overflow separates the sidebar from the page");
                var labels = new List<object>();
                for (int index = 0; index < fixture.Length; index++)
                {
                    var item = C<ListView>("ProfileList").Items.OfType<ListViewItem>().Single(candidate => ReferenceEquals(candidate.Tag, fixture[index]));
                    var itemBounds = VisualBounds(item, root);
                    var normal = profileLabels[index].Normal;
                    var mini = profileLabels[index].Compact;
                    if (itemBounds.Left < -1 || itemBounds.Right > sidebar + 1 || itemBounds.Width <= 0)
                        throw new InvalidOperationException("A profile row escapes the sidebar");
                    if (compact)
                    {
                        if (normal.Visibility != Visibility.Collapsed || mini.Visibility != Visibility.Visible)
                            throw new InvalidOperationException("Compact profiles must show their icons instead of clipped labels");
                        continue;
                    }
                    var title = VisualDescendants<TextBlock>(normal).Single(text => text.Text == fixture[index].Name);
                    if (normal.Visibility != Visibility.Visible || mini.Visibility != Visibility.Collapsed || title.ActualWidth <= 0)
                        throw new InvalidOperationException("Expanded profile labels did not recover after compact mode");
                    // A TextBlock may keep its natural text width once the name fits;
                    // measure the available row, and check trimming independently.
                    if (width == 1180 && !baselineWidths.ContainsKey(fixture[index].Id)) baselineWidths.Add(fixture[index].Id, normal.ActualWidth);
                    if (actualWidth >= 1500 && (title.IsTextTrimmed || normal.ActualWidth < baselineWidths[fixture[index].Id] + 70))
                        throw new InvalidOperationException($"A typical profile name cannot use the wider sidebar: {fixture[index].Name}; row={normal.ActualWidth}; trimmed={title.IsTextTrimmed}");
                    labels.Add(new { title.Text, Width = title.ActualWidth, RowWidth = normal.ActualWidth, Trimmed = title.IsTextTrimmed, Bounds = VisualBounds(title, root) });
                }
                var commands = C<StackPanel>("SidebarCommands");
                double commandWidth = commands.ActualWidth;
                if (Math.Abs(commandWidth - (compact ? 44 : 212)) > 1)
                    throw new InvalidOperationException("Sidebar commands stretch with the navigation column");
                var commandBounds = VisualBounds(commands, root);
                if (Math.Abs(commandBounds.Left - (sidebar - commandWidth) / 2) > 1)
                    throw new InvalidOperationException("Sidebar commands are not centered in the navigation column");
                foreach (int destination in new[] { 8, 1, 0 })
                {
                    SelectPage(destination);
                    root.UpdateLayout();
                    foreach (string buttonName in new[] { "NewProfile", "ProfileSuggestionsButton", "SettingsButton" })
                    {
                        var button = C<Button>(buttonName);
                        foreach (string state in new[] { "PointerOver", "Pressed", "Normal" })
                        {
                            if (!VisualStateManager.GoToState(button, state, false))
                                throw new InvalidOperationException("Sidebar command state is unavailable: " + state);
                            root.UpdateLayout();
                            var bounds = VisualBounds(button, commands);
                            if (Math.Abs(bounds.Left) > 1 || Math.Abs(bounds.Width - commandWidth) > 1)
                                throw new InvalidOperationException("Sidebar command state expands outside the bounded group");
                        }
                    }
                }
                reports.Add(new { RequestedWidth = width, ActualWidth = actualWidth, SidebarWidth = sidebar, Commands = commandBounds, Compact = compact, Main = mainBounds, Card = cardBounds, Labels = labels });
                if (step++ < 5) await CaptureElement(root, "sidebar-width-" + width);
                previousWidth = actualWidth;
                previousSidebar = sidebar;
            }
            File.WriteAllText(Path.Combine(folder, "sidebar-layout-checks.json"), JsonSerializer.Serialize(reports, Store.JsonOptions));
        }
        finally
        {
            try
            {
                settings.Profiles.Clear();
                settings.Profiles.AddRange(originalProfiles);
                settings.SelectedProfileId = originalSelection;
                AppWindow.Resize(originalSize);
                BindAll();
                SelectPage(originalPage);
                await Task.Delay(150);
            }
            finally { binding = originalBinding; }
        }
    }
}
