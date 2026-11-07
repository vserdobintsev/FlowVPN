using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
namespace FlowVPN;

internal sealed partial class FluentWindow
{
    string? visualDialogName;
    Exception? visualDialogError;
    void ReviewDialog(ContentDialog dialog, Action? beforeCapture = null)
    {
        if (!Entry.VisualTest || visualDialogName == null)
            return;
        string name = visualDialogName;
        dialog.Opened += async (_, _) => { try { await Task.Delay(700); dialog.UpdateLayout(); beforeCapture?.Invoke(); await CaptureElement(dialog, name); } catch (Exception e) { visualDialogError = e; } finally { dialog.Hide(); } };
    }
    async Task CaptureElement(UIElement element, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var buffer = await bitmap.GetPixelsAsync();
        var bytes = new byte[buffer.Length];
        using (var data = DataReader.FromBuffer(buffer))
            data.ReadBytes(bytes);
        string path = Path.Combine(Store.Root, "design-preview", name + ".png");
        File.WriteAllBytes(path, []);
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    async Task VisualChecks()
    {
        var folder = Path.Combine(Store.Root, "design-preview");
        Directory.CreateDirectory(folder);
        await CheckMotion();
        // Motion is checked separately. Layout captures must show the settled page,
        // not a partially transparent frame of its entrance animation.
        motion.TestEnabled = false;
        File.Delete(Path.Combine(Store.Data, "visual-error.txt"));
        await FeatureChecks.Run(detectedPresets);
        await UpdateChecks.Run();
        var reports = new List<object>();
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        binding = true;
        C<ListView>("ProfileList").SelectedItem = C<ListView>("ProfileList").Items.OfType<ListViewItem>().FirstOrDefault(i => ((VpnProfile)i.Tag).Name == "Codex + Telegram");
        settings.SelectedProfileId = Profile?.Id ?? settings.SelectedProfileId;
        BindProfile();
        foreach (var spec in new[] { (Name: "desktop", Width: 1180, Height: 840, Page: 0, Theme: ElementTheme.Light), (Name: "compact", Width: 850, Height: 720, Page: 0, Theme: ElementTheme.Light), (Name: "minimum", Width: 800, Height: 620, Page: 0, Theme: ElementTheme.Light), (Name: "profile-settings", Width: 1180, Height: 840, Page: 4, Theme: ElementTheme.Light), (Name: "startup", Width: 1180, Height: 840, Page: 6, Theme: ElementTheme.Light), (Name: "startup-minimum", Width: 800, Height: 620, Page: 6, Theme: ElementTheme.Dark), (Name: "about", Width: 1180, Height: 840, Page: 7, Theme: ElementTheme.Light), (Name: "about-minimum", Width: 800, Height: 620, Page: 7, Theme: ElementTheme.Dark), (Name: "base", Width: 1180, Height: 840, Page: 1, Theme: ElementTheme.Light), (Name: "sources", Width: 1180, Height: 840, Page: 2, Theme: ElementTheme.Light), (Name: "updates", Width: 1180, Height: 840, Page: 5, Theme: ElementTheme.Light), (Name: "updates-minimum", Width: 800, Height: 620, Page: 5, Theme: ElementTheme.Light), (Name: "dark", Width: 1180, Height: 840, Page: 0, Theme: ElementTheme.Dark) })
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(spec.Width * scale), (int)(spec.Height * scale)));
            root.RequestedTheme = spec.Theme;
            SelectPage(spec.Page);
            await Task.Delay(450);
            root.UpdateLayout();
            Responsive();
            await Task.Delay(200);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(root);
            var buffer = await bitmap.GetPixelsAsync();
            var bytes = new byte[buffer.Length];
            using (var data = DataReader.FromBuffer(buffer))
                data.ReadBytes(bytes);
            string path = Path.Combine(folder, spec.Name + ".png");
            File.WriteAllBytes(path, []);
            var file = await StorageFile.GetFileFromPathAsync(path);
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
                await encoder.FlushAsync();
            }
            var measured = new List<object>();
            foreach (var name in new[] { "ProfileHeading", "BaseBadge", "ConnectButton", "SourceCombo", "TabApps", "TabLogs", "PageHost", "AddApp", "DomainsButton", "RemoveApps" })
            {
                var element = C<FrameworkElement>(name);
                if ((Page(spec.Page).General || spec.Page == 4) && (name is "BaseBadge" or "ConnectButton" or "SourceCombo" or "TabApps" or "TabLogs" or "AddApp" or "DomainsButton" or "RemoveApps"))
                    continue;
                var point = element.TransformToVisual(C<Grid>("MainArea")).TransformPoint(new Windows.Foundation.Point());
                bool inside = point.X >= -1 && point.Y >= -1 && point.X + element.ActualWidth <= C<Grid>("MainArea").ActualWidth + 1 && point.Y + element.ActualHeight <= C<Grid>("MainArea").ActualHeight + 1;
                if (!inside && spec.Page == 0 && name is "AddApp" or "DomainsButton" or "RemoveApps")
                {
                    element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                    await Task.Delay(100);
                    root.UpdateLayout();
                    point = element.TransformToVisual(C<Grid>("MainArea")).TransformPoint(new Windows.Foundation.Point());
                    inside = point.X >= -1 && point.Y >= -1 && point.X + element.ActualWidth <= C<Grid>("MainArea").ActualWidth + 1 && point.Y + element.ActualHeight <= C<Grid>("MainArea").ActualHeight + 1;
                }
                measured.Add(new
                {
                    Name = name,
                    Inside = inside,
                    X = point.X,
                    Y = point.Y,
                    Width = element.ActualWidth,
                    Height = element.ActualHeight
                });
                if (!inside)
                    throw new Exception("Layout overflow: " + spec.Name + " / " + name);
            }
            reports.Add(new
            {
                spec.Name,
                Width = root.ActualWidth,
                Height = root.ActualHeight,
                Compact = compact,
                Controls = measured
            });
        }
        await CheckOperationLayout(scale, folder);
        await CheckWorkspaceLayout(scale, folder);
        await CheckModuleCards(scale, folder);
        await CheckDeveloperIdentityLayout(scale, folder);
        await CheckSidebarNavigation(scale, folder);
        await CheckProfileSettingsLayout(scale, folder);
        await CheckEmptyApplicationsLayout(scale, folder);
        await CheckSettingsContentLayout(scale, folder);
        SelectPage(5);
        if (C<Button>("CheckAppUpdateButton").IsEnabled || C<Button>("PrepareUpdateButton").IsEnabled)
            throw new Exception("Unconfigured preview exposed an update action");
        SelectPage(0);
        File.WriteAllText(Path.Combine(folder, "layout-checks.json"), JsonSerializer.Serialize(reports, Store.JsonOptions));
        var catalog = await Task.Run(AppCatalog.Read);
        if (catalog.Count == 0)
            throw new Exception("Application catalogue is empty");
        var known = new List<object>();
        foreach (var app in catalog.Where(a => a.PresetId.Length > 0))
            known.Add(new
            {
                app.Name,
                Rules = app.Rules.Count,
                IconLoaded = (await AppIcons.Load(app))?.Length > 0
            });
        File.WriteAllText(Path.Combine(folder, "catalog-checks.json"), JsonSerializer.Serialize(new
        {
            Count = catalog.Count,
            Running = catalog.Count(a => a.Running),
            Known = known
        }, Store.JsonOptions));
        var iconDiagnostics = new List<object>();
        foreach (var app in catalog)
        {
            var bytes = await AppIcons.Load(app);
            iconDiagnostics.Add(new
            {
                Name = app.Name,
                Exe = Path.GetFileName(app.Path),
                Available = bytes != null,
                Hash = bytes == null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            });
        }
        File.WriteAllText(Path.Combine(folder, "icon-diagnostics.json"), JsonSerializer.Serialize(iconDiagnostics, Store.JsonOptions));
        var groups = ApplicationGroups.Create(Profile?.Apps ?? [], detectedPresets);
        File.WriteAllText(Path.Combine(folder, "group-checks.json"), JsonSerializer.Serialize(groups.Select(g => new { g.Name, Components = g.Rules.Count }), Store.JsonOptions));
        root.RequestedTheme = ElementTheme.Light;
        SelectPage(0);
        var expandedProfile = Profile ?? throw new InvalidOperationException("Expanded application fixture requires a profile");
        var originalApplications = expandedProfile.Apps;
        try
        {
            expandedProfile.Apps = [
                new AppRule { Name = "Основной компонент · визуальная проверка", Path = "visual-main.exe", Pattern = "^visual-main-fixture$", GroupId = "visual-expansion", GroupName = "Проверочная группа" },
                new AppRule { Name = "Вспомогательный компонент · визуальная проверка", Path = "visual-engine.exe", Pattern = "^visual-engine-fixture$", GroupId = "visual-expansion", GroupName = "Проверочная группа", ComponentRole = "engine" }
            ];
            BindProfile();
            var appList = C<ListView>("AppList");
            var item = appList.Items.OfType<ListViewItem>().Single();
            var expanded = (Expander)item.Content;
            expanded.IsExpanded = true;
            await Task.Delay(350);
            var component = ((StackPanel)expanded.Content).Children.OfType<FrameworkElement>().First();
            component.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            await Task.Delay(100);
            root.UpdateLayout();
            var inList = VisualBounds(component, appList);
            var inWindow = VisualBounds(component, C<ScrollViewer>("MainScroll"));
            if (!expanded.IsExpanded || inList.Top < -1 || inList.Bottom > appList.ActualHeight + 1 ||
                inWindow.Top < -1 || inWindow.Bottom > C<ScrollViewer>("MainScroll").ViewportHeight + 1)
                throw new InvalidOperationException("Expanded application component is clipped in the visual fixture");
            await CaptureElement(root, "application-expanded");
        }
        finally
        {
            expandedProfile.Apps = originalApplications;
            BindProfile();
            C<ScrollViewer>("MainScroll").ChangeView(null, 0, null, true);
        }
        SelectPage(1);
        if (C<FrameworkElement>("SectionNavigation").Visibility != Visibility.Collapsed || C<FrameworkElement>("ConnectionCard").Visibility != Visibility.Collapsed || C<ListView>("ProfileList").SelectedItem != null)
            throw new Exception("Settings scope is mixed with profile controls");
        SelectPage(3);
        if (C<TextBlock>("LogScope").Text.Contains(Profile?.Name ?? "missing") == false)
            throw new Exception("Journal scope missing");
        SelectPage(0);
        visualDialogName = "profile-icons";
        await EditProfileIcon();
        root.RequestedTheme = ElementTheme.Dark;
        visualDialogName = "profile-icons-dark";
        await EditProfileIcon();
        root.RequestedTheme = ElementTheme.Light;
        visualDialogName = "application-picker";
        await PickApplications();
        visualDialogName = "application-extensions";
        await PickApplications();
        visualDialogName = "preset-components";
        await PickApplications();
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(800 * scale), (int)(620 * scale)));
        root.RequestedTheme = ElementTheme.Dark;
        await Task.Delay(400);
        visualDialogName = "preset-components-minimum";
        await PickApplications();
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(840 * scale)));
        visualDialogName = "application-picker-dark";
        await PickApplications();
        root.RequestedTheme = ElementTheme.Light;
        visualDialogName = "core-connections";
        await ShowCoreConnections();
        visualDialogName = "domains";
        await EditDomains();
        await CheckCategoryPicker();
        visualDialogName = "running-applications";
        await PickProcesses();
        root.RequestedTheme = ElementTheme.Dark;
        visualDialogName = "running-details";
        await PickProcesses();
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(800 * scale), (int)(620 * scale)));
        await Task.Delay(300);
        visualDialogName = "running-minimum";
        await PickProcesses();
        visualDialogName = "running-system";
        await PickProcesses();
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(840 * scale)));
        visualDialogName = null;
        var trayMenu = tray.ContextMenuStrip!;
        File.WriteAllText(Path.Combine(folder, "taskbar-checks.json"), JsonSerializer.Serialize(new
        {
            taskbar.Registered,
            taskbar.LastResult,
            Buttons = 1,
            Action = "Connect/Disconnect",
            PreviewDisabled = true
        }, Store.JsonOptions));
        if (!taskbar.Registered)
            throw new Exception("Taskbar thumbnail toolbar was not registered: " + taskbar.LastResult);
        var top = trayMenu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().ToArray();
        var general = top.Single(i => i.Text == "Настройки приложения");
        var profileMenu = top.Single(i => i.Text == "Профили");
        if (general.DropDownItems.Count != 4 || profileMenu.DropDownItems.Count != settings.Profiles.Count || top.Any(i => i.Text == "Общая база"))
            throw new Exception("Tray scopes are inconsistent");
        if (profileMenu.DropDownItems.OfType<System.Windows.Forms.ToolStripMenuItem>().Any(i => !i.DropDownItems.OfType<System.Windows.Forms.ToolStripMenuItem>().Any(c => c.Text == "Настройки профиля")))
            throw new Exception("Tray profile actions are missing");
        if (trayToggle == null || top.Count(i => ReferenceEquals(i, trayToggle)) != 1 || trayToggle.Enabled)
            throw new Exception("Tray must have one disabled connection toggle in preview");
        File.WriteAllText(Path.Combine(folder, "tray-checks.json"), JsonSerializer.Serialize(new
        {
            ScopesSeparated = true,
            ProfileMenus = profileMenu.DropDownItems.Count,
            Settings = general.DropDownItems.OfType<System.Windows.Forms.ToolStripMenuItem>().Select(i => i.Text)
        }, Store.JsonOptions));
        await CheckProfileActions(scale, folder);
        await CheckProfileCapacity(scale, folder);
        foreach (var state in new[] { "connected", "disconnected", "busy", "warning" })
        {
            using var icon = TrayArt.Status(state).ToBitmap();
            icon.Save(Path.Combine(folder, "tray-" + state + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        if (visualDialogError != null)
            throw visualDialogError;
    }

    async Task CheckProfileActions(double scale, string folder)
    {
        var item = C<ListView>("ProfileList").Items.OfType<ListViewItem>().First();
        var host = (Grid)item.Content;
        var gear = host.Children.OfType<Button>().Single();
        var label = (FrameworkElement)host.Children[0];
        var outside = C<Button>("ThemeButton");
        var results = new List<object>();
        async Task Focus(UIElement target, FocusState state)
        {
            if (!target.Focus(state))
                throw new Exception("Profile action focus target is unavailable");
            await Task.Delay(150);
        }
        void Check(string name, bool visible)
        {
            if (gear.Visibility != (visible ? Visibility.Visible : Visibility.Collapsed) ||
                label.Margin.Right != (compact ? 0 : 32))
                throw new Exception("Profile action visibility mismatch: " + name + " / " + item.FocusState + " / " + gear.FocusState);
            results.Add(new
            {
                Name = name,
                Visible = visible,
                ItemFocus = item.FocusState.ToString(),
                ButtonFocus = gear.FocusState.ToString()
            });
        }

        await Focus(outside, FocusState.Pointer);
        await Focus(item, FocusState.Programmatic);
        // Programmatic focus retains WinUI's effective input modality; it is not a pointer-focus request.
        Check("programmatic-focus-respects-input-mode", item.FocusState == FocusState.Keyboard);
        await CaptureElement(root, "profile-actions-programmatic");
        await Focus(item, FocusState.Keyboard);
        Check("keyboard-focus-visible", true);
        await CaptureElement(root, "profile-actions-focus");
        await Focus(gear, FocusState.Keyboard);
        Check("keyboard-button-visible", true);
        await Focus(outside, FocusState.Keyboard);
        Check("focus-left-hidden", false);
        await Focus(item, FocusState.Pointer);
        Check("pointer-focus-without-hover-hidden", false);
        await Focus(item, FocusState.Keyboard);
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(850 * scale), (int)(720 * scale)));
        await Task.Delay(300);
        Check("compact-hidden", false);
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(840 * scale)));
        await Task.Delay(300);
        Check("expanded-keyboard-visible", true);
        await CheckProfileButtonStates(item, gear, folder);
        await Focus(outside, FocusState.Pointer);
        File.WriteAllText(Path.Combine(folder, "profile-actions-checks.json"), JsonSerializer.Serialize(new
        {
            Passed = results.Count,
            Checks = results
        }, Store.JsonOptions));
    }
}
