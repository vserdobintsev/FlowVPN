using System.Text.Json.Nodes;

namespace FlowVPN;

internal static class DomainRuleChecks
{
    public static void Run(Action<bool, string> check)
    {
        var coverage = new DomainCoverage(["example.com", "пример.рф"]);
        check(coverage.Match("www.EXAMPLE.com.") == "example.com" && coverage.Match("notexample.com") == null, "Base lookup is case-insensitive and respects domain label boundaries");
        check(coverage.Match("sub.xn--e1afmkfd.xn--p1ai") != null, "Base matching supports IDN and subdomains");
        string[] saved = ["example.com", "www.example.com", "unique.net", "UNIQUE.net", "sub.unique.net"];
        check(DomainCoverage.Effective(saved, coverage).SequenceEqual(new[] { "unique.net" }), "Active base and parent-domain duplicates are removed from generated routing");
        check(DomainCoverage.Effective(saved, null).SequenceEqual(new[] { "example.com", "unique.net" }), "Disabled base restores additional rules without modifying saved domains");
        check(saved.Length == 5, "Deduplication leaves profile data intact until the user deletes rows");
        bool invalid = false;
        try
        {
            DomainRepository.CategoryId("../secrets");
        }
        catch (InvalidDataException) { invalid = true; }
        check(invalid, "Category IDs cannot escape the rules directory");
        var source = new VpnSource { Id = "test", Kind = "Outline", ConnectionJson = "{\"type\":\"shadowsocks\",\"server\":\"127.0.0.1\",\"server_port\":12345,\"method\":\"aes-128-gcm\",\"password\":\"test\"}" };
        var settings = new Settings { BaseRulesEnabled = false, Sources = [source] };
        var profile = new VpnProfile { SourceId = source.Id, Domains = ["example.com", "example.com", "sub.example.com"] };
        var config = ConfigBuilder.Build(settings, profile, new("test", "127.0.0.1"), 12346);
        var users = config["inbounds"]![1]!["users"]!.AsArray();
        check(users.Count == 1 && users[0]!["password"]!.GetValue<string>().Length == 64, "Every generated manager inbound requires a cryptographically random session password");
        var routeDomains = config["route"]!["rules"]!.AsArray().OfType<JsonObject>().Single(r => r["domain_suffix"] != null)["domain_suffix"]!.AsArray();
        check(routeDomains.Count == 1 && routeDomains[0]!.ToString() == "example.com", "The actual sing-box config contains no duplicate or covered profile domains");
        profile.Mode = "exclude";
        profile.Apps.Add(AppRule.File(@"D:\Example\app.exe"));
        var excluded = ConfigBuilder.Build(settings, profile, new("test", "127.0.0.1"), 12346);
        var dnsRules = excluded["dns"]!["rules"]!.AsArray();
        check(dnsRules[0]?["domain_suffix"] != null && dnsRules[0]?["server"]?.ToString() == "dns-vpn" && dnsRules[1]?["server"]?.ToString() == "dns-direct", "Explicit VPN domains have DNS priority over app exclusions, matching traffic routing priority");
        check(StartupIntegration.Command(@"E:\Тест User\FlowVPN.exe", @"E:\Data\", true).Contains("--startup") && !StartupIntegration.Command("app.exe", "data", false).Contains("--connect-selected"), "Startup registration never requests automatic VPN connection");
        check(StartupIntegration.Quote("a\"b\\") == "\"a\\\"b\\\\\"", "Windows startup argument quoting preserves quotes and trailing backslashes");
    }
}
