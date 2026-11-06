using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlowVPN;

// The privileged process reconstructs a closed configuration schema. Caller-supplied
// objects never become sing-box configuration, including nested TLS and dial options.
internal static class BrokerConfig
{
    const int MaxRules = 2_000_000;
    static JsonArray A(IEnumerable<string> values) => new(values.Select(x => JsonValue.Create(x)).ToArray());
    static InvalidDataException Invalid() => new("Недопустимые параметры подключения VPN.");

    public static JsonObject Build(BrokerPlan plan, Uplink uplink, int proxyPort, CoreControl control, ProxyAccess access)
    {
        if (plan.Mode is not ("include" or "exclude" or "all") || proxyPort is < 1 or > 65535)
            throw Invalid();
        var target = Source(plan.Kind, plan.ConnectionJson);
        var patterns = Strings(plan.ProcessPatterns, 4096, 4096);
        var domains = Strings(plan.Domains, 100_000, 253).Select(DomainCoverage.Normalize).Distinct(StringComparer.Ordinal).ToArray();
        var cidrs = Strings(plan.IpCidrs, MaxRules, 64);
        foreach (string cidr in cidrs)
            if (!IPNetwork.TryParse(cidr, out _))
                throw Invalid();
        if (!plan.BaseEnabled && (cidrs.Length != 0 || plan.BaseDomains != null))
            throw Invalid();
        if (plan.BaseEnabled && (cidrs.Length == 0 || plan.BaseDomains == null))
            throw Invalid();

        var sets = new JsonArray();
        if (plan.BaseEnabled)
        {
            sets.Add(new JsonObject { ["type"] = "inline", ["tag"] = "ru-blocked", ["rules"] = new JsonArray(new JsonObject { ["ip_cidr"] = A(cidrs) }) });
            var source = plan.BaseDomains!;
            var domainRules = new JsonObject();
            long count = (long)(source.Domain?.Length ?? 0) + (source.Suffix?.Length ?? 0) + (source.Keyword?.Length ?? 0) + (source.Regex?.Length ?? 0);
            if (count is 0 or > MaxRules)
                throw Invalid();
            Add("domain", source.Domain, 253, true);
            Add("domain_suffix", source.Suffix, 253, true);
            Add("domain_keyword", source.Keyword, 1024, false);
            Add("domain_regex", source.Regex, 4096, false);
            sets.Add(new JsonObject { ["type"] = "inline", ["tag"] = "ru-domains", ["rules"] = new JsonArray(domainRules) });
            void Add(string key, string[]? values, int length, bool normalize)
            {
                var clean = Strings(values, MaxRules, length);
                if (clean.Length > 0)
                    domainRules[key] = A(normalize ? clean.Select(DomainCoverage.Normalize) : clean);
            }
        }
        var dnsRules = new JsonArray();
        if (domains.Length > 0)
            dnsRules.Add(new JsonObject { ["domain_suffix"] = A(domains), ["server"] = "dns-vpn" });
        if (patterns.Length > 0)
            dnsRules.Add(new JsonObject { ["process_path_regex"] = A(patterns), ["server"] = plan.Mode == "exclude" && !plan.BaseEnabled ? "dns-direct" : "dns-vpn" });
        var rules = new JsonArray
        {
            new JsonObject { ["inbound"] = A(["manager"]), ["action"] = "route", ["outbound"] = "vpn" },
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }
        };
        if (plan.BaseEnabled)
            rules.Add(new JsonObject { ["rule_set"] = A(["ru-blocked", "ru-domains"]), ["action"] = "route", ["outbound"] = "vpn" });
        rules.Add(new JsonObject { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "direct" });
        if (domains.Length > 0)
            rules.Add(new JsonObject { ["domain_suffix"] = A(domains), ["action"] = "route", ["outbound"] = "vpn" });
        if (patterns.Length > 0 && plan.Mode != "all")
            rules.Add(new JsonObject { ["process_path_regex"] = A(patterns), ["action"] = "route", ["outbound"] = plan.Mode == "exclude" ? "direct" : "vpn" });
        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "udp", ["tag"] = "dns-direct", ["server"] = uplink.Dns, ["server_port"] = 53 },
                    new JsonObject { ["type"] = "https", ["tag"] = "dns-vpn", ["server"] = "1.1.1.1", ["server_port"] = 443, ["path"] = "/dns-query", ["detour"] = "vpn", ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "cloudflare-dns.com" } }
                },
                ["rules"] = dnsRules, ["final"] = plan.BaseEnabled || plan.Mode != "include" ? "dns-vpn" : "dns-direct", ["strategy"] = "ipv4_only"
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject { ["type"] = "tun", ["tag"] = "flow-tun", ["interface_name"] = "FlowVPN", ["address"] = A(["172.30.254.1/30", "fd72:9b63:7a11::1/126"]), ["mtu"] = 1400, ["auto_route"] = true, ["strict_route"] = false, ["dns_mode"] = "native", ["stack"] = "mixed" },
                new JsonObject { ["type"] = "mixed", ["tag"] = "manager", ["listen"] = "127.0.0.1", ["listen_port"] = proxyPort, ["users"] = access.Users() }
            },
            ["endpoints"] = plan.Kind == "OpenVPN" ? new JsonArray(target) : new JsonArray(),
            ["outbounds"] = plan.Kind == "Outline" ? new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }, target) : new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject { ["auto_detect_interface"] = false, ["default_interface"] = uplink.Name, ["default_domain_resolver"] = "dns-direct", ["rule_set"] = sets, ["rules"] = rules, ["final"] = plan.Mode == "include" ? "direct" : "vpn" },
            ["experimental"] = control.Configuration()
        };
    }

    internal static JsonObject Source(string kind, string json)
    {
        if (json is null || json.Length is 0 or > 4 * 1024 * 1024)
            throw Invalid();
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        if (kind == "Outline")
        {
            Fields(root, "type", "tag", "server", "server_port", "method", "password", "domain_resolver");
            Equal(root, "type", "shadowsocks");
            Equal(root, "tag", "vpn");
            Equal(root, "domain_resolver", "dns-direct");
            string method = Text(root, "method", 64);
            if (method is not ("chacha20-ietf-poly1305" or "aes-128-gcm" or "aes-256-gcm" or "2022-blake3-aes-128-gcm" or "2022-blake3-aes-256-gcm" or "2022-blake3-chacha20-poly1305"))
                throw Invalid();
            string password = root.GetProperty("password").GetString() ?? throw Invalid();
            if (password.Length > 65536)
                throw Invalid();
            // An opaque credential may contain control characters; JSON encoding preserves it.
            return new JsonObject { ["type"] = "shadowsocks", ["tag"] = "vpn", ["server"] = Host(root), ["server_port"] = Port(root), ["method"] = method, ["password"] = password, ["domain_resolver"] = "dns-direct" };
        }
        if (kind != "OpenVPN")
            throw Invalid();
        Fields(root, "type", "tag", "mode", "network", "servers", "tls", "remote_random", "route_no_pull", "redirect_gateway", "system", "domain_resolver", "data_ciphers_fallback", "data_ciphers", "auth");
        Equal(root, "type", "openvpn-client");
        Equal(root, "tag", "vpn");
        Equal(root, "mode", "tls");
        Equal(root, "domain_resolver", "dns-direct");
        Boolean(root, "route_no_pull", true);
        Boolean(root, "redirect_gateway", false);
        Boolean(root, "system", false);
        var servers = new JsonArray();
        var serverList = root.GetProperty("servers");
        if (serverList.ValueKind != JsonValueKind.Array || serverList.GetArrayLength() is 0 or > 64)
            throw Invalid();
        foreach (var server in serverList.EnumerateArray())
        {
            Fields(server, "server", "server_port", "network");
            servers.Add(new JsonObject { ["server"] = Host(server), ["server_port"] = Port(server), ["network"] = Network(server) });
        }
        var tlsSource = root.GetProperty("tls");
        Fields(tlsSource, "certificate", "client_certificate", "client_key", "remote_certificate_tls", "server_name", "server_name_type");
        Equal(tlsSource, "remote_certificate_tls", "server");
        var tls = new JsonObject { ["certificate"] = Pem(tlsSource, "certificate", false), ["client_certificate"] = Pem(tlsSource, "client_certificate", false), ["client_key"] = Pem(tlsSource, "client_key", true), ["remote_certificate_tls"] = "server" };
        if (tlsSource.TryGetProperty("server_name", out _))
        {
            tls["server_name"] = Text(tlsSource, "server_name", 1024);
            string type = Text(tlsSource, "server_name_type", 32);
            if (type is not ("subject" or "name" or "name-prefix"))
                throw Invalid();
            tls["server_name_type"] = type;
        }
        else if (tlsSource.TryGetProperty("server_name_type", out _))
            throw Invalid();
        var target = new JsonObject { ["type"] = "openvpn-client", ["tag"] = "vpn", ["mode"] = "tls", ["network"] = Network(root), ["servers"] = servers, ["tls"] = tls, ["remote_random"] = root.GetProperty("remote_random").GetBoolean(), ["route_no_pull"] = true, ["redirect_gateway"] = false, ["system"] = false, ["domain_resolver"] = "dns-direct" };
        foreach (string key in new[] { "data_ciphers_fallback", "auth" })
            if (root.TryGetProperty(key, out var value))
                target[key] = Algorithm(value);
        if (root.TryGetProperty("data_ciphers", out var ciphers))
        {
            if (ciphers.ValueKind != JsonValueKind.Array || ciphers.GetArrayLength() is 0 or > 32)
                throw Invalid();
            target["data_ciphers"] = A(ciphers.EnumerateArray().Select(Algorithm));
        }
        return target;
    }

    static string Algorithm(JsonElement value)
    {
        string text = value.GetString() ?? throw Invalid();
        if (text.Length is 0 or > 64 || text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw Invalid();
        return text;
    }
    static JsonArray Pem(JsonElement root, string key, bool privateKey)
    {
        var array = root.GetProperty(key);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is 0 or > 32)
            throw Invalid();
        var result = new JsonArray();
        foreach (var item in array.EnumerateArray())
        {
            string value = item.GetString() ?? throw Invalid();
            if (value.Length is 0 or > 262144 || value.Contains('\0') || !value.Contains("-----BEGIN ", StringComparison.Ordinal) ||
                !value.Contains(privateKey ? "PRIVATE KEY-----" : "CERTIFICATE-----", StringComparison.Ordinal))
                throw Invalid();
            result.Add(value);
        }
        return result;
    }
    static string Network(JsonElement root)
    {
        string value = Text(root, "network", 8);
        return value is "udp" or "udp4" or "udp6" or "tcp" or "tcp4" or "tcp6" ? value : throw Invalid();
    }
    static int Port(JsonElement root) => root.GetProperty("server_port").TryGetInt32(out int port) && port is > 0 and <= 65535 ? port : throw Invalid();
    static string Host(JsonElement root)
    {
        string value = Text(root, "server", 253);
        return Uri.CheckHostName(value) != UriHostNameType.Unknown && !value.Any(char.IsWhiteSpace) ? value : throw Invalid();
    }
    static void Equal(JsonElement root, string key, string expected)
    {
        if (Text(root, key, 64) != expected)
            throw Invalid();
    }
    static void Boolean(JsonElement root, string key, bool expected)
    {
        if (root.GetProperty(key).GetBoolean() != expected)
            throw Invalid();
    }
    static string Text(JsonElement root, string key, int length)
    {
        string value = root.GetProperty(key).GetString() ?? throw Invalid();
        return value.Length > 0 && value.Length <= length && !value.Any(char.IsControl) ? value : throw Invalid();
    }
    static void Fields(JsonElement root, params string[] allowed)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw Invalid();
    }
    static string[] Strings(string[]? values, int count, int length)
    {
        if (values == null || values.Length > count || values.Any(v => v == null || v.Length is 0 || v.Length > length || v.Any(char.IsControl)))
            throw Invalid();
        return values;
    }
}
