using System.Diagnostics;

namespace FlowVPN;

internal static class CoreCliCancellationChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var canceled = new CancellationToken(true);
        bool rejected = false;
        try { await CoreCli.Run(canceled, "version"); }
        catch (OperationCanceledException) { rejected = true; }
        check(rejected, "Core CLI rejects an already canceled operation before launching a child");

        rejected = false;
        try { await BrokerPlanBuilder.Create(new(), new(), canceled); }
        catch (OperationCanceledException) { rejected = true; }
        check(rejected, "Canceled broker plan does not read settings or cached rules");

        using (var cancellation = new CancellationTokenSource())
        {
            IEnumerable<string> Domains()
            {
                yield return "first.example";
                cancellation.Cancel();
                yield return "second.example";
                throw new InvalidOperationException("Domain normalization continued after cancellation.");
            }
            rejected = false;
            try { _ = DomainCoverage.Effective(Domains(), null, cancellation.Token); }
            catch (OperationCanceledException) { rejected = true; }
            check(rejected, "Domain normalization observes cancellation while enumerating rules");
        }

        // This core has no inbounds, API, TUN, DNS transport or outbound requests.
        // Retain its original process handle to distinguish exit from PID reuse.
        string file = Path.Combine(Store.Run, "core-cancellation-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(file, """
            {"log":{"disabled":true},"outbounds":[{"type":"direct","tag":"direct"}],"route":{"final":"direct"}}
            """);
        var baseline = new HashSet<int>();
        foreach (var existing in Process.GetProcessesByName("sing-box"))
        {
            using (existing) baseline.Add(existing.Id);
        }
        using var stop = new CancellationTokenSource();
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Process? child = null;
        var run = CoreCli.Run(stop.Token, "run", "-c", file);
        try
        {
            while (child == null)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (run.IsCompleted)
                {
                    await run;
                    throw new InvalidOperationException("Cancellation fixture exited before the stop request.");
                }
                foreach (var candidate in Process.GetProcessesByName("sing-box"))
                {
                    if (child == null && !baseline.Contains(candidate.Id) &&
                        string.Equals(Native.ProcessPath(candidate.Id), Store.Bin, StringComparison.OrdinalIgnoreCase))
                        child = candidate;
                    else candidate.Dispose();
                }
                if (child == null) await Task.Delay(25, startup.Token);
            }
            check(!child.HasExited, "Cancellation fixture starts the actual pinned core without TUN or traffic");
            stop.Cancel();
            rejected = false;
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { rejected = true; }
            check(rejected && child.HasExited, "Core CLI returns cancellation only after its real child has exited");
            string version = await CoreCli.Run("version");
            check(version.Contains(UpdateProtocol.ExpectedCoreVersion()), "A completed cancellation releases the core lease for the next command");
        }
        finally
        {
            stop.Cancel();
            try { await run; }
            catch (OperationCanceledException) { }
            finally
            {
                child?.Dispose();
                File.Delete(file);
            }
        }
    }
}
