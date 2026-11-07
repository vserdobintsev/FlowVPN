using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    enum OperationKind { Module, BaseRules }
    readonly record struct OperationTicket(OperationKind Kind, int Generation);
    sealed class OperationSlot(InfoBar bar, ProgressBar progress, TextBlock detail, Button cancel, Button retry)
    {
        public readonly InfoBar Bar = bar;
        public readonly ProgressBar Progress = progress;
        public readonly TextBlock Detail = detail;
        public readonly Button Cancel = cancel, Retry = retry;
        public int Generation;
        public bool Active, Cancelling;
        public Action? CancelAction;
        public Func<Task>? RetryAction;
        public DispatcherQueueTimer? DismissTimer;
        public long DismissDeadline;
        public int DismissGeneration;
        public bool PointerOver;
    }
    // Two fixed slots: concurrent base/module operations cannot overwrite each other.
    readonly Dictionary<OperationKind, OperationSlot> operationSlots = [];
    static readonly TimeSpan OperationDismissDelay = TimeSpan.FromSeconds(8);
    static readonly TimeSpan OperationErrorDismissDelay = TimeSpan.FromSeconds(12);
    static string TransferBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} МиБ" : bytes >= 1024 ? $"{bytes / 1024d:0.#} КиБ" : $"{bytes} Б";

    OperationSlot Operation(OperationKind kind)
    {
        if (operationSlots.TryGetValue(kind, out var existing)) return existing;
        if (operationSlots.Count == 0)
            C<ScrollViewer>("MainScroll").SizeChanged += (_, _) => Responsive();
        OperationSlot slot;
        if (kind == OperationKind.Module)
            slot = new(C<InfoBar>("ModuleTransferStatus"), C<ProgressBar>("ModuleTransferProgress"), C<TextBlock>("ModuleTransferDetails"), C<Button>("CancelModuleTransfer"), C<Button>("RetryModuleTransfer"));
        else
        {
            var bar = new InfoBar { IsOpen = false, Visibility = Visibility.Collapsed };
            var progress = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true };
            var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var cancel = new Button { Content = "Отменить" };
            var retry = new Button { Content = "Повторить", Visibility = Visibility.Collapsed };
            var layout = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
            layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetColumnSpan(progress, 2);
            Grid.SetRow(detail, 1);
            Grid.SetRow(cancel, 1); Grid.SetColumn(cancel, 1);
            layout.Children.Add(progress); layout.Children.Add(detail); layout.Children.Add(cancel);
            bar.Content = layout;
            C<StackPanel>("OperationStatusPanel").Children.Add(bar);
            slot = new(bar, progress, detail, cancel, retry);
        }
        slot.Bar.CornerRadius = new(16);
        slot.Bar.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        if (slot.Bar.Content is Grid content)
        {
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            content.Margin = new Thickness(0, 4, 16, 12);
        }
        slot.Bar.ActionButton = slot.Retry;
        operationSlots.Add(kind, slot);
        slot.Bar.Closed += (_, _) =>
        {
            StopOperationDismiss(slot);
            if (!slot.Active) slot.Bar.Visibility = Visibility.Collapsed;
            C<StackPanel>("OperationStatusPanel").Visibility = operationSlots.Values.Any(s => s.Bar.IsOpen) ? Visibility.Visible : Visibility.Collapsed;
            UpdateWindowCloseHint();
        };
        slot.Bar.SizeChanged += (_, _) => Responsive();
        slot.Bar.PointerEntered += (_, _) => { slot.PointerOver = true; StopOperationDismiss(slot); };
        slot.Bar.PointerExited += (_, _) => { slot.PointerOver = false; ScheduleOperationDismiss(slot); };
        slot.Bar.GotFocus += (_, _) => StopOperationDismiss(slot);
        slot.Bar.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() => ScheduleOperationDismiss(slot));
        slot.Cancel.Click += (_, _) => CancelOperation(slot);
        slot.Retry.Click += async (_, _) =>
        {
            var retry = slot.RetryAction;
            if (slot.Active || retry == null || windowClosed) return;
            slot.Retry.IsEnabled = false;
            try { await retry(); }
            finally { if (!windowClosed) slot.Retry.IsEnabled = true; }
        };
        return slot;
    }

    OperationTicket BeginOperation(OperationKind kind, string title, Action? cancel)
    {
        var slot = Operation(kind);
        StopOperationDismiss(slot);
        slot.Generation++;
        slot.Active = true; slot.Cancelling = false;
        slot.CancelAction = cancel; slot.RetryAction = null;
        slot.Bar.Title = title; slot.Bar.Message = "";
        slot.Bar.Severity = InfoBarSeverity.Informational;
        slot.Bar.IsClosable = false; slot.Bar.IsOpen = true; slot.Bar.Visibility = Visibility.Visible;
        C<StackPanel>("OperationStatusPanel").Visibility = Visibility.Visible;
        UpdateWindowCloseHint();
        if (slot.Bar.Content is FrameworkElement content) content.Visibility = Visibility.Visible;
        slot.Progress.IsIndeterminate = true; slot.Progress.Visibility = Visibility.Visible;
        slot.Detail.Text = "Подготавливаем…"; slot.Detail.Visibility = Visibility.Visible;
        slot.Cancel.Visibility = cancel != null ? Visibility.Visible : Visibility.Collapsed;
        slot.Cancel.IsEnabled = true; slot.Retry.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(slot.Progress, title);
        return new(kind, slot.Generation);
    }

    void ReportOperation(OperationTicket ticket, TransferProgress report)
    {
        if (windowClosed || !operationSlots.TryGetValue(ticket.Kind, out var slot) || slot.Generation != ticket.Generation || !slot.Active || slot.Cancelling) return;
        bool known = report.Stage == TransferStage.Downloading && report.TotalBytes is > 0;
        slot.Progress.IsIndeterminate = !known;
        if (known) slot.Progress.Value = Math.Clamp(report.BytesReceived / (double)report.TotalBytes!.Value * 100, 0, 100);
        slot.Detail.Text = report.Stage switch
        {
            TransferStage.Verifying => "Проверяем · " + report.Item,
            TransferStage.Applying => "Сохраняем · " + report.Item,
            _ => report.Item + " · " + (known ? $"{TransferBytes(report.BytesReceived)} из {TransferBytes(report.TotalBytes!.Value)} · {slot.Progress.Value:0}%" :
                report.BytesReceived > 0 ? $"{TransferBytes(report.BytesReceived)} загружено" : "загрузка…")
        };
    }

    void CompleteOperation(OperationTicket ticket, string title, string message, InfoBarSeverity severity, Func<Task>? retry = null)
    {
        if (windowClosed || !operationSlots.TryGetValue(ticket.Kind, out var slot) || slot.Generation != ticket.Generation) return;
        StopOperationDismiss(slot);
        slot.Active = false;
        slot.CancelAction = null; slot.RetryAction = retry;
        slot.Bar.Title = title; slot.Bar.Message = message; slot.Bar.Severity = severity; slot.Bar.IsClosable = true;
        slot.Progress.Visibility = slot.Detail.Visibility = slot.Cancel.Visibility = Visibility.Collapsed;
        if (slot.Bar.Content is FrameworkElement content) content.Visibility = Visibility.Collapsed;
        slot.Retry.Visibility = retry == null ? Visibility.Collapsed : Visibility.Visible;
        slot.Retry.IsEnabled = true;
        ScheduleOperationDismiss(slot);
    }

    bool HasOperationFocus(OperationSlot slot)
    {
        if (root.XamlRoot == null) return false;
        for (var element = FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject;
            element != null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, slot.Bar)) return true;
        return false;
    }

    bool CanDismissOperation(OperationSlot slot) => !windowClosed && !slot.Active && slot.Bar.IsOpen
        && !slot.PointerOver && !HasOperationFocus(slot);

    void UpdateWindowCloseHint() => C<TextBlock>("WindowCloseHint").Visibility =
        C<StackPanel>("OperationStatusPanel").Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    static void StopOperationDismiss(OperationSlot slot)
    {
        slot.DismissTimer?.Stop();
        slot.DismissDeadline = 0;
    }

    void ScheduleOperationDismiss(OperationSlot slot)
    {
        StopOperationDismiss(slot);
        if (!CanDismissOperation(slot)) return;
        if (slot.DismissTimer == null)
        {
            var timer = DispatcherQueue.CreateTimer();
            slot.DismissTimer = timer;
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                if (slot.DismissDeadline == 0 || slot.DismissGeneration != slot.Generation || !CanDismissOperation(slot))
                {
                    StopOperationDismiss(slot);
                    return;
                }
                // A queued tick from a previous run must not close a newer notification early.
                long remaining = slot.DismissDeadline - Environment.TickCount64;
                if (remaining > 0)
                {
                    timer.Interval = TimeSpan.FromMilliseconds(remaining);
                    timer.Start();
                    return;
                }
                StopOperationDismiss(slot);
                slot.Bar.IsOpen = false;
            };
        }
        var delay = slot.Bar.Severity is InfoBarSeverity.Error or InfoBarSeverity.Warning ? OperationErrorDismissDelay : OperationDismissDelay;
        slot.DismissGeneration = slot.Generation;
        slot.DismissDeadline = Environment.TickCount64 + (long)delay.TotalMilliseconds;
        slot.DismissTimer.Interval = delay;
        slot.DismissTimer.Start();
    }

    void DisposeOperations()
    {
        foreach (var slot in operationSlots.Values)
        {
            StopOperationDismiss(slot);
            slot.CancelAction = null;
            slot.RetryAction = null;
        }
    }

    static void CancelOperation(OperationSlot slot)
    {
        if (!slot.Active || slot.Cancelling) return;
        slot.Cancelling = true;
        slot.Cancel.IsEnabled = false;
        slot.Detail.Text = "Отменяем…";
        slot.Progress.IsIndeterminate = true;
        slot.CancelAction?.Invoke();
    }
}
