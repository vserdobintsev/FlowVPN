namespace FlowVPN;

internal static class BaseRulePreparation
{
    public static async Task Ensure(Settings settings, CancellationToken token, IProgress<TransferProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (!settings.BaseRulesEnabled) return;
        var categories = BaseDomainSelection.From(settings).Categories;
        bool missingIp = !File.Exists(Path.Combine(Store.Rules, "ru-blocked.srs"));
        bool missingDomains = categories.Count > 0 && !File.Exists(DomainRepository.RulePath(categories));
        if (!missingIp && !missingDomains) return;
        using var client = BoundedHttp.Client(TimeSpan.FromSeconds(45));
        await Prepare(settings.BaseRulesEnabled, missingIp, missingDomains,
            async cancellation => { await RuleRepository.Update(client, cancellation, progress); },
            async cancellation => { await DomainRepository.Update(client, categories, cancellation, progress); }, token);
    }

    internal static async Task Prepare(bool enabled, bool missingIp, bool missingDomains,
        Func<CancellationToken, Task> prepareIp, Func<CancellationToken, Task> prepareDomains, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!enabled) return;
        try
        {
            if (missingIp) await prepareIp(token);
            if (missingDomains) await prepareDomains(token);
        }
        catch (DownloadInterruptedException) { throw; }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new IOException("Сервер общей базы не ответил вовремя. Повторите подключение для продолжения загрузки.", error); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException)
        {
            throw new InvalidOperationException("Не удалось подготовить общую базу для подключения. Проверьте интернет и повторите попытку или обновите базу в настройках. VPN не запущен.", error);
        }
    }
}
