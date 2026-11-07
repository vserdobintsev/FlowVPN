using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlowVPN;

internal static class BrokerChecks
{
    public static async Task Run()
    {
        var passed = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition)
                throw new InvalidOperationException(label);
            passed.Add(label);
        }
        Check(AppContext.TryGetSwitch("System.StartupHookProvider.IsSupported", out bool hooks) && !hooks,
            "Runtime disables environment-supplied managed startup hooks before the entry point");
        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException) { rejected = true; }
            Check(rejected, label);
        }
        using var key = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=Broker fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var openvpn = new JsonObject
        {
            ["type"] = "openvpn-client", ["tag"] = "vpn", ["mode"] = "tls", ["network"] = "udp",
            ["servers"] = new JsonArray(new JsonObject { ["server"] = "127.0.0.1", ["server_port"] = 1194, ["network"] = "udp" }),
            ["tls"] = new JsonObject { ["certificate"] = new JsonArray(cert.ExportCertificatePem()), ["client_certificate"] = new JsonArray(cert.ExportCertificatePem()), ["client_key"] = new JsonArray(key.ExportPkcs8PrivateKeyPem()), ["remote_certificate_tls"] = "server" },
            ["remote_random"] = false, ["route_no_pull"] = true, ["redirect_gateway"] = false, ["system"] = false, ["domain_resolver"] = "dns-direct"
        };
        var outline = Importers.Outline("ss://YWVzLTEyOC1nY206Zml4dHVyZS1vbmx5@127.0.0.1:23456");
        foreach (string kind in new[] { "Outline", "OpenVPN" })
            foreach (string mode in new[] { "include", "exclude", "all" })
                foreach (bool enabled in new[] { false, true })
                {
                    var plan = new BrokerPlan
                    {
                        Kind = kind, ConnectionJson = kind == "Outline" ? outline.ConnectionJson : openvpn.ToJsonString(), Mode = mode,
                        ProcessPatterns = [AppRule.Folder(@"D:\Apps\Fixture").Pattern], Domains = ["example.org"], BaseEnabled = enabled,
                        IpCidrs = enabled ? ["192.0.2.0/24", "2001:db8::/32"] : [],
                        BaseDomains = enabled ? new() { Domain = ["exact.example"], Suffix = ["suffix.example"], Keyword = ["fixture"], Regex = ["^test[0-9]+\\.example$"] } : null
                    };
                    using var api = new CoreControl(15876);
                    var config = BrokerConfig.Build(plan, new("Fixture adapter", "127.0.0.1"), 15876, api, new());
                    Check(config["log"]!["output"] == null && config["route"]!["rule_set"]!.AsArray().All(r => r!["type"]!.ToString() == "inline" && r["path"] == null),
                        $"Broker {kind}/{mode}/base={enabled} has no privileged log or rule file paths");
                    await CheckStdin(config);
                    Check(true, $"Broker {kind}/{mode}/base={enabled} passes pinned sing-box check over stdin without TUN");
                      if (kind == "Outline")
                      {
                          plan.LocalTransport = new()
                          {
                              ModuleId = ModuleTransport.OutlineModuleId, ProtocolVersion = 1, Port = 23456,
                              Username = new string('A', 64), Password = new string('B', 64), InterfaceName = "Fixture adapter"
                          };
                          var moduleConfig = BrokerConfig.Build(plan, new("Fixture adapter", "127.0.0.1"), 15876, api, new());
                          Check(JsonNode.DeepEquals(config["route"], moduleConfig["route"]) && JsonNode.DeepEquals(config["dns"], moduleConfig["dns"]),
                              $"Module transport preserves all routing and DNS rules in {mode}/base={enabled}");
                          await CheckStdin(moduleConfig);
                          Check(true, $"Module transport {mode}/base={enabled} passes pinned engine validation");
                      }
                }
        using (var api = new CoreControl(15876))
        {
            var ipOnly = BrokerConfig.Build(new BrokerPlan { Kind = "Outline", ConnectionJson = outline.ConnectionJson, BaseEnabled = true, IpCidrs = ["192.0.2.0/24"] },
                new("Fixture adapter", "127.0.0.1"), 15876, api, new());
            Check(ipOnly["route"]!["rule_set"]!.AsArray().Count == 1 && ipOnly["route"]!["rules"]!.AsArray().OfType<JsonObject>()
                .Single(rule => rule["rule_set"] != null)["rule_set"]!.AsArray().Single()!.ToString() == "ru-blocked",
                "An empty domain selection keeps the GeoIP base and never references a missing domain rule set");
            await CheckStdin(ipOnly);
            Check(true, "IP-only shared policy passes pinned engine validation without TUN");
        }
        foreach (string field in new[] { "certificate_path", "client_certificate_path", "client_key_path", "insecure", "handshake_timeout", "utls" })
        {
            var altered = openvpn.DeepClone();
            altered["tls"]![field] = @"C:\private\material";
            Reject(() => BrokerConfig.Source("OpenVPN", altered.ToJsonString()), "Broker rejects unapproved TLS field " + field);
        }
        foreach (string field in new[] { "bind_interface", "protect_path", "routing_mark", "detour", "plugin", "username", "password", "system_interface_name" })
        {
            var altered = openvpn.DeepClone();
            altered[field] = "untrusted";
            Reject(() => BrokerConfig.Source("OpenVPN", altered.ToJsonString()), "Broker rejects unapproved endpoint field " + field);
        }
        foreach (string field in new[] { "system", "redirect_gateway", "route_no_pull" })
        {
            var altered = openvpn.DeepClone();
            altered[field] = !altered[field]!.GetValue<bool>();
            Reject(() => BrokerConfig.Source("OpenVPN", altered.ToJsonString()), "Broker rejects caller change to protected OpenVPN setting " + field);
        }
        var shadow = JsonNode.Parse(outline.ConnectionJson)!;
        shadow["password"] = "fixture\nwith\tcontrol\0characters";
        Check(BrokerConfig.Source("Outline", shadow.ToJsonString())["password"]!.GetValue<string>() == shadow["password"]!.GetValue<string>(), "Broker preserves opaque Shadowsocks credential characters");
        shadow["plugin"] = "unexpected.exe";
        Reject(() => BrokerConfig.Source("Outline", shadow.ToJsonString()), "Broker rejects Shadowsocks plugin execution options");
        Reject(() => BrokerConfig.Source("Outline", outline.ConnectionJson.Insert(1, "\"tag\":\"duplicate\",")), "Broker rejects duplicate nested source properties");
        Reject(() => BrokerConfig.Source("Unknown", outline.ConnectionJson), "Broker rejects unknown source kind");
        using (var api = new CoreControl(15876))
        {
            JsonObject Build(BrokerPlan plan) => BrokerConfig.Build(plan, new("Fixture", "127.0.0.1"), 15876, api, new());
            BrokerPlan Plan() => new() { Kind = "Outline", ConnectionJson = outline.ConnectionJson };
            var plan = Plan(); plan.Mode = "custom";
            Reject(() => Build(plan), "Broker rejects unknown routing mode");
            plan = Plan(); plan.ProcessPatterns = null!;
            Reject(() => Build(plan), "Broker rejects null process rule array");
            plan = Plan(); plan.ProcessPatterns = [new string('a', 4097)];
            Reject(() => Build(plan), "Broker bounds process expression size");
            plan = Plan(); plan.IpCidrs = ["192.0.2.0/24"];
            Reject(() => Build(plan), "Disabled base cannot carry hidden rules");
            plan = Plan(); plan.BaseEnabled = true; plan.IpCidrs = ["not-a-network"]; plan.BaseDomains = new() { Suffix = ["example.org"] };
            Reject(() => Build(plan), "Broker rejects malformed IP network");
            plan = Plan(); plan.BaseEnabled = true; plan.IpCidrs = ["192.0.2.0/24"]; plan.BaseDomains = new();
            Reject(() => Build(plan), "Enabled base cannot silently omit its domain category");
              BrokerLocalTransport Transport() => new()
              {
                  ModuleId = ModuleTransport.OutlineModuleId, ProtocolVersion = 1, Port = 23456,
                  Username = new string('A', 64), Password = new string('B', 64), InterfaceName = "Fixture"
              };
              plan = Plan(); plan.LocalTransport = Transport();
              var localConfig = Build(plan);
              var localTarget = localConfig["outbounds"]!.AsArray().Single(item => item!["tag"]!.ToString() == "vpn")!;
              Check(localTarget["type"]!.ToString() == "socks" && localTarget["server"]!.ToString() == "127.0.0.1" &&
                  localTarget["inet4_bind_address"]!.ToString() == "127.0.0.1" && localTarget["bind_interface"] != null,
                  "Module outbound is authenticated SOCKS bound to Windows loopback rather than the physical default interface");
              await CheckStdin(localConfig);
              Check(true, "Module SOCKS outbound passes pinned sing-box validation without TUN");
              foreach (Action<BrokerLocalTransport> mutate in new Action<BrokerLocalTransport>[]
              {
                  value => value.ModuleId = "unknown", value => value.ProtocolVersion = 2,
                  value => value.Port = 80, value => value.Port = 65536,
                  value => value.Username = "weak", value => value.Password = "weak", value => value.InterfaceName = "Changed"
              })
              {
                  plan = Plan(); plan.LocalTransport = Transport(); mutate(plan.LocalTransport);
                  Reject(() => Build(plan), "Broker rejects an unrecognized or unsafe local-module transport");
              }
              plan = new() { Kind = "OpenVPN", ConnectionJson = openvpn.ToJsonString(), LocalTransport = Transport() };
              Reject(() => Build(plan), "Local Outline module cannot replace an OpenVPN connection");
        }
        await BrokerTransportChecks.Run(Check);
        await CoreCliCancellationChecks.Run(Check);
        Store.Atomic(Path.Combine(Store.Data, "broker-checks.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Passed = passed.Count, Tests = passed, UsesTun = false, UsesElevation = false, ExternalTraffic = false
        }, Store.JsonOptions));
    }

    static async Task CheckStdin(JsonObject config)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (string arg in new[] { "--disable-color", "check", "-c", "stdin" })
            info.ArgumentList.Add(arg);
        using var core = CoreIntegrity.Start(info);
        using var job = new CoreJob(core.Process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var log = new BrokerHost.LogBuffer();
        var output = log.Capture(core.Process.StandardOutput);
        var error = log.Capture(core.Process.StandardError);
        await core.Process.StandardInput.WriteAsync(config.ToJsonString().AsMemory(), timeout.Token);
        core.Process.StandardInput.Close();
        await core.Process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(output, error).WaitAsync(timeout.Token);
        if (core.Process.ExitCode != 0)
            throw new InvalidOperationException("Pinned engine rejected broker fixture: " + string.Join("\n", log.Drain()));
    }
}
