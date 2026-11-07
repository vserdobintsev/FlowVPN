using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinForms = System.Windows.Forms;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    const int MaxConnectionText = 4 * 1024 * 1024;
    const int MaxConnectionFiles = 32;
    bool importingConnections;

    static string ConnectionTypeLabel(VpnSource source) => source.Kind == "Outline" ? "Shadowsocks / Outline" : source.Kind;

    static string ConnectionKindLabel(ConnectionInputKind kind) => kind switch
    {
        ConnectionInputKind.OpenVpn => "OpenVPN",
        ConnectionInputKind.OutlineKey => "Ключ Outline / Shadowsocks",
        ConnectionInputKind.DynamicOutlineKey => "Динамический ключ Outline",
        ConnectionInputKind.ShadowsocksJson => "Shadowsocks · JSON",
        _ => "Формат не распознан"
    };

    async Task AddConnection()
    {
        if (importingConnections || dialogOpen)
            return;
        var name = new TextBox { Header = "Название · необязательно", PlaceholderText = "Например, личный сервер", MaxLength = 180 };
        var hidden = new PasswordBox { Header = "Ключ или конфигурация", MaxLength = MaxConnectionText, PasswordRevealMode = PasswordRevealMode.Hidden };
        var editor = new TextBox
        {
            Header = "Ключ или конфигурация", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"), MinHeight = 170, MaxHeight = 260,
            MaxLength = MaxConnectionText, Visibility = Visibility.Collapsed
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        var reveal = new CheckBox { Content = "Показать текст", IsChecked = false };
        var paste = new Button { Content = CommandContent("\uE77F", "Вставить из буфера") };
        var files = new Button { Content = CommandContent("\uE8B7", "Выбрать файлы…") };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(paste);
        actions.Children.Add(files);
        var detected = new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Title = "Формат определится автоматически", Message = "Поддерживаются конфигурации OpenVPN, ключи Outline и параметры Shadowsocks в JSON." };
        var form = new StackPanel { Spacing = 12, MinWidth = 340 };
        foreach (var item in new UIElement[] { name, actions, hidden, editor, reveal, detected })
            form.Children.Add(item);
        string input = "";
        bool synchronizing = false, chooseFiles = false, closed = false;
        ContentDialog? activeDialog = null;
        CancellationTokenSource? detection = null;

        async void DetectInput()
        {
            detection?.Cancel();
            detection?.Dispose();
            detection = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
            var token = detection.Token;
            string snapshot = input;
            if (activeDialog != null)
                activeDialog.IsPrimaryButtonEnabled = false;
            try
            {
                await Task.Delay(250, token);
                var kind = await Task.Run(() => ConnectionInput.Detect(snapshot), token);
                if (closed || windowClosed || token.IsCancellationRequested)
                    return;
                detected.Title = string.IsNullOrWhiteSpace(snapshot) ? "Формат определится автоматически" : ConnectionKindLabel(kind);
                detected.Severity = kind == ConnectionInputKind.Unknown && snapshot.Length > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
                detected.Message = kind switch
                {
                    ConnectionInputKind.OpenVpn => "При вставке сертификаты и ключ должны быть внутри конфигурации. Для отдельных сертификатов выберите исходный файл .ovpn: пути будут найдены относительно его папки.",
                    ConnectionInputKind.DynamicOutlineKey => "Конфигурация будет загружена только после нажатия «Добавить».",
                    ConnectionInputKind.OutlineKey or ConnectionInputKind.ShadowsocksJson => "Формат распознан. Параметры будут проверены при добавлении; VPN сейчас не запускается.",
                    _ => "Вставьте ключ или полную конфигурацию. Если у вас только адрес, порт и пароль, откройте «Параметры сервера…»."
                };
                if (activeDialog != null)
                    activeDialog.IsPrimaryButtonEnabled = kind != ConnectionInputKind.Unknown;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                if (closed || windowClosed)
                    return;
                detected.Severity = InfoBarSeverity.Error;
                detected.Title = "Не удалось определить формат";
                detected.Message = "Проверьте содержимое или выберите файл конфигурации.";
            }
        }
        void SetInput(string value)
        {
            input = value;
            synchronizing = true;
            try { hidden.Password = value; editor.Text = value; }
            finally { synchronizing = false; }
            DetectInput();
        }
        async Task PasteInput()
        {
            try
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text))
                    throw new InvalidDataException("В буфере обмена нет текста.");
                string text = await content.GetTextAsync();
                if (closed || windowClosed)
                    return;
                if (text.Length > MaxConnectionText)
                    throw new InvalidDataException("Текст слишком большой. Выберите файл конфигурации размером не более 4 МиБ.");
                SetInput(text);
            }
            catch (Exception)
            {
                if (closed || windowClosed)
                    return;
                detected.Severity = InfoBarSeverity.Warning;
                detected.Title = "Не удалось вставить текст";
                detected.Message = "Скопируйте ключ или конфигурацию заново. Допустимый размер — до 4 МиБ.";
            }
        }
        hidden.PasswordChanged += (_, _) => { if (!synchronizing) { input = hidden.Password; DetectInput(); } };
        editor.TextChanged += (_, _) => { if (!synchronizing) { input = editor.Text; DetectInput(); } };
        hidden.Paste += async (_, args) => { args.Handled = true; await PasteInput(); };
        editor.Paste += async (_, args) => { args.Handled = true; await PasteInput(); };
        paste.Click += async (_, _) => await PasteInput();
        void UpdateReveal()
        {
            synchronizing = true;
            try { hidden.Password = input; editor.Text = input; }
            finally { synchronizing = false; }
            hidden.Visibility = reveal.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            editor.Visibility = reveal.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        reveal.Checked += (_, _) => UpdateReveal();
        reveal.Unchecked += (_, _) => UpdateReveal();
        files.Click += (_, _) => { chooseFiles = true; activeDialog?.Hide(); };
        (ContentDialogResult Result, VpnSource[]? Sources) result;
        try
        {
            result = await ConnectionDialog("Добавить подключение", form, async token =>
                {
                    string snapshot = input, connectionName = name.Text;
                    return [await Task.Run(() => ConnectionInput.ImportAsync(snapshot, connectionName, token), token)];
                }, dialog =>
                {
                    activeDialog = dialog;
                    dialog.IsPrimaryButtonEnabled = false;
                    dialog.SecondaryButtonText = "Параметры сервера…";
                    dialog.PrimaryButtonClick += (_, _) => detection?.Cancel();
                    dialog.Closed += (_, _) => { closed = true; detection?.Cancel(); };
                });
        }
        finally
        {
            closed = true;
            detection?.Cancel();
            detection?.Dispose();
            synchronizing = true;
            hidden.Password = "";
            editor.Text = "";
            input = "";
        }
        if (windowClosed)
            return;
        if (chooseFiles)
            await ImportConnectionFiles();
        else if (result.Result == ContentDialogResult.Secondary)
            await AddShadowsocksConnection();
        else if (result.Sources is { Length: > 0 })
            await SaveConnections(result.Sources);
    }

    async Task AddShadowsocksConnection()
    {
        var name = new TextBox { Header = "Название · необязательно", MaxLength = 180 };
        var host = new TextBox { Header = "Адрес сервера", PlaceholderText = "Домен, IPv4 или IPv6", MaxLength = 253 };
        var port = new NumberBox { Header = "Порт", Value = 443, Minimum = 1, Maximum = 65535, SmallChange = 1, LargeChange = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, ValidationMode = NumberBoxValidationMode.Disabled };
        var method = new ComboBox { Header = "Метод шифрования", ItemsSource = Importers.ShadowsocksMethods, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var password = new PasswordBox { Header = "Пароль", MaxLength = 65536, PasswordRevealMode = PasswordRevealMode.Peek };
        var form = new StackPanel { Spacing = 12, MinWidth = 340 };
        form.Children.Add(new TextBlock { Text = "Параметры Shadowsocks от вашего провайдера или администратора. Для OpenVPN нужен файл или текст конфигурации.", TextWrapping = TextWrapping.Wrap });
        foreach (var item in new UIElement[] { name, host, port, method, password })
            form.Children.Add(item);
        try
        {
            var result = await ConnectionDialog("Параметры сервера Shadowsocks", form, _ =>
            {
                if (!int.TryParse(port.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 1 or > 65535 || !double.IsFinite(port.Value) || port.Value != value)
                    throw new InvalidDataException("Укажите целый порт от 1 до 65535.");
                if (password.Password.Length == 0)
                    throw new InvalidDataException("Введите пароль сервера.");
                var source = Importers.Shadowsocks(host.Text, value, method.SelectedItem as string ?? "", password.Password, name.Text);
                return Task.FromResult(new[] { source });
            });
            if (result.Sources is { Length: > 0 } && !windowClosed)
                await SaveConnections(result.Sources);
        }
        finally { password.Password = ""; }
    }

    async Task<(ContentDialogResult Result, VpnSource[]? Sources)> ConnectionDialog(string title, StackPanel form,
        Func<CancellationToken, Task<VpnSource[]>> create, Action<ContentDialog>? configure = null)
    {
        var error = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error, Title = "Проверьте параметры" };
        form.Children.Add(error);
        var formHost = new ContentControl { Content = form, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var content = new ScrollViewer { Content = formHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Clamp(root.ActualHeight - 250, 230, 540) };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        VpnSource[]? sources = null;
        bool submitting = false, dialogClosing = false;
        var result = await Dialog(title, content, "Добавить", configure: dialog =>
        {
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
                if (submitting)
                    return;
                var deferral = args.GetDeferral();
                submitting = true;
                dialog.IsPrimaryButtonEnabled = false;
                dialog.IsSecondaryButtonEnabled = false;
                formHost.IsEnabled = false;
                error.IsOpen = false;
                dialog.PrimaryButtonText = "Добавление…";
                try
                {
                    sources = await create(lifetime.Token);
                    args.Cancel = windowClosed || lifetime.IsCancellationRequested;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    if (!windowClosed && !dialogClosing)
                    {
                        error.Message = ConnectionError(exception);
                        error.IsOpen = true;
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
                            dialog.PrimaryButtonText = "Добавить";
                            dialog.IsPrimaryButtonEnabled = true;
                            dialog.IsSecondaryButtonEnabled = true;
                        }
                    }
                    finally { deferral.Complete(); }
                }
            };
            configure?.Invoke(dialog);
        });
        return (result, result == ContentDialogResult.Primary ? sources : null);
    }

    async Task ImportConnectionFiles()
    {
        using var picker = new WinForms.OpenFileDialog
        {
            Filter = "Конфигурации и ключи (*.ovpn;*.conf;*.txt;*.json)|*.ovpn;*.conf;*.txt;*.json|Все файлы (*.*)|*.*",
            Multiselect = true, CheckFileExists = true, RestoreDirectory = true, Title = "Выберите файлы подключений"
        };
        if (picker.ShowDialog(new HwndOwner(this)) != WinForms.DialogResult.OK)
            return;
        string[] paths = picker.FileNames;
        if (paths.Length > MaxConnectionFiles)
        {
            ConnectionFeedback("Слишком много файлов", "За один раз можно добавить до 32 файлов.", InfoBarSeverity.Warning);
            return;
        }
        await WithConnectionImport(async () =>
        {
            var rows = new List<(string Path, CheckBox Selection)>();
            var unreadable = new List<string>();
            var form = new StackPanel { Spacing = 10, MinWidth = 340 };
            form.Children.Add(new TextBlock { Text = "Формат определён по содержимому. Выберите подключения для добавления. Динамические ключи загрузятся только после подтверждения.", TextWrapping = TextWrapping.Wrap });
            foreach (string path in paths)
            {
                var kind = ConnectionInputKind.Unknown;
                try { kind = await Task.Run(() => ConnectionInput.DetectFileAsync(path, windowLifetime.Token), windowLifetime.Token); }
                catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested) { return; }
                catch (Exception) { }
                if (windowClosed)
                    return;
                var label = new StackPanel { Spacing = 3 };
                label.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextWrapping = TextWrapping.Wrap });
                label.Children.Add(new TextBlock { Text = ConnectionKindLabel(kind), Opacity = .7, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                var selection = new CheckBox { Content = label, IsChecked = kind != ConnectionInputKind.Unknown, IsEnabled = kind != ConnectionInputKind.Unknown, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                if (kind == ConnectionInputKind.Unknown)
                    unreadable.Add(Path.GetFileName(path));
                rows.Add((path, selection));
                form.Children.Add(selection);
            }
            var failures = new List<string>();
            var result = await ConnectionDialog("Добавить подключения из файлов", form, async token =>
            {
                var selected = rows.Where(row => row.Selection.IsChecked == true).ToArray();
                if (selected.Length == 0)
                    throw new InvalidDataException("Выберите хотя бы один файл с распознанным форматом.");
                failures.Clear();
                var sources = new List<VpnSource>();
                foreach (var row in selected)
                {
                    token.ThrowIfCancellationRequested();
                    try { sources.Add(await Task.Run(() => ConnectionInput.ImportFileAsync(row.Path, token: token), token)); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception) { failures.Add(Path.GetFileName(row.Path)); }
                }
                if (sources.Count == 0)
                    throw new InvalidDataException("Не удалось проверить файлы: " + string.Join(", ", failures.Take(8)) + ". Проверьте конфигурации и связанные сертификаты.");
                return sources.ToArray();
            });
            if (result.Sources is { Length: > 0 } && !windowClosed)
                await SaveConnections(result.Sources, unreadable.Concat(failures).ToArray());
        });
    }

    async Task ImportClients() => await WithConnectionImport(async () =>
    {
        var imported = new Settings();
        var result = await Task.Run(() => Importers.Installed(imported), windowLifetime.Token);
        if (windowClosed)
            return;
        await SaveConnections(imported.Sources, importNotes: result.Notes);
    });

    async Task WithConnectionImport(Func<Task> action)
    {
        if (importingConnections)
            return;
        importingConnections = true;
        C<Button>("AddConnectionButton").IsEnabled = false;
        C<Button>("ImportClientsButton").IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (windowClosed) { }
        finally
        {
            importingConnections = false;
            if (!windowClosed)
            {
                C<Button>("AddConnectionButton").IsEnabled = true;
                C<Button>("ImportClientsButton").IsEnabled = true;
            }
        }
    }

    async Task SaveConnections(IEnumerable<VpnSource> sources, IReadOnlyList<string>? failures = null, IReadOnlyList<string>? importNotes = null)
    {
        bool firstConnection = settings.Sources.Count == 0;
        int added = 0, duplicates = 0;
        VpnSource? selected = null;
        bool needsOutlineModule = false;
        foreach (var source in sources)
        {
            needsOutlineModule |= ModuleTransport.RequiredModule(source) != null;
            if (Store.AddSource(settings, source))
            {
                added++;
                selected = source;
            }
            else
            {
                duplicates++;
                selected = settings.Sources.First(item => Store.SameSource(item, source));
            }
        }
        Store.Save(settings);
        BindAll();
        SelectPage(2);
        if (selected != null)
        {
            var list = C<ListView>("SourceList");
            list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(item => (item.Tag as VpnSource)?.Id == selected.Id);
            if (list.SelectedItem != null)
                list.ScrollIntoView(list.SelectedItem);
        }
        string summary = $"Добавлено: {added}. Уже существуют: {duplicates}.";
        if (failures is { Count: > 0 })
            summary += " Не добавлены: " + string.Join(", ", failures.Take(8)) + (failures.Count > 8 ? $" и ещё {failures.Count - 8}" : "") + ".";
        if (importNotes is { Count: > 0 })
            summary += "\n" + string.Join("\n", importNotes.Take(8));
        string title = added > 0 ? "Подключения добавлены" : duplicates > 0 ? "Подключение уже существует" : "Новых подключений не найдено";
        ConnectionFeedback(title, summary, failures is { Count: > 0 } || importNotes is { Count: > 0 } ? InfoBarSeverity.Warning : added > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
        if (needsOutlineModule)
            await EnsureModuleAvailableAsync(ModuleTransport.OutlineCapability);
        if (firstConnection && added > 0 && settings.Profiles.Count == 0 && !settings.ProfileOnboardingCompleted)
            await ShowProfileSuggestions(onboarding: true);
    }

    void ConnectionFeedback(string title, string message, InfoBarSeverity severity)
    {
        var feedback = C<InfoBar>("SourceFeedback");
        feedback.Title = title;
        feedback.Message = message;
        feedback.Severity = severity;
        feedback.IsOpen = true;
    }

    static string ConnectionError(Exception error) => error switch
    {
        System.Text.Json.JsonException or FormatException or ArgumentException => "Неверный формат ключа или конфигурации. Проверьте скопированный текст и параметры сервера.",
        HttpRequestException => "Не удалось загрузить динамический ключ. Проверьте адрес и доступ к интернету.",
        OperationCanceledException => "Время ожидания истекло. Повторите попытку.",
        UnauthorizedAccessException => "Нет доступа к файлу подключения или связанным сертификатам.",
        InvalidDataException => error.Message,
        IOException => "Не удалось прочитать конфигурацию или связанный файл сертификата.",
        _ when error.GetType() == typeof(Exception) => error.Message,
        _ => "Не удалось добавить подключение. Проверьте параметры и повторите попытку."
    };
}
