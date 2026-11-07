namespace FlowVPN;

internal static class BaseRulePreparationChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var calls = new List<string>();
        Task Ip(CancellationToken token) { token.ThrowIfCancellationRequested(); calls.Add("ip"); return Task.CompletedTask; }
        Task Domains(CancellationToken token) { token.ThrowIfCancellationRequested(); calls.Add("domains"); return Task.CompletedTask; }
        foreach (var scenario in new[] { (false, true, true, ""), (true, false, false, ""), (true, true, false, "ip"), (true, false, true, "domains"), (true, true, true, "ip,domains") })
        {
            calls.Clear();
            await BaseRulePreparation.Prepare(scenario.Item1, scenario.Item2, scenario.Item3, Ip, Domains, default);
            check(string.Join(',', calls) == scenario.Item4, "Connection prepares only missing enabled base components: " + scenario);
        }
        calls.Clear();
        bool rejected = false;
        try { await BaseRulePreparation.Prepare(true, true, true, _ => throw new HttpRequestException("offline"), Domains, default); }
        catch (InvalidOperationException error) { rejected = error.InnerException is HttpRequestException && error.Message.Contains("VPN не запущен"); }
        check(rejected && calls.Count == 0, "Failed base preparation stops connection and does not silently disable rules");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool canceled = false;
        try { await BaseRulePreparation.Prepare(true, true, true, Ip, Domains, cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        check(canceled && calls.Count == 0, "Canceled preparation does not download or convert cancellation to a network error");
    }
}
