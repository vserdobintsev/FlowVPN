using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace FlowVPN;

internal static class ManualConnectionChecks
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, label);
        }
        check(Importers.ShadowsocksMethods.Count == 6 && Importers.ShadowsocksMethods.Distinct().Count() == 6 &&
            Importers.ShadowsocksMethods is ICollection<string> { IsReadOnly: true }, "Manual Shadowsocks exposes one immutable list of six supported methods");
        const string credential = " \tfixture:@/?#%\n\0пароль ";
        foreach (string method in Importers.ShadowsocksMethods)
        {
            var manual = Importers.Shadowsocks(" Example.TEST ", 23456, method, credential, "Manual fixture");
            string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(method + ":" + credential));
            var imported = Importers.Outline("ss://" + auth + "@example.test:23456#Imported");
            check(manual.ConnectionJson == imported.ConnectionJson && manual.Fingerprint == imported.Fingerprint,
                "Manual and access-key Shadowsocks share canonical configuration and fingerprint: " + method);
            check(JsonNode.Parse(manual.ConnectionJson)!["password"]!.GetValue<string>() == credential && manual.Name == "Manual fixture" && manual.Kind == "Outline",
                "Opaque credentials and chosen names survive manual Shadowsocks construction: " + method);
        }
        var ip6 = Importers.Shadowsocks("[2001:0DB8:0:0::1]", 65535, "aes-128-gcm", "fixture");
        var imported6 = Importers.Outline("ss://YWVzLTEyOC1nY206Zml4dHVyZQ==@[2001:db8::1]:65535");
        check(ip6.Fingerprint == imported6.Fingerprint && JsonNode.Parse(ip6.ConnectionJson)!["server"]!.ToString() == "2001:db8::1", "Bracketed and bare IPv6 use the same canonical server");
        check(ip6.Name == "[2001:db8::1] · Shadowsocks" && imported6.Name == "[2001:db8::1] · Outline", "Manual protocol naming does not rename imported Outline keys");
        var international = Importers.Shadowsocks("ПРИМЕР.РФ", 443, "aes-128-gcm", "fixture");
        var ascii = Importers.Outline("ss://YWVzLTEyOC1nY206Zml4dHVyZQ==@xn--e1afmkfd.xn--p1ai:443");
        check(international.Fingerprint == ascii.Fingerprint && JsonNode.Parse(international.ConnectionJson)!["server"]!.ToString() == "xn--e1afmkfd.xn--p1ai", "Unicode and ASCII IDN server names share a canonical fingerprint");
        check(Importers.Shadowsocks("127.0.0.1", 1, "aes-256-gcm", "").ConnectionJson.Contains("\"password\":\"\"", StringComparison.Ordinal), "Existing empty opaque Shadowsocks credentials remain representable");
        foreach (string host in new[] { "", "https://example.test", "user@example.test", "example.test/path", @"example.test\path", "example.test?query", "example.test#fragment", "example.test:443", "example .test", "example\n.test", "[example.test]", "[127.0.0.1]", "[::1", "::1]", new string('a', 254) })
            Reject(() => Importers.Shadowsocks(host, 443, "aes-128-gcm", "fixture"), "Manual server rejects URI syntax, malformed brackets or excessive input: " + host.Replace('\n', ' '));
        foreach (int port in new[] { -1, 0, 65536, int.MaxValue })
            Reject(() => Importers.Shadowsocks("example.test", port, "aes-128-gcm", "fixture"), "Manual server rejects out-of-range port " + port);
        Reject(() => Importers.Shadowsocks("example.test", 443, "unsupported", "fixture"), "Manual Shadowsocks rejects unsupported methods");
        Reject(() => Importers.Shadowsocks("example.test", 443, "aes-128-gcm", new string('x', 65537)), "Manual Shadowsocks bounds credential size");

        var connections = new Settings();
        var staticSource = Importers.Shadowsocks("example.test", 443, "aes-128-gcm", "fixture");
        var subscription = new VpnSource
        {
            Kind = "Outline", ConnectionJson = staticSource.ConnectionJson,
            DynamicKey = "ssconf://example.test/subscription", Fingerprint = Store.Hash("ssconf://example.test/subscription")
        };
        check(Store.AddSource(connections, staticSource) && Store.AddSource(connections, subscription) && connections.Sources.Count == 2,
            "Static and dynamic connections retain separate identities when their current payloads match");
        var refreshed = new VpnSource
        {
            Kind = "Outline", ConnectionJson = Importers.Shadowsocks("changed.example", 23456, "aes-128-gcm", "rotated").ConnectionJson,
            DynamicKey = "  ssconf://example.test/subscription  ", Fingerprint = "changed-fixture-fingerprint"
        };
        check(Store.SameSource(subscription, refreshed) && !Store.AddSource(connections, refreshed) && connections.Sources.Count == 2,
            "A dynamic connection is deduplicated by subscription identity across key rotation");
        check(!Store.AddSource(connections, Importers.Outline("ss://YWVzLTEyOC1nY206Zml4dHVyZQ==@example.test:443")) && connections.Sources.Count == 2,
            "Equivalent static manual and access-key connections still deduplicate");
        var differentSubscription = new VpnSource { Kind = "Outline", ConnectionJson = subscription.ConnectionJson, DynamicKey = "ssconf://example.test/another", Fingerprint = subscription.Fingerprint };
        check(!Store.SameSource(subscription, differentSubscription), "Different subscriptions are not merged by a transient payload or stale fingerprint");
        check(!Store.SameSource(new() { ConnectionJson = "first" }, new() { ConnectionJson = "second" }), "Unset fingerprints do not merge unrelated static connections");

        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=Manual import fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        string ca = "<ca>\n" + certificate.ExportCertificatePem() + "\n</ca>\n";
        string cert = "<cert>\n" + certificate.ExportCertificatePem() + "\n</cert>\n";
        string privateKey = "<key>\n" + key.ExportPkcs8PrivateKeyPem() + "\n</key>\n";
        string config = "client\nproto udp\nremote 127.0.0.1 1194\nverify-x509-name 'Manual import fixture' name\n" + ca + cert + privateKey;
        var source = Importers.OpenVpnText(config, "Standalone OpenVPN");
        check(source.Name == "Standalone OpenVPN" && source.Kind == "OpenVPN" && BrokerConfig.Source(source.Kind, source.ConnectionJson)["type"]!.ToString() == "openvpn-client", "Standalone OpenVPN text produces the broker's closed source schema");
        check(Importers.OpenVpnText("ca [inline]\ncert [inline]\nkey [inline]\n" + config).Fingerprint == source.Fingerprint, "OpenVPN text supports explicit inline material markers");
        Reject(() => Importers.OpenVpnText(""), "OpenVPN text rejects an empty input");
        Reject(() => Importers.OpenVpnText(new string('x', 4 * 1024 * 1024 + 1)), "OpenVPN text bounds its input size");
        Reject(() => Importers.OpenVpnText(config.Replace(privateKey, "")), "OpenVPN text requires inline private-key material");
        Reject(() => Importers.OpenVpnText(config.Replace(ca, "<ca>\n </ca>\n")), "OpenVPN text rejects empty inline material");
        Reject(() => Importers.OpenVpnText("ca C:/private/ca.pem\n" + config), "OpenVPN text rejects external material even beside an inline block");
        Reject(() => Importers.OpenVpnText(config.Replace(ca, "ca C:/private/ca.pem\n")), "OpenVPN text never opens referenced certificate paths");
        Reject(() => Importers.OpenVpnText("config C:/private/profile.ovpn\n" + config), "OpenVPN text retains rejection of nested configuration files");
        Reject(() => Importers.OpenVpnText("tls-verify arbitrary-command\n" + config), "OpenVPN text retains rejection of external verification commands");

        check(ConnectionInput.Detect(config) == ConnectionInputKind.OpenVpn && ConnectionInput.Detect("# comment\n--remote example.test 443") == ConnectionInputKind.OpenVpn, "Connection detection recognizes OpenVPN syntax without a file extension");
        check(ConnectionInput.Detect("\uFEFF SS://fixture") == ConnectionInputKind.OutlineKey && ConnectionInput.Detect("ssconf://example.test/key") == ConnectionInputKind.DynamicOutlineKey, "Connection detection recognizes static and dynamic keys without fetching them");
        const string json = "{\"server\":\"::1\",\"server_port\":23456,\"method\":\"aes-128-gcm\",\"password\":\"fixture\"}";
        check(ConnectionInput.Detect(json) == ConnectionInputKind.ShadowsocksJson && ConnectionInput.Detect("{\"server\":") == ConnectionInputKind.ShadowsocksJson, "Recognizable incomplete Shadowsocks JSON stays in its detected format");
        foreach (string unknown in new[] { "", "example.test", "127.0.0.1", "https://example.test/key", "vless://unsupported", "{\"unrelated\":true}" })
            check(ConnectionInput.Detect(unknown) == ConnectionInputKind.Unknown, "Connection detection does not infer unsupported input: " + unknown);
        check(ConnectionInput.ImportAsync(json).GetAwaiter().GetResult().Fingerprint == Importers.Shadowsocks("::1", 23456, "aes-128-gcm", "fixture").Fingerprint,
            "Detected JSON uses the same validated Shadowsocks builder");
        check(ConnectionInput.ImportAsync(json.Replace("\"fixture\"", "\"\"")).GetAwaiter().GetResult().Fingerprint == Importers.Shadowsocks("::1", 23456, "aes-128-gcm", "").Fingerprint,
            "JSON and manual input preserve the same empty opaque credential");
        Reject(() => ConnectionInput.ImportAsync("{\"server\":").GetAwaiter().GetResult(), "Malformed detected JSON produces a friendly validation failure");
        Reject(() => ConnectionInput.ImportAsync(json.Replace("}", ",\"plugin\":\"unsupported\"}")).GetAwaiter().GetResult(), "Detected JSON never drops unsupported transport fields");
        Reject(() => ConnectionInput.ImportAsync(json.Insert(1, "\"password\":\"duplicate\",")).GetAwaiter().GetResult(), "JSON rejects duplicate credential fields instead of choosing a value");
        string? publicError = null;
        try { ConnectionInput.ImportAsync(json.Insert(1, "\"fixture-secret-property\":0,")).GetAwaiter().GetResult(); }
        catch (InvalidDataException error) { publicError = error.Message; }
        check(publicError != null && !publicError.Contains("fixture-secret-property", StringComparison.Ordinal), "JSON validation does not echo untrusted property names");
        bool canceled = false;
        try { ConnectionInput.ImportAsync("ssconf://127.0.0.1/never-requested", token: new CancellationToken(true)).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { canceled = true; }
        check(canceled, "Canceled dynamic import performs no request");
        canceled = false;
        try { ConnectionInput.DetectFileAsync("not-opened-after-cancellation", new CancellationToken(true)).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { canceled = true; }
        check(canceled, "Canceled file detection performs no file lookup");

        string folder = Path.Combine(Store.Run, "manual-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "config.txt");
            File.WriteAllText(file, config);
            check(ConnectionInput.DetectFileAsync(file).GetAwaiter().GetResult() == ConnectionInputKind.OpenVpn, "File preview detects OpenVPN from bounded local contents");
            var fromFile = ConnectionInput.ImportFileAsync(file).GetAwaiter().GetResult();
            check(fromFile.ConnectionJson == source.ConnectionJson && fromFile.Fingerprint == source.Fingerprint, "File and pasted OpenVPN share canonical configuration independent of extension");
            File.WriteAllText(file, json);
            check(ConnectionInput.DetectFileAsync(file).GetAwaiter().GetResult() == ConnectionInputKind.ShadowsocksJson, "File preview recognizes JSON independent of its extension");
            check(ConnectionInput.ImportFileAsync(file).GetAwaiter().GetResult().Kind == "Outline", "JSON file content determines its connection type");
            File.WriteAllText(file, "ssconf://127.0.0.1/not-fetched-in-preview");
            check(ConnectionInput.DetectFileAsync(file).GetAwaiter().GetResult() == ConnectionInputKind.DynamicOutlineKey, "Dynamic-key file preview never fetches the subscription");
            File.WriteAllText(file, config.Replace(ca, "ca ca.pem\n"));
            File.WriteAllText(Path.Combine(folder, "ca.pem"), certificate.ExportCertificatePem() + "\n");
            check(ConnectionInput.ImportFileAsync(file).GetAwaiter().GetResult().Fingerprint == source.Fingerprint, "Explicit OpenVPN file import preserves relative certificate material");
        }
        finally { Directory.Delete(folder, true); }
    }
}
