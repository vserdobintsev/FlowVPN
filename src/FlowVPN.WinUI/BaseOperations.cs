using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task RunBaseOperation(BaseDomainSelection? selection, bool show, bool catalogOnly = false)
    {
        if (windowClosed || updating || switchingCategory) return;
        bool switching = selection != null;
        bool selectionCommitted = false;
        if (switching) switchingCategory = true; else updating = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        var savedSelection = BaseDomainSelection.From(settings);
        var requested = selection ?? savedSelection;
        var ticket = BeginOperation(OperationKind.BaseRules, switching ? "Подготовка доменных списков" : catalogOnly ? "Загрузка каталога категорий" : "Обновление общей базы", cancellation.Cancel);
        var progress = new Progress<TransferProgress>(value => ReportOperation(ticket, value));
        C<Button>("UpdateRulesButton").IsEnabled = false;
        C<ComboBox>("DomainCategoryCombo").IsEnabled = false;
        BindBaseCategories();
        if (switching) C<TextBlock>("DomainCategoryHint").Text = "Проверяем сохранённые списки…";
        try
        {
            using var client = engine.Running ? engine.Client() : BoundedHttp.Client(TimeSpan.FromSeconds(45));
            bool changed = await Task.Run(async () =>
            {
                if (catalogOnly) { await DomainCategories.Update(client, cancellation.Token, progress); return false; }
                if (switching) return await DomainRepository.Update(client, requested.Categories, cancellation.Token, progress, protectedCategories: savedSelection.Categories);
                bool updated = await RuleRepository.Update(client, cancellation.Token, progress);
                if (requested.Categories.Count > 0) await DomainCategories.Update(client, cancellation.Token, progress);
                return await DomainRepository.Update(client, requested.Categories, cancellation.Token, progress) || updated;
            }, cancellation.Token);
            if (windowClosed) return;
            if (switching)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                requested.ApplyTo(settings);
                try { Store.Save(settings); }
                catch { savedSelection.ApplyTo(settings); throw; }
                selectionCommitted = true;
                Pending();
            }
            UpdateBase();
            CompleteOperation(ticket, switching ? "Выбор доменных списков сохранён" : catalogOnly ? "Каталог категорий обновлён" : "Общая база обновлена",
                switching ? "Новые правила применятся при следующем подключении." : catalogOnly ? "Можно выбрать дополнительные категории." :
                changed ? "Проверенные правила сохранены и применятся при следующем подключении." : "Установлена актуальная версия.",
                show ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!windowClosed)
            {
                UpdateBase();
                CompleteOperation(ticket, switching ? "Применение выбора отменено" : "Обновление отменено",
                    switching ? "Действующий выбор не изменился. Изменения остались на странице базы." : "Уже проверенные файлы сохранены. Можно продолжить обновление позже.",
                    InfoBarSeverity.Informational, () => RunBaseOperation(selection == null ? null : baseDraft ?? selection, show, catalogOnly));
            }
        }
        catch (Exception error)
        {
            if (!windowClosed)
            {
                UpdateBase();
                string message = error is OperationCanceledException ? "Источник не ответил вовремя. Попробуйте ещё раз." : error.Message;
                CompleteOperation(ticket, selectionCommitted ? "Выбор сохранён" : switching ? "Выбор не применён" : "Обновление не завершено",
                    (selectionCommitted ? "Не удалось обновить отображение. " : switching ? "Действующий выбор не изменился. " : "Используются сохранённые проверенные файлы. ") + message,
                    selectionCommitted ? InfoBarSeverity.Warning : InfoBarSeverity.Error,
                    selectionCommitted ? null : () => RunBaseOperation(selection == null ? null : baseDraft ?? selection, show, catalogOnly));
            }
        }
        finally
        {
            if (switching) switchingCategory = false; else updating = false;
            if (!windowClosed)
            {
                C<Button>("UpdateRulesButton").IsEnabled = true;
                C<ComboBox>("DomainCategoryCombo").IsEnabled = true;
                BindBaseCategories();
            }
        }
    }
}
