using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinForms = System.Windows.Forms;

namespace FlowVPN;

internal sealed partial class FluentWindow : Window
{
    readonly Grid root;
    readonly Settings settings;
    readonly VpnEngine engine = new();
    readonly WinForms.NotifyIcon tray;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly Motion motion;
    readonly IDisposable sizing;
    readonly TaskbarControls taskbar;
    readonly List<(FrameworkElement Normal, FrameworkElement Compact)> profileLabels = [];
    bool binding, closing, updating, compact, dialogOpen;
    int page, theme;
    DateTime nextUpdate = DateTime.MinValue;
    T C<T>(string name) where T : class => (root.FindName(name) as T) ?? throw new Exception("UI element: " + name);
    VpnProfile? Profile => (C<ListView>("ProfileList").SelectedItem as ListViewItem)?.Tag as VpnProfile ?? settings.Profiles.FirstOrDefault(p => p.Id == settings.SelectedProfileId);
    VpnSource? Source => (C<ListView>("SourceList").SelectedItem as ListViewItem)?.Tag as VpnSource;
    static SolidColorBrush Color(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
    public FluentWindow()
    {
        motion = new Motion(DispatcherQueue);
        Title = "FlowVPN";
        settings = Store.Load();
        if (CatalogRuleMigration.Apply(settings.Profiles.SelectMany(p => p.Apps), detectedPresets))
            Store.Save(settings);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FlowVPN.Interface.xml") ?? throw new Exception("Interface resource missing");
        using var reader = new StreamReader(stream);
        root = (Grid)XamlReader.Load(reader.ReadToEnd());
        Content = root;
        double dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        int windowWidth = Math.Min((int)(1180 * dpi), area.Width - 40), windowHeight = Math.Min((int)(840 * dpi), area.Height - 40);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(windowWidth, windowHeight));
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - windowWidth) / 2, area.Y + (area.Height - windowHeight) / 2));
        theme = settings.Appearance;
        root.RequestedTheme = theme == 1 ? ElementTheme.Light : theme == 2 ? ElementTheme.Dark : ElementTheme.Default;
        string iconPath = Path.Combine(AppContext.BaseDirectory, "FlowVPN.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
        sizing = WindowSizing.Install(this, 800, 620);
        taskbar = new(WinRT.Interop.WindowNative.GetWindowHandle(this), connect => DispatcherQueue.TryEnqueue(async () =>
        {
            if (windowClosed || Entry.Preview || engine.Busy)
                return;
            try
            {
                if (connect && !engine.Running)
                    await Connect();
                else if (!connect && engine.Running)
                    await Disconnect();
            }
            catch (Exception e) { Restore(); await Message(e.Message); }
        }));
        AppWindow.Closing += (_, e) => { if (!closing) { e.Cancel = true; AppWindow.Hide(); } };
        tray = new WinForms.NotifyIcon { Icon = new System.Drawing.Icon(iconPath), Text = "FlowVPN", Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) DispatcherQueue.TryEnqueue(Restore); };
        Closed += CloseResources;
        Hook("SettingsButton", () => { SelectPage(1); return Task.CompletedTask; });
        Hook("NewProfile", NewProfile);
        Hook("ThemeButton", () => { Theme(); return Task.CompletedTask; });
        Hook("BaseBadge", () => { SelectPage(1); return Task.CompletedTask; });
        Hook("ConnectButton", async () => { if (engine.Running || engine.Busy) await Disconnect(); else await Connect(); });
        Hook("ApplyButton", async () => { await Disconnect(); await Connect(); });
        C<NavigationView>("SectionNavigation").SelectionChanged += (_, e) => { if (e.SelectedItem is NavigationViewItem item) SelectPage(int.Parse((string)item.Tag)); };
        Hook("AddApp", () => { AppMenu(); return Task.CompletedTask; });
        Hook("RemoveApps", () => { RemoveApplications(); return Task.CompletedTask; });
        Hook("DomainsButton", EditDomains);
        Hook("ImportClientsButton", ImportClients);
        Hook("ImportFileButton", () => { ImportMenu(); return Task.CompletedTask; });
        Hook("RenameSourceButton", RenameSource);
        Hook("DeleteSourceButton", DeleteSource);
        Hook("UpdateRulesButton", () => UpdateRules(true));
        Hook("RepositoryLink", () => { Process.Start(new ProcessStartInfo(RuleRepository.Repository) { UseShellExecute = true }); return Task.CompletedTask; });
        Hook("RefreshLogButton", () => { UpdateLog(); return Task.CompletedTask; });
        Hook("OpenLogsButton", async () =>
        {
            if (!await Windows.System.Launcher.LaunchFolderPathAsync(ProfileLogFolder()))
                throw new IOException("Windows не удалось открыть папку журнала.");
        });
        Hook("ValidateButton", Validate);
        C<ListView>("ProfileList").SelectionChanged += (_, _) => { if (binding) return; settings.SelectedProfileId = Profile?.Id ?? ""; Store.Save(settings); BindProfile(); SelectPage(0); Pending(); UpdateTray(); };
        C<ComboBox>("SourceCombo").SelectionChanged += (_, _) => { if (binding || Profile == null) return; Profile.SourceId = (C<ComboBox>("SourceCombo").SelectedItem as ComboBoxItem)?.Tag as string ?? ""; Save(); };
        C<ComboBox>("ModeCombo").SelectionChanged += (_, _) => { if (binding || Profile == null) return; Profile.Mode = new[] { "include", "exclude", "all" }[Math.Max(0, C<ComboBox>("ModeCombo").SelectedIndex)]; Save(); UpdateMode(); };
        C<ListView>("AppList").SelectionChanged += (_, _) => C<Button>("RemoveApps").IsEnabled = C<ListView>("AppList").SelectedItems.Count > 0;
        C<ListView>("SourceList").SelectionChanged += (_, _) => { C<Button>("RenameSourceButton").IsEnabled = Source != null; C<Button>("DeleteSourceButton").IsEnabled = Source != null; };
        C<ToggleSwitch>("BaseSwitch").Toggled += (_, _) => { if (binding) return; settings.BaseRulesEnabled = C<ToggleSwitch>("BaseSwitch").IsOn; Save(); UpdateBase(); };
        C<ToggleSwitch>("AutoUpdateSwitch").Toggled += (_, _) => { if (binding) return; settings.AutoUpdateRules = C<ToggleSwitch>("AutoUpdateSwitch").IsOn; Store.Save(settings); };
        engine.Changed += EngineChanged;
        root.SizeChanged += (_, _) => Responsive();
        timer.Tick += BackgroundTick;
        C<NavigationView>("SettingsNavigation").SelectionChanged += (_, e) => { if (e.SelectedItem is NavigationViewItem item) SelectPage(int.Parse((string)item.Tag)); };
        Hook("CoreConnectionsButton", ShowCoreConnections);
        HookProfileSettings();
        HookBaseCategories();
        HookStartup();
        HookUpdates();
        HookAbout();
        DecorateButtons();
        BindAll();
        timer.Start();
        root.Loaded += OnRootLoaded;
    }
    void Hook(string name, Func<Task> action)
    {
        C<ButtonBase>(name).Click += async (_, _) => { try { await action(); } catch (Exception e) { await Message(e.Message, "Не удалось выполнить действие"); } };
    }
    static TextBlock Text(string text, double size = 14, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
    void BindAll()
    {
        bool previous = binding;
        binding = true;
        try
        {
            var list = C<ListView>("ProfileList");
            list.Items.Clear();
            profileLabels.Clear();
            profileDots.Clear();
            foreach (var p in settings.Profiles)
            {
                var normal = ProfileNormal(p);
                var kind = settings.Sources.FirstOrDefault(s => s.Id == p.SourceId)?.Kind ?? "Нет подключения";
                var mini = ProfileGlyph(p);
                var grid = new Grid { MinHeight = 50 };
                grid.Children.Add(normal);
                grid.Children.Add(mini);
                mini.Visibility = Visibility.Collapsed;
                profileLabels.Add((normal, mini));
                var item = new ListViewItem { Content = grid, Tag = p, MinWidth = 0, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 2, 0, 2), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                ToolTipService.SetToolTip(item, p.Name + " · " + kind);
                AttachProfileActions(item, p, grid);
                list.Items.Add(item);
                if (p.Id == settings.SelectedProfileId)
                    list.SelectedItem = item;
            }
            if (list.SelectedIndex < 0 && list.Items.Count > 0)
                list.SelectedIndex = 0;
            var combo = C<ComboBox>("SourceCombo");
            combo.Items.Clear();
            var sl = C<ListView>("SourceList");
            sl.Items.Clear();
            foreach (var s in settings.Sources)
            {
                combo.Items.Add(new ComboBoxItem { Content = s.Name + " · " + s.Kind, Tag = s.Id });
                sl.Items.Add(Card(s.Name, s.Kind + " · " + s.Origin, "\uE968", s));
            }
        }
        finally { binding = previous; }
        BindProfile();
        UpdateBase();
        UpdateState();
        UpdateTray();
        Responsive();
        SelectPage(page);
    }
    ListViewItem Card(string name, string detail, string glyph, object tag)
    {
        var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(2, 8, 2, 8) };
        g.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(38)
        });
        g.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = Color(225, 242, 238), Child = new FontIcon { Glyph = glyph, FontSize = 16, Foreground = Color(0, 125, 112) } };
        g.Children.Add(icon);
        var stack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Text(name, 14, true));
        stack.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = 0.66, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(stack, 1);
        g.Children.Add(stack);
        var item = new ListViewItem { Content = g, Tag = tag, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 2, 0, 2) };
        ToolTipService.SetToolTip(item, detail);
        return item;
    }
    void BindProfile()
    {
        bool previous = binding;
        binding = true;
        try
        {
            var p = Profile;
            C<TextBlock>("ProfileHeading").Text = p?.Name ?? "Создайте профиль";
            var combo = C<ComboBox>("SourceCombo");
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == p?.SourceId);
            C<ComboBox>("ModeCombo").SelectedIndex = p?.Mode == "exclude" ? 1 : p?.Mode == "all" ? 2 : 0;
            BindProfileSettings(p);
            BindApplications(p);
            C<Button>("DomainsButton").Content = CommandContent("\uE774", "Домены" + (p?.Domains.Count > 0 ? $" · {p.Domains.Count}" : "…"));
        }
        finally { binding = previous; }
        UpdateMode();
        UpdateHeading();
        UpdateState();
    }
    void UpdateMode()
    {
        C<TextBlock>("ModeHint").Text = Profile?.Mode switch
        {
            "exclude" => "Выбранные приложения идут напрямую. Адреса из общей базы всё равно идут через VPN.",
            "all" => "Весь интернет идёт через VPN. Локальная сеть остаётся доступной напрямую.",
            _ => "Выбранные приложения и адреса из общей базы — через VPN. Остальное — напрямую."
        };
        C<ListView>("AppList").IsEnabled = Profile?.Mode != "all";
    }
    void Save()
    {
        Store.Save(settings);
        Pending();
    }
    void Pending()
    {
        C<InfoBar>("PendingInfo").IsOpen = engine.Running;
        UpdateState();
    }
    void UpdateBase()
    {
        bool previous = binding;
        binding = true;
        try
        {
            C<ToggleSwitch>("BaseSwitch").IsOn = settings.BaseRulesEnabled;
            C<ToggleSwitch>("AutoUpdateSwitch").IsOn = settings.AutoUpdateRules;
            C<TextBlock>("BaseBadgeText").Text = settings.BaseRulesEnabled ? "База включена" : "База выключена";
            C<Button>("BaseBadge").Opacity = settings.BaseRulesEnabled ? 1 : 0.65;
            C<TextBlock>("BaseMetadata").Text = RuleRepository.Description() + "\n" + DomainRepository.Description(settings.BaseDomainCategory);
            BindBaseCategories();
        }
        finally { binding = previous; }
    }
    void UpdateState()
    {
        if (page == 5)
            RefreshUpdates();
        UpdateProfileDots();
        C<Button>("CoreConnectionsButton").IsEnabled = Entry.VisualTest || (!Entry.Preview && engine.Running && engine.ActiveProfileId == Profile?.Id);
        bool busy = engine.Busy;
        C<Button>("ProfileReconnectButton").IsEnabled = !Entry.Preview && engine.Running && !busy;
        C<Button>("ProfileReconnectButton").Visibility = engine.Running ? Visibility.Visible : Visibility.Collapsed;
        C<TextBlock>("StatusText").Text = Entry.Preview ? "Предпросмотр" : busy ? "Подключение…" : engine.Running ? (engine.Healthy ? "Подключено" : "Нет связи") : "Отключено";
        C<TextBlock>("StatusDetail").Text = Entry.Preview ? "Проверка интерфейса · VPN не запускается" : engine.Running ? "Профиль: " + engine.ActiveProfile + "  ·  " + engine.Status.Replace("Подключено  •  ", "") : busy ? engine.Status : engine.Status == "Отключено" ? "Подключение запускается вручную" : engine.Status;
        C<Microsoft.UI.Xaml.Shapes.Ellipse>("StatusDot").Fill = engine.Healthy ? Color(0, 145, 125) : engine.Running ? Color(210, 135, 35) : Color(130, 145, 163);
        C<ProgressRing>("BusyRing").IsActive = busy;
        C<ProgressRing>("BusyRing").Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        C<Button>("ConnectButton").IsEnabled = !Entry.Preview && Profile != null && (!busy || engine.Running);
        C<TextBlock>("ConnectText").Text = engine.Running ? "Отключить" : busy ? "Подождите…" : "Подключить";
        C<Button>("ApplyButton").IsEnabled = !Entry.Preview && engine.Running && !busy;
        C<Button>("ApplyButton").Visibility = engine.Running ? Visibility.Visible : Visibility.Collapsed;
        SetTrayState();
    }
    async Task Connect()
    {
        if (Entry.Preview || Profile == null)
            return;
        if (Entry.ReleaseUiTest)
            throw new InvalidOperationException("Подключение недоступно во время проверки сборки.");
        if (stagingUpdate)
            throw new Exception("Дождитесь подготовки обновления.");
        await engine.Connect(settings, Profile);
        if (windowClosed || !engine.Running)
            return;
        C<InfoBar>("PendingInfo").IsOpen = false;
        if (settings.AutoUpdateRules && (RuleRepository.Due() || DomainRepository.Due(settings.BaseDomainCategory)))
            await UpdateRules(false);
    }
    async Task Disconnect()
    {
        await engine.Disconnect();
        C<InfoBar>("PendingInfo").IsOpen = false;
    }
    void Responsive()
    {
        if (root.ActualWidth <= 0)
            return;
        C<Grid>("MainArea").Height = Math.Max(root.ActualHeight, 680);
        compact = root.ActualWidth < 1050;
        C<ColumnDefinition>("SidebarColumn").Width = new GridLength(compact ? 80 : 248);
        foreach (var n in new[] { "BrandLabel", "ProfilesCaption", "NewProfileText", "SettingsText" })
            C<FrameworkElement>(n).Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (ListViewItem item in C<ListView>("ProfileList").Items)
        {
            item.MinWidth = 0;
            item.Padding = compact ? new Thickness(2) : new Thickness(10, 8, 10, 8);
            item.Width = compact ? 44 : double.NaN;
            if (item.Content is Grid grid)
            {
                grid.Width = compact ? 40 : double.NaN;
            }
        }
        foreach (var pair in profileLabels)
        {
            pair.Normal.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            pair.Compact.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        }
        C<Grid>("MainArea").Padding = new Thickness(compact ? 18 : 28, 20, compact ? 18 : 28, 14);
        C<ListView>("AppList").MaxHeight = Math.Max(120, root.ActualHeight - 470);
        C<ListView>("SourceList").MaxHeight = Math.Max(130, root.ActualHeight - 450);
        C<TextBlock>("ProfileHeading").FontSize = root.ActualWidth < 900 ? 22 : 27;
        C<Button>("ThemeButton").Content = compact ? "◐" : theme == 1 ? "☀  Светлая тема" : theme == 2 ? "◐  Тёмная тема" : "◐  Системная тема";
        C<StackPanel>("SidebarFooter").Children[1].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
    void Theme()
    {
        theme = (theme + 1) % 3;
        settings.Appearance = theme;
        Store.Save(settings);
        root.RequestedTheme = theme == 1 ? ElementTheme.Light : theme == 2 ? ElementTheme.Dark : ElementTheme.Default;
        C<Button>("ThemeButton").Content = theme == 1 ? "☀  Светлая тема" : theme == 2 ? "◐  Тёмная тема" : "◐  Системная тема";
    }
    async Task<string?> Prompt(string title, string label, string value = "", bool multiline = false, bool secret = false)
    {
        Control input = secret ? new PasswordBox { Password = value, MinWidth = 300 } : new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinWidth = 300, MinHeight = multiline ? 150 : 0, PlaceholderText = label };
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(input);
        var result = await Dialog(title, stack, "Сохранить");
        return result == ContentDialogResult.Primary ? (input is PasswordBox p ? p.Password : ((TextBox)input).Text).Trim() : null;
    }
    async Task<ContentDialogResult> Dialog(string title, object content, string primary = "OK", bool cancel = true)
    {
        if (dialogOpen)
            return ContentDialogResult.None;
        dialogOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = cancel ? "Отмена" : "", DefaultButton = ContentDialogButton.Primary };
            ReviewDialog(dialog);
            return await dialog.ShowAsync();
        }
        finally { dialogOpen = false; }
    }
    async Task Message(string text, string title = "FlowVPN")
    {
        await Dialog(title, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 }, "Понятно", false);
    }
    async Task NewProfile()
    {
        var name = await Prompt("Новый профиль", "Название профиля");
        if (string.IsNullOrWhiteSpace(name))
            return;
        var p = new VpnProfile { Name = name, SourceId = settings.Sources.FirstOrDefault()?.Id ?? "" };
        settings.Profiles.Add(p);
        settings.SelectedProfileId = p.Id;
        Store.Save(settings);
        BindAll();
        SelectPage(4);
    }
    MenuFlyout Menu(params (string Text, Func<Task> Action)[] actions)
    {
        var menu = new MenuFlyout();
        foreach (var action in actions)
        {
            var item = new MenuFlyoutItem { Text = action.Text, Icon = new FontIcon { Glyph = ActionGlyph(action.Text), FontSize = 16 } };
            item.Click += async (_, _) => { try { await action.Action(); } catch (Exception e) { await Message(e.Message); } };
            menu.Items.Add(item);
        }
        return menu;
    }
    void AddApps(IEnumerable<AppRule> rules)
    {
        if (Profile == null)
            throw new Exception("Создайте профиль.");
        ApplicationGroups.Add(Profile, rules);
        Save();
        BindProfile();
    }
    void AppMenu()
    {
        Menu(("Выбрать приложения…", PickApplications), ("Из запущенных процессов…", PickProcesses), ("Выбрать EXE-файлы…", () => { using var d = new WinForms.OpenFileDialog { Filter = "Приложения (*.exe)|*.exe", Multiselect = true }; if (d.ShowDialog(new HwndOwner(this)) == WinForms.DialogResult.OK) AddApps(d.FileNames.Select(AppRule.File)); return Task.CompletedTask; }
        ), ("Папка приложения…", () => { using var d = new WinForms.FolderBrowserDialog { Description = "Включить все EXE в папке и её подпапках", UseDescriptionForTitle = true }; if (d.ShowDialog(new HwndOwner(this)) == WinForms.DialogResult.OK) AddApps([AppRule.Folder(d.SelectedPath)]); return Task.CompletedTask; }
        )).ShowAt(C<Button>("AddApp"));
    }
    async Task ImportClients()
    {
        C<Button>("ImportClientsButton").IsEnabled = false;
        try
        {
            // Read client files on the worker; merge into the live model only on the UI thread.
            var imported = new Settings();
            var result = await Task.Run(() => Importers.Installed(imported));
            if (windowClosed)
                return;
            int added = 0;
            foreach (var source in imported.Sources)
                if (Store.AddSource(settings, source))
                    added++;
            Store.SeedProfiles(settings);
            Store.Save(settings);
            BindAll();
            await Message($"Добавлено подключений: {added}." + (result.Notes.Count > 0 ? "\n\n" + string.Join("\n", result.Notes) : ""), "Импорт завершён");
        }
        finally { if (!windowClosed) C<Button>("ImportClientsButton").IsEnabled = true; }
    }
    void ImportMenu()
    {
        Menu(("Файл OpenVPN (.ovpn)…", () => { using var d = new WinForms.OpenFileDialog { Filter = "OpenVPN (*.ovpn)|*.ovpn" }; if (d.ShowDialog(new HwndOwner(this)) == WinForms.DialogResult.OK) { Store.AddSource(settings, Importers.OpenVpn(d.FileName)); Store.SeedProfiles(settings); Store.Save(settings); BindAll(); } return Task.CompletedTask; }
        ), ("Ключ Outline…", async () => { var key = await Prompt("Импорт Outline", "Ключ ss:// или ssconf://", secret: true); if (string.IsNullOrWhiteSpace(key)) return; var source = await Importers.DynamicOutline(key); Store.AddSource(settings, source); Store.SeedProfiles(settings); Store.Save(settings); BindAll(); }
        )).ShowAt(C<Button>("ImportFileButton"));
    }
    async Task RenameSource()
    {
        var source = Source;
        if (source == null)
            return;
        var name = await Prompt("Название подключения", "Новое название", source.Name);
        if (string.IsNullOrWhiteSpace(name) || !settings.Sources.Contains(source))
            return;
        source.Name = name;
        Store.Save(settings);
        BindAll();
    }
    async Task DeleteSource()
    {
        var source = Source;
        if (source == null)
            return;
        void CheckUsed()
        {
            if (settings.Profiles.Any(p => p.SourceId == source.Id))
                throw new InvalidOperationException("Подключение используется профилем. Сначала выберите другой источник в этом профиле.");
        }
        CheckUsed();
        if (await Dialog("Удалить подключение?", new TextBlock { Text = source.Name }, "Удалить") == ContentDialogResult.Primary)
        {
            CheckUsed();
            settings.Sources.Remove(source);
            Store.Save(settings);
            BindAll();
        }
    }
    async Task UpdateRules(bool show)
    {
        if (updating || switchingCategory)
            return;
        updating = true;
        C<Button>("UpdateRulesButton").IsEnabled = false;
        try
        {
            using var client = engine.Running ? engine.Client() : BoundedHttp.Client(TimeSpan.FromSeconds(40));
            var changed = await RuleRepository.Update(client);
            await DomainCategories.Update(client);
            changed |= await DomainRepository.Update(client, settings.BaseDomainCategory);
            UpdateBase();
            if (show)
                await Message(changed ? "Проверенная база загружена. Новые правила применятся при следующем подключении." : "Установлена актуальная версия.", "Общая база");
        }
        catch (Exception e) { if (show) await Message("Предыдущая копия базы сохранена.\n" + e.Message, "Обновление не удалось"); else C<TextBlock>("BaseMetadata").Text = RuleRepository.Description() + "\nНе удалось проверить обновления; используется сохранённая копия."; }
        finally { updating = false; C<Button>("UpdateRulesButton").IsEnabled = true; }
    }
    async Task Validate()
    {
        if (Profile == null)
            return;
        var file = Path.Combine(Store.Run, "validation.json");
        try
        {
            Store.WriteJson(file, await ConfigBuilder.Build(settings, Profile, ConfigBuilder.Detect(), 15876));
            await CoreCli.Run("check", "-c", file);
        }
        finally { File.Delete(file); }
        await Message("Конфигурация принята sing-box. Доступность сервера проверяется при подключении.", "Проверка пройдена");
    }
    void UpdateTray() => BuildTray();
    void Restore()
    {
        AppWindow.Show();
        Activate();
    }
    sealed class HwndOwner(Window window) : WinForms.IWin32Window
    {
        public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(window);
    }
}
