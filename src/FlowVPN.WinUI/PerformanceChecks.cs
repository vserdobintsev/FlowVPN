using System.Diagnostics;
using System.Text.Json;

namespace FlowVPN;

internal static class PerformanceChecks
{
    public static void Run()
    {
        var results = new List<object>();
        foreach (int size in new[] { 10000, 100000 })
        {
            var baseline = new DomainCoverage(Enumerable.Range(0, size / 2).Select(i => $"site{i}.example"));
            var input = Enumerable.Range(0, size).SelectMany(i => new[] { $"SITE{i}.example", $"sub.site{i}.example" }).ToArray();
            _ = DomainCoverage.Effective(input.Take(100), baseline);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            var result = DomainCoverage.Effective(input, baseline);
            timer.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (result.Count != size / 2)
                throw new InvalidOperationException("Domain benchmark changed routing semantics");
            results.Add(new
            {
                InputDomains = input.Length,
                BaseDomains = size / 2,
                OutputDomains = result.Count,
                Milliseconds = timer.Elapsed.TotalMilliseconds,
                AllocatedBytes = allocated
            });
        }
        var appResults = new List<object>();
        ApplicationGroups.Add(new VpnProfile(), [AppRule.File(@"D:\Warmup\app.exe")]);
        foreach (int size in new[] { 1000, 10000 })
        {
            var profile = new VpnProfile { Apps = Enumerable.Range(0, size).Select(i => AppRule.File($@"D:\App{i}\app.exe")).ToList() };
            var input = Enumerable.Range(size / 2, size).Select(i => AppRule.File($@"D:\App{i}\app.exe")).ToArray();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            ApplicationGroups.Add(profile, input);
            timer.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (profile.Apps.Count != size + size / 2)
                throw new InvalidOperationException("Rule merge benchmark changed duplicate semantics");
            appResults.Add(new
            {
                ExistingRules = size,
                InputRules = input.Length,
                OutputRules = profile.Apps.Count,
                Milliseconds = timer.Elapsed.TotalMilliseconds,
                AllocatedBytes = allocated
            });
        }
        var processes = Enumerable.Range(0, 250).SelectMany(i => Enumerable.Range(0, 10).Select(j =>
            new ProcessSnapshot(10000 + i * 10 + j, $@"D:\Program{i}\program{i}.exe", $"Program {i}", $"Vendor {i}", "1.0", $"Document {j}", j == 0))).ToArray();
        var inventory = ProcessCatalog.Group(processes, [], @"C:\Windows");
        string[] queries = ["Vendor 249", "Program 249", "program249.exe", "12490"];
        _ = ProcessCatalog.Filter(inventory, ProcessFilter.All, "warmup");
        long processBefore = GC.GetAllocatedBytesForCurrentThread();
        var searchTimer = Stopwatch.StartNew();
        int matches = 0;
        for (int i = 0; i < 60; i++)
            matches += ProcessCatalog.Filter(inventory, ProcessFilter.All, queries[i % queries.Length]).Count;
        searchTimer.Stop();
        long processAllocated = GC.GetAllocatedBytesForCurrentThread() - processBefore;
        if (matches != 60)
            throw new InvalidOperationException("Process search benchmark changed search results");
        Store.Atomic(Path.Combine(Store.Data, "performance-checks.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Runtime = Environment.Version.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            Results = results,
            ApplicationRules = appResults,
            ProcessSearch = new
            {
                Processes = processes.Length,
                Applications = inventory.Count,
                Queries = 60,
                Milliseconds = searchTimer.Elapsed.TotalMilliseconds,
                AllocatedBytes = processAllocated
            }
        }, Store.JsonOptions));
    }
}
