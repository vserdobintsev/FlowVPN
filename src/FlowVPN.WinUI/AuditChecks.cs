using System.Text.Json;

namespace FlowVPN;

internal static class AuditChecks
{
    public static void Run(Action<bool, string> check)
    {
        var rule = AppRule.File(@"E:\App\engine.exe", "Engine");
        rule.GroupId = "preset:old";
        var replacement = AppRule.File(rule.Path, "Extension");
        replacement.GroupId = "preset:new";
        replacement.GroupName = "Extension · Host";
        replacement.ScopeNote = "Exact engine only";
        List<DetectedPreset> catalog = [new(new() { Id = "new", LegacyGroupIds = ["preset:old"] }, [new(replacement, replacement.Path)])];
        string saved = JsonSerializer.Serialize(rule);
        var group = ApplicationGroups.Create([rule], catalog);
        check(JsonSerializer.Serialize(rule) == saved && ReferenceEquals(group.Single().Rules.Single(), rule), "Rendering groups does not mutate rules and preserves identity for removal");
        check(CatalogRuleMigration.Apply([rule], catalog) && !CatalogRuleMigration.Apply([rule], catalog), "Catalogue metadata migration is explicit and idempotent");
        check(rule.Pattern == replacement.Pattern && rule.Path == replacement.Path, "Catalogue migration preserves saved network scope");
        var one = new VpnProfile();
        var two = new VpnProfile();
        ApplicationGroups.Add(one, [rule]);
        ApplicationGroups.Add(two, [rule]);
        one.Apps[0].Name = "Edited";
        check(two.Apps[0].Name == rule.Name && !ReferenceEquals(one.Apps[0], two.Apps[0]), "Adding a preset gives each profile its own independent rule objects");
        var repeated = rule.Copy();
        repeated.Pattern = repeated.Pattern.ToUpperInvariant();
        repeated.GroupName = "Updated group";
        ApplicationGroups.Add(two, [rule, repeated, repeated]);
        check(two.Apps.Count == 1 && two.Apps[0].GroupName == "Updated group", "Rule merge deduplicates case-insensitively across existing and incoming batches while updating metadata");
        string[] args = ["--root", @"E:\Users\Другой пользователь\Data", "--custom", "a\"b"];
        var start = Entry.ElevatedStart(@"E:\FlowVPN\FlowVPN.exe", @"E:\Data", args);
        check(start.ArgumentList.SequenceEqual(args) && start.Verb == "runas", "Elevation preserves argument boundaries, Unicode, quotes and a custom data root");
        string directory = Path.Combine(Store.Run, "atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "state.dat");
            Store.Atomic(path, [1, 2, 3]);
            Store.Atomic(path, [4, 5]);
            check(File.ReadAllBytes(path).SequenceEqual(new byte[] { 4, 5 }) && Directory.GetFiles(directory).Length == 1, "Atomic replacement leaves only the complete final file");
            string blocked = Path.Combine(directory, "directory");
            Directory.CreateDirectory(blocked);
            bool failed = false;
            try
            {
                Store.Atomic(blocked, [6]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
            check(failed && Directory.Exists(blocked) && Directory.GetFiles(directory).Length == 1, "Failed atomic replacement cleans its own temporary file and preserves the destination");
        }
        finally { Directory.Delete(directory, true); }
        check(VpnConnectivity.Assess(true, true, true) == VpnHealth.Online, "A successful tunnel probe reports online");
        check(VpnConnectivity.Assess(true, true, false) == VpnHealth.Unreachable, "A living core with a failed probe does not report online");
        check(VpnConnectivity.Assess(false, true, false) == VpnHealth.NoNetwork, "Missing physical network has a separate state");
        check(VpnConnectivity.Assess(true, false, true) == VpnHealth.NetworkChanged, "Changed uplink requires reconnection even after a previous success");
        check(VpnConnectivity.ParseAddress("203.0.113.10\n") == "203.0.113.10" && VpnConnectivity.ParseAddress("fl=123\nip=2001:db8::1\n") == "2001:db8::1", "Connectivity responses accept both independent endpoints and IPv6");
        check(VpnConnectivity.ParseAddress("<html>error</html>") == null, "HTTP success without an IP is not a successful VPN probe");
        using var handler = new ProbeHandler();
        using var client = new HttpClient(handler);
        string? ip = VpnConnectivity.Probe(client, CancellationToken.None).GetAwaiter().GetResult();
        check(ip == "203.0.113.20" && handler.Requests == 2, "Tunnel probe falls back to an independent endpoint when the first fails");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool cancelledProbe = false;
        try
        {
            VpnConnectivity.Probe(client, cancelled.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { cancelledProbe = true; }
        check(cancelledProbe, "Stopping a VPN session cancels its connectivity probe");
        check(RuleRepository.Describe(new System.Text.Json.Nodes.JsonObject { ["revision"] = "abc", ["fetched"] = "corrupt" }).Contains("неизвестно"), "Incomplete blocklist metadata does not break application startup");
    }

    sealed class ProbeHandler : HttpMessageHandler
    {
        public int Requests
        {
            get; private set;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(new HttpResponseMessage(Requests == 1 ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("ip=203.0.113.20\n")
            });
        }
    }
}
