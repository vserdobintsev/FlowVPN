using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    sealed class ModuleCard(ModuleDefinition definition)
    {
        public ModuleDefinition Definition { get; } = definition;
        public readonly TextBlock State = new() { Text = "Проверяем…", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        public readonly TextBlock Hint = new() { FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap };
        public readonly ProgressRing Busy = new() { Width = 18, Height = 18, IsActive = true };
        public ModuleState? StableState;
        public readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
        public readonly Button Install = new() { Content = "Установить", IsEnabled = false, CornerRadius = new(8) };
        public readonly Button Remove = new() { Content = "Удалить", Visibility = Visibility.Collapsed, CornerRadius = new(8) };
    }

    readonly Dictionary<string, ModuleCard> moduleCards = new(StringComparer.Ordinal);
    readonly Dictionary<string, ModuleStatus> moduleStatuses = new(StringComparer.Ordinal);
    Task<bool>? moduleInstallTask;
    CancellationTokenSource? moduleInstallCancellation;
    string? moduleTransferId;
    string? moduleRemovalId;
    bool moduleTransferActive, modulesRefreshing, modulesRefreshAgain;
    int modulesRefreshQueued;
    OperationTicket moduleOperation;

    void HookModules()
    {
        var body = C<StackPanel>("ModulesBody");
        body.Children.Add(new TextBlock { Text = "Дополнительные возможности", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "Расширяйте возможности FlowVPN с помощью модулей.", TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        foreach (var definition in moduleManager.Modules)
        {
            var card = new ModuleCard(definition);
            moduleCards.Add(definition.Id, card);
            body.Children.Add(CreateModuleCard(card));
            card.Install.Click += async (_, _) =>
            {
                if (!moduleStatuses.TryGetValue(definition.Id, out var status) || status.State == ModuleState.Unavailable)
                    await RefreshModulesAsync();
                else await InstallModuleAsync(definition.Id);
            };
            card.Remove.Click += async (_, _) => await RemoveModuleAsync(card);
        }
        if (moduleCards.Count == 0)
            body.Children.Add(new TextBlock { Text = "Для этой версии пока нет дополнительных модулей.", TextWrapping = TextWrapping.Wrap });
        moduleManager.Changed += ModuleManagerChanged;
        _ = Operation(OperationKind.Module);
    }

    FrameworkElement CreateModuleCard(ModuleCard card)
    {
        var panel = new StackPanel { Spacing = 14 };
        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(ProfileGlyph(new VpnProfile { Icon = "network" }, track: false, adaptToTheme: true));
        var labels = new StackPanel { Spacing = 5 };
        labels.Children.Add(new TextBlock { Text = card.Definition.Name, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        labels.Children.Add(new TextBlock { Text = ModuleMetadata(card.Definition), FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(labels, 1);
        header.Children.Add(labels);
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock { Text = card.Definition.Description, TextWrapping = TextWrapping.Wrap });
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stateRow.Children.Add(card.Busy);
        stateRow.Children.Add(card.State);
        panel.Children.Add(stateRow);
        panel.Children.Add(card.Hint);
        card.Actions.Children.Add(card.Install);
        card.Actions.Children.Add(card.Remove);
        panel.Children.Add(card.Actions);
        var frame = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1"
                CornerRadius="12" Padding="20" />
            """);
        frame.Child = panel;
        frame.SizeChanged += (_, _) => card.Actions.Orientation = frame.ActualWidth < 430 ? Orientation.Vertical : Orientation.Horizontal;
        AutomationProperties.SetName(card.Install, "Установить модуль «" + card.Definition.Name + "»");
        AutomationProperties.SetName(card.Remove, "Удалить модуль «" + card.Definition.Name + "»");
        AutomationProperties.SetName(card.Busy, "Операция с модулем «" + card.Definition.Name + "»");
        return frame;
    }

    void ShowModulesPage() => _ = RefreshModulesAsync();

    void ModuleManagerChanged(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref modulesRefreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            Interlocked.Exchange(ref modulesRefreshQueued, 0);
            if (windowClosed) return;
            UpdateModuleTransferProgress();
            await RefreshModulesAsync();
        })) Interlocked.Exchange(ref modulesRefreshQueued, 0);
    }

    async Task RefreshModulesAsync()
    {
        if (windowClosed) return;
        if (modulesRefreshing) { modulesRefreshAgain = true; return; }
        modulesRefreshing = true;
        try
        {
            do
            {
                modulesRefreshAgain = false;
                foreach (var (id, card) in moduleCards)
                {
                    if (moduleManager.DownloadProgress is { } active && active.ModuleId != id)
                    {
                        if (moduleStatuses.TryGetValue(id, out var known)) RenderModuleCard(card, known);
                        continue;
                    }
                    try
                    {
                        var status = await moduleManager.GetStatusAsync(id, windowLifetime.Token);
                        if (windowClosed) return;
                        moduleStatuses[id] = status;
                        RenderModuleCard(card, status);
                    }
                    catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        if (windowClosed) return;
                        moduleStatuses.Remove(id);
                        card.State.Text = "Состояние неизвестно";
                        card.Hint.Text = "";
                        card.Install.Content = "Установить";
                        card.Install.IsEnabled = false;
                        card.Install.Visibility = Visibility.Visible;
                        card.Remove.Visibility = card.Busy.Visibility = Visibility.Collapsed;
                        card.Busy.IsActive = false;
                        if (!moduleTransferActive && moduleRemovalId == null)
                        {
                            var operation = BeginOperation(OperationKind.Module, "Проверка · " + card.Definition.Name, null);
                            CompleteOperation(operation, "Не удалось проверить модуль", ModuleErrorText.Describe(error), InfoBarSeverity.Error, RefreshModulesAsync);
                        }
                    }
                }
            } while (modulesRefreshAgain && !windowClosed);
        }
        finally { modulesRefreshing = false; }
    }

    void RenderModuleCard(ModuleCard card, ModuleStatus status)
    {
        bool installing = status.State == ModuleState.Installing || moduleTransferActive && moduleTransferId == card.Definition.Id;
        if (status.State != ModuleState.Installing) card.StableState = status.State;
        bool installed = card.StableState is ModuleState.Installed or ModuleState.InUse;
        card.State.Text = card.StableState is { } stable ? ModuleStateName(stable) : "Проверяем…";
        card.Hint.Text = card.StableState is { } known ? ModuleStateHint(known) : "";
        card.Install.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        card.Install.Content = card.StableState == ModuleState.Corrupt ? "Восстановить" : "Установить";
        card.Install.IsEnabled = !moduleTransferActive && moduleRemovalId == null && status.State != ModuleState.Unavailable && card.Definition.CanDownload;
        card.Remove.Visibility = installed || card.StableState == ModuleState.Corrupt ? Visibility.Visible : Visibility.Collapsed;
        card.Remove.IsEnabled = status.State != ModuleState.InUse && !moduleTransferActive && moduleRemovalId == null;
        card.Busy.IsActive = installing || moduleRemovalId == card.Definition.Id;
        card.Busy.Visibility = card.Busy.IsActive ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(card.Install, card.Install.Content + " · " + card.Definition.Name);
    }

    static bool ModuleReady(ModuleStatus status) => status.State is ModuleState.Installed or ModuleState.InUse;
    static string ModuleStateName(ModuleState state) => state switch
    {
        ModuleState.Missing => "Не установлен", ModuleState.Installed => "Готов к работе",
        ModuleState.Corrupt => "Требует восстановления", ModuleState.Unavailable => "Пока недоступен",
        ModuleState.Installing => "Устанавливаем…", ModuleState.InUse => "Используется", _ => "Проверяем…"
    };
    static string ModuleStateHint(ModuleState state) => state switch
    {
        ModuleState.Missing => "Установите модуль, чтобы включить его возможности.",
        ModuleState.Installed => "Возможности модуля доступны в приложении.",
        ModuleState.Corrupt => "Файлы модуля не прошли проверку. Восстановите его перед использованием.",
        ModuleState.Unavailable => "Загрузка пока недоступна для этой версии FlowVPN.",
        ModuleState.InUse => "Удаление будет доступно после завершения работы с модулем.",
        ModuleState.Installing => "Установка продолжится при переходе на другую страницу.", _ => ""
    };
    static string ModuleMetadata(ModuleDefinition module) => "Версия " + module.Version + " · "
        + (module.SizeBytes > 0 ? "Загрузка: " + TransferBytes(module.SizeBytes) : "Размер загрузки пока не указан");

    Task<bool> InstallModuleAsync(string moduleId)
    {
        if (windowClosed || moduleRemovalId != null) return Task.FromResult(false);
        if (moduleInstallTask is { IsCompleted: false })
            return moduleTransferId == moduleId ? moduleInstallTask : Task.FromResult(false);
        var definition = moduleManager.Modules.FirstOrDefault(module => module.Id == moduleId);
        if (definition == null) return Task.FromResult(false);
        moduleInstallCancellation?.Dispose();
        moduleInstallCancellation = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        moduleTransferId = moduleId;
        moduleInstallTask = RunModuleInstallAsync(definition, moduleInstallCancellation.Token);
        return moduleInstallTask;
    }

    async Task<bool> RunModuleInstallAsync(ModuleDefinition definition, CancellationToken token)
    {
        moduleTransferActive = true;
        moduleOperation = BeginOperation(OperationKind.Module, "Установка · " + definition.Name, () => CancelModuleInstall(definition.Id));
        UpdateModuleTransferProgress();
        try
        {
            await moduleManager.InstallAsync(definition.Id, null, token);
            // A late cancellation must not label an already committed installation as cancelled.
            var status = await moduleManager.GetStatusAsync(definition.Id, windowLifetime.Token);
            if (!ModuleReady(status)) throw new InvalidDataException("Module verification did not complete.");
            if (windowClosed) return false;
            moduleStatuses[definition.Id] = status;
            moduleTransferActive = false;
            CompleteModuleTransfer("Модуль «" + definition.Name + "» установлен", "Готов к работе.", InfoBarSeverity.Success, retry: false);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            moduleTransferActive = false;
            if (!windowClosed) CompleteModuleTransfer("Установка отменена", "Вы можете повторить её, когда будет удобно.", InfoBarSeverity.Informational, definition.CanDownload);
            return false;
        }
        catch (Exception exception)
        {
            moduleTransferActive = false;
            if (!windowClosed)
            {
                string message = ModuleErrorText.Describe(exception);
                CompleteModuleTransfer("Не удалось установить «" + definition.Name + "»", message, InfoBarSeverity.Error, definition.CanDownload);
            }
            return false;
        }
        finally
        {
            moduleTransferActive = false;
            if (!windowClosed) await RefreshModulesAsync();
        }
    }

    void CompleteModuleTransfer(string title, string message, InfoBarSeverity severity, bool retry)
    {
        string? moduleId = moduleTransferId;
        CompleteOperation(moduleOperation, title, message, severity, retry && moduleId != null ? async () =>
        {
            if (moduleInstallTask is { IsCompleted: false } pending) await pending;
            await InstallModuleAsync(moduleId);
        } : null);
    }

    void UpdateModuleTransferProgress()
    {
        if (!moduleTransferActive || moduleTransferId == null || windowClosed) return;
        var snapshot = moduleManager.DownloadProgress;
        ReportOperation(moduleOperation, snapshot?.ModuleId == moduleTransferId ? new TransferProgress("Модуль", snapshot.Stage switch
        {
            ModuleProgressStage.Verifying => TransferStage.Verifying,
            ModuleProgressStage.Installing => TransferStage.Applying,
            _ => TransferStage.Downloading
        }, snapshot.BytesReceived, snapshot.TotalBytes > 0 ? snapshot.TotalBytes : null) : new("Модуль", TransferStage.Downloading));
    }

    void CancelModuleInstall(string? moduleId)
    {
        if (!moduleTransferActive || moduleTransferId != moduleId || moduleInstallCancellation == null) return;
        moduleInstallCancellation.Cancel();
    }

    async Task RemoveModuleAsync(ModuleCard card)
    {
        if (dialogOpen || windowClosed || moduleTransferActive || moduleRemovalId != null) return;
        OperationTicket? operation = null;
        bool ownsRemoval = false;
        try
        {
            var current = await moduleManager.GetStatusAsync(card.Definition.Id, windowLifetime.Token);
            if (windowClosed) return;
            if (current.State == ModuleState.InUse) { RenderModuleCard(card, current); return; }
            if (dialogOpen || moduleTransferActive || moduleRemovalId != null) return;
            var result = await Dialog("Удалить модуль «" + card.Definition.Name + "»?",
                new TextBlock { Text = "Настройки сохранятся. Возможности модуля станут доступны после повторной установки.", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, "Удалить");
            if (result != ContentDialogResult.Primary || windowClosed) return;
            if (moduleTransferActive || moduleRemovalId != null) return;
            moduleRemovalId = card.Definition.Id;
            ownsRemoval = true;
            operation = BeginOperation(OperationKind.Module, "Удаление · " + card.Definition.Name, null);
            card.Remove.IsEnabled = false;
            card.Busy.IsActive = true;
            card.Busy.Visibility = Visibility.Visible;
            await moduleManager.RemoveAsync(card.Definition.Id, windowLifetime.Token);
            if (!windowClosed) CompleteOperation(operation.Value, "Модуль «" + card.Definition.Name + "» удалён", "Настройки подключения сохранены.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!windowClosed)
            {
                operation ??= BeginOperation(OperationKind.Module, "Удаление · " + card.Definition.Name, null);
                string message = exception is InvalidOperationException ? "Модуль сейчас используется. Сначала отключите использующее его подключение." : ModuleErrorText.Describe(exception);
                CompleteOperation(operation.Value, "Не удалось удалить модуль", message, InfoBarSeverity.Error, () => RemoveModuleAsync(card));
            }
        }
        finally
        {
            // A request that lost the async race must not release another removal's marker.
            if (ownsRemoval) moduleRemovalId = null;
            if (!windowClosed) await RefreshModulesAsync();
        }
    }

    async Task<bool> EnsureModuleAvailableAsync(string capability)
    {
        var definition = moduleManager.FindByCapability(capability);
        if (definition == null || windowClosed) return false;
        if (moduleInstallTask is { IsCompleted: false } pending)
            return moduleTransferId == definition.Id && await pending;
        var status = await moduleManager.GetStatusAsync(definition.Id, windowLifetime.Token);
        if (windowClosed) return false;
        if (ModuleReady(status)) return true;
        // Import forms use InstallModuleAsync and their own inline status; never stack ContentDialogs.
        if (dialogOpen) return false;
        moduleStatuses[definition.Id] = status;
        var form = new StackPanel { Spacing = 12, MaxWidth = 440 };
        form.Children.Add(new TextBlock { Text = "Для этого подключения нужен модуль «" + definition.Name + "». Установите его, чтобы продолжить.", TextWrapping = TextWrapping.Wrap });
        form.Children.Add(new TextBlock { Text = ModuleMetadata(definition), TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 });
        form.Children.Add(new TextBlock { Text = ModuleStateHint(status.State), TextWrapping = TextWrapping.Wrap });
        var response = await Dialog("Нужен дополнительный модуль", form, status.State == ModuleState.Corrupt ? "Восстановить" : "Установить", configure: dialog =>
        {
            dialog.IsPrimaryButtonEnabled = definition.CanDownload && status.State != ModuleState.Unavailable;
            dialog.DefaultButton = dialog.IsPrimaryButtonEnabled ? ContentDialogButton.Primary : ContentDialogButton.Close;
        });
        // Close the prompt before starting: progress, cancellation and retry remain reachable in the global panel.
        return response == ContentDialogResult.Primary && !windowClosed && await InstallModuleAsync(definition.Id);
    }

    void DisposeModules()
    {
        moduleManager.Changed -= ModuleManagerChanged;
        moduleInstallCancellation?.Cancel();
        moduleInstallCancellation?.Dispose();
        moduleInstallCancellation = null;
    }
}
