using System.Net;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task CheckModuleCards(double scale, string folder)
    {
        if (moduleTransferActive || moduleRemovalId != null) throw new InvalidOperationException("Module visual checks require idle operations");
        var definition = moduleManager.Modules.First();
        var body = C<StackPanel>("ModulesBody");
        var originalChildren = body.Children.ToArray();
        int originalPage = page;
        string? originalTransferId = moduleTransferId;
        var reports = new List<object>();
        var slot = Operation(OperationKind.Module);
        try
        {
            SelectPage(9);
            // Use a detached fixture card: no downloads, module installation or real module files.
            body.Children.Clear();
            foreach (var heading in originalChildren.Take(2)) body.Children.Add(heading);
            var card = new ModuleCard(definition);
            var view = CreateModuleCard(card);
            body.Children.Add(view);
            foreach (int width in new[] { 800, 1180 })
            {
                AppWindow.Resize(new((int)(width * scale), (int)((width == 800 ? 620 : 840) * scale)));
                foreach (string state in new[] { "missing", "installing", "error", "installed" })
                {
                    moduleTransferActive = state == "installing";
                    moduleTransferId = definition.Id;
                    RenderModuleCard(card, new(definition, ModuleState.Missing));
                    if (state is "installing" or "error")
                    {
                        var operation = BeginOperation(OperationKind.Module, "Установка · " + definition.Name, () => { });
                        if (state == "installing")
                            ReportOperation(operation, new("Модуль", TransferStage.Downloading, 1024, definition.SizeBytes));
                        else CompleteOperation(operation, "Не удалось установить модуль",
                            ModuleErrorText.Describe(new HttpRequestException(null, null, HttpStatusCode.NotFound)), InfoBarSeverity.Error, () => Task.CompletedTask);
                    }
                    else { StopOperationDismiss(slot); slot.Bar.IsOpen = false; slot.Bar.Visibility = Visibility.Collapsed; }
                    RenderModuleCard(card, new(definition, state == "installing" ? ModuleState.Installing : state == "installed" ? ModuleState.Installed : ModuleState.Missing));
                    C<ScrollViewer>("MainScroll").ChangeView(null, 0, null, true);
                    await Task.Delay(200);
                    root.UpdateLayout(); Responsive(); root.UpdateLayout();
                    if (VisualDescendants<ProgressBar>(view).Any() || VisualDescendants<InfoBar>(view).Any()
                        || VisualDescendants<Button>(view).Count() != 2 || card.Busy.IsActive != (state == "installing"))
                        throw new InvalidOperationException("Module card duplicates operation feedback: " + state);
                    if (state == "installing" && (card.Install.IsEnabled || card.State.Text != "Не установлен" || slot.Cancel.Visibility != Visibility.Visible)
                        || state == "error" && (!card.Install.IsEnabled || slot.Retry.Visibility != Visibility.Visible || slot.Bar.ActionButton != slot.Retry)
                        || state == "installed" && (card.Install.Visibility != Visibility.Collapsed || !card.Remove.IsEnabled))
                        throw new InvalidOperationException("Module card/action state is inconsistent: " + state);
                    var action = state == "installed" ? card.Remove : card.Install;
                    await CaptureElement(root, "module-card-" + width + "-" + state);
                    action.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                    await Task.Delay(100);
                    root.UpdateLayout();
                    var bounds = VisualBounds(action, root);
                    if (bounds.Left < -1 || bounds.Right > root.ActualWidth + 1 || bounds.Top < -1 || bounds.Bottom > root.ActualHeight + 1)
                        throw new InvalidOperationException("Module action is unreachable: " + width + " / " + state);
                    if (C<ScrollViewer>("MainScroll").VerticalOffset > 1)
                        await CaptureElement(root, "module-card-" + width + "-" + state + "-action");
                    reports.Add(new { Width = width, State = state, NoDuplicateProgress = true, NoCardError = true, ActionVisible = true });
                }
            }
            File.WriteAllText(Path.Combine(folder, "module-card-checks.json"), JsonSerializer.Serialize(new { Passed = reports.Count, Checks = reports }, Store.JsonOptions));
        }
        finally
        {
            moduleTransferActive = false;
            moduleTransferId = originalTransferId;
            StopOperationDismiss(slot);
            slot.Active = false; slot.CancelAction = null; slot.RetryAction = null;
            slot.Bar.IsOpen = false; slot.Bar.Visibility = Visibility.Collapsed;
            body.Children.Clear();
            foreach (var child in originalChildren) body.Children.Add(child);
            AppWindow.Resize(new((int)(1180 * scale), (int)(840 * scale)));
            SelectPage(originalPage);
        }
    }
}
