using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace FlowVPN;

internal static class ReleaseChecks
{
    public static async Task Run()
    {
        // Synthetic credentials exist only for local parsing checks. No server is contacted.
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=FlowVPN compatibility fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var tls = new JsonObject
        {
            ["certificate"] = new JsonArray(certificate.ExportCertificatePem()),
            ["client_certificate"] = new JsonArray(certificate.ExportCertificatePem()),
            ["client_key"] = new JsonArray(rsa.ExportPkcs8PrivateKeyPem()),
            ["remote_certificate_tls"] = "server"
        };
        var openvpn = new VpnSource
        {
            Name = "Fixture",
            Kind = "OpenVPN",
            ConnectionJson = new JsonObject
            {
                ["type"] = "openvpn-client",
                ["mode"] = "tls",
                ["network"] = "udp",
                ["servers"] = new JsonArray(new JsonObject { ["server"] = "127.0.0.1", ["server_port"] = 1194 }),
                ["tls"] = tls,
                ["route_no_pull"] = true,
                ["redirect_gateway"] = false,
                ["system"] = false,
                ["domain_resolver"] = "dns-direct"
            }.ToJsonString()
        };
        var outline = new VpnSource { Name = "Fixture", Kind = "Outline", ConnectionJson = new JsonObject { ["type"] = "shadowsocks", ["server"] = "127.0.0.1", ["server_port"] = 23456, ["method"] = "chacha20-ietf-poly1305", ["password"] = "compatibility-test-only" }.ToJsonString() };
        string ruleJson = Path.Combine(Store.Run, "fixture-rules.json"), ruleSrs = Path.Combine(Store.Rules, "ru-blocked.srs"), config = Path.Combine(Store.Run, "compatibility.json");
        if (File.Exists(ruleSrs))
            throw new InvalidOperationException("Release checks require a fresh isolated --root; an existing rule set must not be replaced.");
        var passed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException(name);
            passed.Add(name);
        }
        try
        {
            Entry.ValidateElevationIdentity(null, "S-1-5-21-1-2-3-1001");
            Check(true, "Ordinary startup does not require an elevation handoff");
            Entry.ValidateElevationIdentity("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1001");
            Check(true, "Same-user elevation preserves access to protected settings");
            bool differentUserRejected = false;
            try
            {
                Entry.ValidateElevationIdentity("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1002");
            }
            catch (InvalidOperationException) { differentUserRejected = true; }
            Check(differentUserRejected, "Elevation as another user is rejected before settings initialization");
            string importedProfile = Path.Combine(Store.Run, "certificate-name-fixture.ovpn");
            try
            {
                File.WriteAllText(importedProfile, "client\nproto tcp4-client\nremote 127.0.0.1 1194 # fixture\nverify-x509-name \"FlowVPN compatibility fixture\" name\n<ca>\n" + certificate.ExportCertificatePem() + "\n</ca>\n<cert>\n" + certificate.ExportCertificatePem() + "\n</cert>\n<key>\n" + rsa.ExportPkcs8PrivateKeyPem() + "\n</key>\n");
                var imported = Importers.OpenVpn(importedProfile);
                Store.WriteJson(config, ConfigBuilder.Build(new Settings { BaseRulesEnabled = false, Sources = [imported] }, new VpnProfile { SourceId = imported.Id }, new("Compatibility adapter", "127.0.0.1"), 15876));
                await CoreCli.Run("check", "-c", config);
                passed.Add("Imported OpenVPN certificate name and TCP4 settings pass the actual pinned engine");
            }
            finally { File.Delete(importedProfile); }
            File.WriteAllText(ruleJson, "{\"version\":3,\"rules\":[{\"ip_cidr\":[\"192.0.2.0/24\"]}]}");
            await CoreCli.Run("rule-set", "compile", "--output", ruleSrs, ruleJson);
            foreach (var source in new[] { openvpn, outline })
                foreach (bool enabled in new[] { false, true })
                    foreach (string mode in new[] { "include", "exclude", "all" })
                    {
                        var settings = new Settings { BaseRulesEnabled = enabled, Sources = [source] };
                        var profile = new VpnProfile { SourceId = source.Id, Mode = mode, Apps = [AppRule.Folder(@"D:\Apps\Example App")], Domains = ["example.com"] };
                        Store.WriteJson(config, ConfigBuilder.Build(settings, profile, new("Compatibility adapter", "127.0.0.1"), 15876));
                        await CoreCli.Run("check", "-c", config);
                        passed.Add(source.Kind + " / " + mode + " / base=" + enabled);
                    }
            var cacheMetadata = new JsonObject { ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ruleSrs))) };
            Check(await RuleRepository.ValidCache(cacheMetadata), "Unchanged GeoIP cache matches its saved hash");
            var alteredRuleSet = File.ReadAllBytes(ruleSrs);
            alteredRuleSet[^1] ^= 1;
            File.WriteAllBytes(ruleSrs, alteredRuleSet);
            Check(!await RuleRepository.ValidCache(cacheMetadata), "Damaged GeoIP cache cannot be reused just because its revision matches");
            File.Delete(ruleSrs);
            Store.WriteJson(RuleRepository.MetaPath, new JsonObject { ["fetched"] = DateTimeOffset.UtcNow.ToString("O") });
            Check(RuleRepository.Due(), "Missing GeoIP cache requires refresh even with recent metadata");
            File.Delete(RuleRepository.MetaPath);
            File.WriteAllText(config, "{\"log\":{\"disabled\":true},\"dns\":{\"servers\":[{\"type\":\"udp\",\"tag\":\"dns-direct\",\"server\":\"127.0.0.1\"}],\"final\":\"dns-direct\"},\"outbounds\":[{\"type\":\"direct\",\"tag\":\"direct\"}],\"route\":{\"final\":\"direct\"}}");
            var start = new ProcessStartInfo(Store.Bin) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (string arg in new[] { "--disable-color", "run", "-c", config })
                start.ArgumentList.Add(arg);
            using var dnsProcess = Process.Start(start)!;
            try
            {
                await Task.Delay(1000);
                if (dnsProcess.HasExited)
                    throw new Exception("Core DNS runtime failed: " + await dnsProcess.StandardError.ReadToEndAsync());
                passed.Add("DNS runtime starts without TUN, listeners or VPN handshake");
            }
            finally { if (!dnsProcess.HasExited) { dnsProcess.Kill(); await dnsProcess.WaitForExitAsync(); } }
            var runtimeConfig = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
            // Exercise the same hidden-console lifecycle as the GUI, without any adapter or listener.
            try
            {
                Store.WriteJson(config, runtimeConfig);
                var info = new ProcessStartInfo(Store.Bin) { UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in new[] { "--disable-color", "run", "-c", config })
                    info.ArgumentList.Add(a);
                using var process = Process.Start(info)!;
                await Task.Delay(800);
                Check(!process.HasExited, "Hidden core starts");
                await Native.Stop(process);
                Check(process.HasExited && process.ExitCode == 0, "Hidden core stops gracefully with Ctrl+C");
            }
            finally { File.Delete(config); }
            try
            {
                Store.WriteJson(config, runtimeConfig);
                var info = new ProcessStartInfo(Store.Bin) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "--disable-color", "run", "-c", config })
                    info.ArgumentList.Add(a);
                using var process = Process.Start(info)!;
                using (var job = new CoreJob(process))
                {
                    await Task.Delay(500);
                    Check(!process.HasExited, "Owned core starts in Windows Job");
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(process.HasExited, "Windows Job terminates orphan core");
            }
            finally { File.Delete(config); }

            Store.Atomic(Path.Combine(Store.Data, "release-tests.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                Passed = passed.Count,
                Tests = passed
            }, Store.JsonOptions));
        }
        finally { foreach (string path in new[] { ruleJson, ruleSrs, config }) if (File.Exists(path)) File.Delete(path); }
    }
}
