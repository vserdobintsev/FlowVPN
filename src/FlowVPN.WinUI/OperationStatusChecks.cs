using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task CheckOperationDismissal()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }

        var saved = BeginOperation(OperationKind.BaseRules, "Проверка уведомлений", null);
        var failed = BeginOperation(OperationKind.Module, "Проверка ошибки", null);
        var successSlot = Operation(OperationKind.BaseRules);
        var errorSlot = Operation(OperationKind.Module);
        try
        {
            CompleteOperation(failed, "Не удалось загрузить модуль", "Файл модуля пока недоступен на сервере выпуска.", InfoBarSeverity.Error, () => Task.CompletedTask);
            CompleteOperation(saved, "Изменения сохранены", "Уведомление закроется автоматически", InfoBarSeverity.Success);
            // The cursor position is external to the isolated visual test.
            successSlot.PointerOver = false;
            errorSlot.PointerOver = false;
            var elapsed = Stopwatch.StartNew();
            ScheduleOperationDismiss(errorSlot);
            ScheduleOperationDismiss(successSlot);
            Check(successSlot.Bar.IsOpen, "Success is initially visible");
            Check(errorSlot.Bar.Content is FrameworkElement { Visibility: Visibility.Collapsed }
                && ReferenceEquals(errorSlot.Bar.ActionButton, errorSlot.Retry) && errorSlot.Retry.Visibility == Visibility.Visible,
                "Completed errors have one native action without an empty progress area");
            var originalSize = AppWindow.Size;
            double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
            try
            {
                await Task.Delay(100);
                await CaptureElement(root, "operation-desktop-completion");
                AppWindow.Resize(new((int)(800 * scale), (int)(620 * scale)));
                await Task.Delay(250);
                root.UpdateLayout(); Responsive(); root.UpdateLayout();
                var retry = VisualBounds(errorSlot.Retry, root);
                Check(retry.Left >= 0 && retry.Right <= root.ActualWidth && retry.Bottom <= root.ActualHeight,
                    "Completed error retry fits the minimum window");
                await CaptureElement(root, "operation-compact-completion");
            }
            finally { AppWindow.Resize(originalSize); }
            while (successSlot.Bar.IsOpen && elapsed.Elapsed < OperationDismissDelay + TimeSpan.FromSeconds(4))
                await Task.Delay(100);
            Check(!successSlot.Bar.IsOpen, "Success closes after the reading interval");
            Check(elapsed.Elapsed >= OperationDismissDelay - TimeSpan.FromMilliseconds(30), "Success is not dismissed early");
            Check(errorSlot.Bar.IsOpen || elapsed.Elapsed >= OperationErrorDismissDelay, "Errors get a longer reading interval");
            Check(!errorSlot.Bar.IsOpen || C<StackPanel>("OperationStatusPanel").Visibility == Visibility.Visible, "Other notifications remain visible");
            while (errorSlot.Bar.IsOpen && elapsed.Elapsed < OperationErrorDismissDelay + TimeSpan.FromSeconds(4))
                await Task.Delay(100);
            Check(!errorSlot.Bar.IsOpen && elapsed.Elapsed >= OperationErrorDismissDelay - TimeSpan.FromMilliseconds(100), "Errors also close automatically after their reading interval");
            Check(C<TextBlock>("WindowCloseHint").Visibility == Visibility.Visible, "The tray hint returns after the last notification closes");

            saved = BeginOperation(OperationKind.BaseRules, "Повторная операция", null);
            CompleteOperation(saved, "Сохранено", "Проверка смены операции", InfoBarSeverity.Success);
            successSlot.PointerOver = false;
            ScheduleOperationDismiss(successSlot);
            BeginOperation(OperationKind.BaseRules, "Новая загрузка", null);
            Check(successSlot.Bar.Content is FrameworkElement { Visibility: Visibility.Visible }, "Starting a new operation restores its progress area");
            await Task.Delay(OperationDismissDelay + TimeSpan.FromMilliseconds(400));
            Check(successSlot.Active && successSlot.Bar.IsOpen, "A previous completion cannot close a new operation");

            CompleteOperation(new(OperationKind.BaseRules, successSlot.Generation), "Сохранено", "Проверка взаимодействия", InfoBarSeverity.Success);
            successSlot.PointerOver = true;
            ScheduleOperationDismiss(successSlot);
            Check(successSlot.DismissDeadline == 0, "Hover pauses automatic dismissal");
            successSlot.PointerOver = false;
            ScheduleOperationDismiss(successSlot);
            Check(successSlot.DismissDeadline > Environment.TickCount64, "Leaving restarts the reading interval");
            successSlot.Bar.IsOpen = false;
            await Task.Delay(100);
            Check(successSlot.DismissDeadline == 0, "Manual close clears automatic dismissal");
            File.WriteAllText(Path.Combine(Store.Root, "design-preview", "operation-dismissal-checks.json"),
                JsonSerializer.Serialize(checks, Store.JsonOptions));
        }
        finally
        {
            foreach (var slot in new[] { successSlot, errorSlot })
            {
                StopOperationDismiss(slot);
                slot.Active = false;
                slot.PointerOver = false;
                slot.Bar.IsOpen = false;
                slot.Bar.Visibility = Visibility.Collapsed;
            }
            C<StackPanel>("OperationStatusPanel").Visibility = Visibility.Collapsed;
            UpdateWindowCloseHint();
        }
    }
}
