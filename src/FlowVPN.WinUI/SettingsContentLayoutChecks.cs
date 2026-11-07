using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task CheckSettingsContentLayout(double scale, string folder)
    {
        int previousPage = page;
        var previousTheme = root.RequestedTheme;
        var reports = new List<object>();
        void CheckSelector(string name, FrameworkElement parent, double maximum)
        {
            var combo = C<ComboBox>(name);
            var bounds = VisualBounds(combo, parent);
            if (bounds.Width <= 100 || bounds.Width > maximum + 1 || bounds.Left < -1 || bounds.Right > parent.ActualWidth + 1 || Math.Abs(bounds.Left) > 1)
                throw new InvalidOperationException("Settings selector width or alignment differs: " + name);
            reports.Add(new { Control = name, Width = bounds.Width, Cap = maximum, ParentWidth = parent.ActualWidth });
        }
        try
        {
            foreach (int width in new[] { 800, 1600 })
            {
                AppWindow.Resize(new((int)(width * scale), (int)(840 * scale)));
                root.RequestedTheme = width == 800 ? ElementTheme.Dark : ElementTheme.Light;
                foreach (var spec in new[] { (Page: 5, Name: "Updates"), (Page: 6, Name: "Startup"), (Page: 7, Name: "About") })
                {
                    SelectPage(spec.Page);
                    await Task.Delay(200);
                    root.UpdateLayout(); Responsive(); root.UpdateLayout();
                    var content = C<StackPanel>(spec.Name + "Content");
                    var scroll = C<ScrollViewer>(spec.Name + "Scroll");
                    var bounds = VisualBounds(content, scroll);
                    if (bounds.Width > 961 || bounds.Width < 500 || Math.Abs(bounds.Left) > 1 || scroll.ExtentWidth > scroll.ViewportWidth + 1)
                        throw new InvalidOperationException("Settings content cap or horizontal scrolling differs: " + spec.Name);
                    if (spec.Page == 5)
                    {
                        CheckSelector("UpdateChannelCombo", content, 320);
                        var versions = C<Grid>("InstalledVersions");
                        var appVersion = VisualBounds(C<StackPanel>("AppVersionInfo"), versions);
                        var coreVersion = VisualBounds(C<StackPanel>("CoreVersionInfo"), versions);
                        bool sideBySide = Grid.GetRow(C<StackPanel>("CoreVersionInfo")) == 0;
                        if (versions.ActualWidth > 641 || versions.ActualWidth > content.ActualWidth + 1
                            || Math.Abs(VisualBounds(versions, content).Left) > 1
                            || sideBySide && (Math.Abs(coreVersion.Left - appVersion.Right - 32) > 1 || Math.Abs(coreVersion.Top - appVersion.Top) > 1)
                            || !sideBySide && (Math.Abs(coreVersion.Left - appVersion.Left) > 1 || Math.Abs(coreVersion.Top - appVersion.Bottom - 16) > 1))
                            throw new InvalidOperationException("Installed version details are stretched or misaligned: " + width);
                        var lastCheck = C<TextBlock>("LastUpdateCheck");
                        var source = C<TextBlock>("UpdateSourceText");
                        if (Channel?.FeedUrl.Length is not > 0 && (lastCheck.Visibility != Visibility.Collapsed || source.Visibility != Visibility.Collapsed
                            || lastCheck.Text.Length != 0 || source.Text.Length != 0 || C<Button>("CheckAppUpdateButton").IsEnabled))
                            throw new InvalidOperationException("An unconfigured update feed exposes irrelevant status or enabled checks");
                        bool previousChecking = checkingUpdate;
                        try
                        {
                            // Exercise both geometry states without configuring a feed or making a request.
                            foreach (bool busy in new[] { false, true, false })
                            {
                                checkingUpdate = busy;
                                RefreshUpdates();
                                lastCheck.Text = "Проверочная дата"; source.Text = "Проверочный источник";
                                lastCheck.Visibility = source.Visibility = Visibility.Visible;
                                root.UpdateLayout();
                                var statusContent = C<StackPanel>("UpdateStatusContent");
                                var title = VisualBounds(C<TextBlock>("UpdateStatus"), statusContent);
                                var date = VisualBounds(lastCheck, statusContent);
                                var origin = VisualBounds(source, statusContent);
                                if (Math.Abs(date.Left - origin.Left) > 1 || Math.Abs(title.Left - date.Left - (busy ? 30 : 0)) > 1)
                                    throw new InvalidOperationException("Update status retains an invisible spinner gap: " + width + " / " + busy);
                            }
                        }
                        finally { checkingUpdate = previousChecking; RefreshUpdates(); root.UpdateLayout(); }
                        reports.Add(new { Section = "UpdateDetails", WindowWidth = width, VersionsWidth = versions.ActualWidth, SideBySide = sideBySide, Gap = sideBySide ? 32 : 16, StatusAlignment = true });
                    }
                    reports.Add(new { Section = spec.Name, WindowWidth = width, ContentWidth = bounds.Width, scroll.ViewportWidth });
                }
                SelectPage(1);
                await Task.Delay(200);
                root.UpdateLayout();
                CheckSelector("DomainCategoryCombo", (FrameworkElement)C<ComboBox>("DomainCategoryCombo").Parent, 420);
                SelectPage(0);
                await Task.Delay(200);
                root.UpdateLayout(); Responsive(); root.UpdateLayout();
                CheckSelector("ModeCombo", C<Grid>("AppsContent"), 560);
                CheckSelector("SourceCombo", C<Grid>("SourceGrid"), 560);
            }
            File.WriteAllText(Path.Combine(folder, "settings-content-layout-checks.json"), JsonSerializer.Serialize(reports, Store.JsonOptions));
        }
        finally
        {
            root.RequestedTheme = previousTheme;
            AppWindow.Resize(new((int)(1180 * scale), (int)(840 * scale)));
            SelectPage(previousPage);
            await Task.Delay(150);
        }
    }
}
