using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using Windows.Foundation;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    Rect VisualBounds(FrameworkElement element, UIElement relative)
    {
        var point = element.TransformToVisual(relative).TransformPoint(new Point());
        return new(point.X, point.Y, element.ActualWidth, element.ActualHeight);
    }

    async Task CheckOperationLayout(double scale, string folder)
    {
        var reports = new List<object>();
        int previousPage = page;
        var previousTheme = root.RequestedTheme;
        bool? previousMotion = motion.TestEnabled;
        motion.TestEnabled = false;
        void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException("Operation layout: " + label); }
        try
        {
            foreach (var spec in new[] { (Name: "status-desktop", Width: 1180, Height: 840, Theme: ElementTheme.Light), (Name: "status-compact", Width: 800, Height: 620, Theme: ElementTheme.Dark) })
            {
                AppWindow.Resize(new((int)(spec.Width * scale), (int)(spec.Height * scale)));
                root.RequestedTheme = spec.Theme;
                SelectPage(1);
                var module = BeginOperation(OperationKind.Module, "Установка дополнительного модуля для выбранных возможностей приложения", () => { });
                var rules = BeginOperation(OperationKind.BaseRules, "Проверка общей базы и дополнительных доменных категорий", () => { });
                ReportOperation(module, new("Модуль · проверка длинной подписи загрузки", TransferStage.Downloading, 25_000_000, 50_000_000));
                ReportOperation(rules, new("Объединённые доменные списки для всех профилей без повторяющихся адресов", TransferStage.Verifying));
                await Task.Delay(350);
                root.UpdateLayout(); Responsive();
                await Task.Delay(150);
                root.UpdateLayout();
                var main = C<ScrollViewer>("MainScroll");
                var card = VisualBounds(C<Border>("BasePage"), root);
                var panel = VisualBounds(C<StackPanel>("OperationStatusPanel"), root);
                var viewport = VisualBounds(main, root);
                Require(C<Grid>("MainArea").ActualHeight >= main.ViewportHeight - 1, spec.Name + " workspace fills the viewport or scrolls as one page");
                Require(main.ScrollableWidth <= 1, spec.Name + " no horizontal page scrolling");
                Require(card.Height > 0 && main.ViewportHeight > 120, spec.Name + " page keeps a usable scroll viewport above operations");
                Require(panel.Top >= viewport.Bottom + 3 && panel.Bottom <= root.ActualHeight - 11, spec.Name + " panel has a separate row and outer gap");
                Require(C<TextBlock>("WindowCloseHint").Visibility == Visibility.Collapsed, spec.Name + " tray hint hides while operations are visible");
                var measured = new List<object>();
                foreach (var kind in new[] { OperationKind.Module, OperationKind.BaseRules })
                {
                    var slot = operationSlots[kind];
                    var bar = VisualBounds(slot.Bar, root);
                    var progress = VisualBounds(slot.Progress, root);
                    var detail = VisualBounds(slot.Detail, root);
                    var cancel = VisualBounds(slot.Cancel, root);
                    Require(Math.Abs(bar.Left - card.Left) <= 1 && Math.Abs(bar.Right - card.Right) <= 1, spec.Name + " shared horizontal card edges");
                    Require(progress.Width > 0 && progress.Right <= bar.Right - 8 && progress.Bottom <= bar.Bottom - 8, spec.Name + " progress has inner right and bottom padding");
                    Require(detail.Width > 0 && detail.Right <= bar.Right - 8 && cancel.Bottom <= bar.Bottom - 8, spec.Name + " text and cancel stay inside the bar");
                    Require(slot.Bar.CornerRadius.TopLeft == 16, spec.Name + " status corner radius matches cards");
                    measured.Add(new { Kind = kind.ToString(), Bar = bar, Progress = progress, Detail = detail, Cancel = cancel });
                }
                double height = C<Grid>("MainArea").ActualHeight;
                await Task.Delay(150);
                root.UpdateLayout();
                Require(Math.Abs(height - C<Grid>("MainArea").ActualHeight) <= .5, spec.Name + " repeated layout is stable");
                await CaptureElement(root, spec.Name);
                reports.Add(new { spec.Name, MainViewport = viewport, Card = card, Panel = panel, Slots = measured, OuterScrollableHeight = main.ScrollableHeight });
                CompleteOperation(module, "Модуль установлен", "Готов к работе.", InfoBarSeverity.Success);
                operationSlots[OperationKind.Module].Bar.IsOpen = false;
                await Task.Delay(150);
                root.UpdateLayout();
                Require(C<StackPanel>("OperationStatusPanel").Visibility == Visibility.Visible && operationSlots[OperationKind.BaseRules].Bar.IsOpen,
                    spec.Name + " closing one completion leaves the other operation visible");
                Require(main.ActualHeight > viewport.Height, spec.Name + " closing a status returns space to the viewport");
                Require(C<TextBlock>("WindowCloseHint").Visibility == Visibility.Collapsed, spec.Name + " tray hint stays hidden until the final operation closes");
                CompleteOperation(rules, "База обновлена", "Правила сохранены.", InfoBarSeverity.Success);
                operationSlots[OperationKind.BaseRules].Bar.IsOpen = false;
                await Task.Delay(150);
                Require(C<TextBlock>("WindowCloseHint").Visibility == Visibility.Visible, spec.Name + " tray hint returns when operations close");
            }
        }
        finally
        {
            foreach (var kind in new[] { OperationKind.Module, OperationKind.BaseRules })
                if (operationSlots.TryGetValue(kind, out var slot)) { slot.Active = false; slot.CancelAction = null; slot.RetryAction = null; slot.Bar.IsOpen = false; }
            motion.TestEnabled = previousMotion;
            AppWindow.Resize(new((int)(1180 * scale), (int)(840 * scale)));
            root.RequestedTheme = previousTheme;
            SelectPage(previousPage);
            await Task.Delay(150);
        }
        File.WriteAllText(Path.Combine(folder, "operation-layout-checks.json"), JsonSerializer.Serialize(reports, Store.JsonOptions));
        await CheckOperationDismissal();
    }

    async Task CheckDeveloperIdentityLayout(double scale, string folder)
    {
        var reports = new List<object>();
        int previousPage = page;
        var previousTheme = root.RequestedTheme;
        var name = C<TextBlock>("DeveloperName");
        var identity = C<StackPanel>("DeveloperIdentity");
        string originalName = name.Text;
        double originalMaxWidth = identity.MaxWidth;
        var originalAlignment = identity.HorizontalAlignment;
        try
        {
            foreach (var spec in new[] { (Name: "identity-desktop", Width: 1180, Height: 840, LongName: false), (Name: "identity-minimum", Width: 800, Height: 620, LongName: true) })
            {
                AppWindow.Resize(new((int)(spec.Width * scale), (int)(spec.Height * scale)));
                root.RequestedTheme = spec.LongName ? ElementTheme.Dark : ElementTheme.Light;
                identity.MaxWidth = spec.LongName ? 180 : originalMaxWidth;
                identity.HorizontalAlignment = HorizontalAlignment.Left;
                name.Text = spec.LongName ? "Владислав Александрович Сердобинцев" : originalName;
                SelectPage(7);
                await Task.Delay(300);
                root.UpdateLayout();
                var row = C<Grid>("DeveloperIdentityRow");
                var avatar = VisualBounds(C<PersonPicture>("DeveloperAvatar"), row);
                var labels = VisualBounds(identity, row);
                var handle = VisualBounds(C<TextBlock>("DeveloperHandle"), identity);
                double centers = Math.Abs(avatar.Y + avatar.Height / 2 - labels.Y - labels.Height / 2);
                if (centers > 1 / scale + .01 || labels.Y < -.5 || labels.Bottom > row.ActualHeight + .5)
                    throw new InvalidOperationException("Developer avatar and identity centers do not align: " + spec.Name);
                if (handle.Height <= 0 || handle.Bottom > identity.ActualHeight + .5 ||
                    spec.LongName && (name.ActualHeight <= name.FontSize * 1.5 || identity.ActualHeight <= avatar.Height))
                    throw new InvalidOperationException("Developer identity is clipped or does not wrap: " + spec.Name);
                reports.Add(new { spec.Name, CenterDifference = centers, RowHeight = row.ActualHeight, Avatar = avatar, Identity = labels, NameHeight = name.ActualHeight });
                // Capture the composed page: an isolated transparent row loses its theme backdrop.
                await CaptureElement(root, spec.Name);
            }
        }
        finally
        {
            name.Text = originalName;
            identity.MaxWidth = originalMaxWidth;
            identity.HorizontalAlignment = originalAlignment;
            root.RequestedTheme = previousTheme;
            AppWindow.Resize(new((int)(1180 * scale), (int)(840 * scale)));
            SelectPage(previousPage);
            await Task.Delay(150);
        }
        File.WriteAllText(Path.Combine(folder, "developer-identity-layout-checks.json"), JsonSerializer.Serialize(reports, Store.JsonOptions));
    }
}
