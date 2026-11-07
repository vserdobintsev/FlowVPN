using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task CheckWorkspaceLayout(double scale, string folder)
    {
        int previousPage = page;
        var reports = new List<object>();
        var operation = BeginOperation(OperationKind.BaseRules, "Проверка общей сетки", null);
        try
        {
            foreach (int width in new[] { 800, 1600, 2200 })
            {
                AppWindow.Resize(new((int)(width * scale), (int)(840 * scale)));
                foreach (var destination in Pages)
                {
                    SelectPage(destination.Id);
                    await Task.Delay(120);
                    root.UpdateLayout(); Responsive(); root.UpdateLayout();
                    var scroll = C<ScrollViewer>("MainScroll");
                    var main = C<Grid>("MainArea");
                    var mainBounds = VisualBounds(main, root);
                    var scrollBounds = VisualBounds(scroll, root);
                    var statusBounds = VisualBounds(C<StackPanel>("OperationStatusPanel"), root);
                    var pageBounds = VisualBounds(C<FrameworkElement>(destination.Element), root);
                    double expectedWidth = Math.Min(WorkspaceContentWidth, scroll.ViewportWidth);
                    if (Math.Abs(mainBounds.Width - expectedWidth) > 1 ||
                        Math.Abs(mainBounds.Left + mainBounds.Width / 2 - scrollBounds.Left - scrollBounds.Width / 2) > 1)
                        throw new InvalidOperationException("Workspace content is not capped and centered: " + destination.Element);
                    if (Math.Abs(statusBounds.Left - mainBounds.Left - main.Padding.Left) > 1 ||
                        Math.Abs(mainBounds.Right - main.Padding.Right - statusBounds.Right) > 1 ||
                        Math.Abs(pageBounds.Left - statusBounds.Left) > 1 || Math.Abs(pageBounds.Right - statusBounds.Right) > 1)
                        throw new InvalidOperationException("Workspace cards and status do not share edges: " + destination.Element);
                    if (scroll.ScrollableWidth > 1)
                        throw new InvalidOperationException("Workspace adds horizontal overflow: " + destination.Element);
                    if (destination.Id is not 3 && C<FrameworkElement>(destination.Element) is Border { Child: ScrollViewer pageScroll }
                        && (pageScroll.ScrollableHeight > 1 || pageScroll.ExtentHeight > pageScroll.ViewportHeight + 1))
                        throw new InvalidOperationException("Page clips its content inside a nested scroll area: " + destination.Element);
                    reports.Add(new { RequestedWidth = width, Page = destination.Element, Viewport = scrollBounds, Content = mainBounds, Status = statusBounds });
                    if (width == 1600 && destination.Id is 4 or 6 or 9)
                        await CaptureElement(root, "workspace-" + destination.Element);
                }
            }
            File.WriteAllText(Path.Combine(folder, "workspace-layout-checks.json"), JsonSerializer.Serialize(reports, Store.JsonOptions));
        }
        finally
        {
            CompleteOperation(operation, "Проверка завершена", "", InfoBarSeverity.Success);
            Operation(OperationKind.BaseRules).Bar.IsOpen = false;
            AppWindow.Resize(new((int)(1180 * scale), (int)(840 * scale)));
            SelectPage(previousPage);
            await Task.Delay(150);
        }
    }
}
