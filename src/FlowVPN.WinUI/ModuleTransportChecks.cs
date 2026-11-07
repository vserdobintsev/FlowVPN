using System.Text;
using System.Text.Json;

namespace FlowVPN;

internal static class ModuleTransportChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string key = "ss://YWVzLTEyOC1nY206Zml4dHVyZQ==@127.0.0.1:23456";
        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or JsonException) { rejected = true; }
            check(rejected, label);
        }
        var plain = Importers.Outline(key);
        check(ModuleProcess.ServerAddress([System.Net.IPAddress.Parse("::ffff:192.0.2.1")]).ToString() == "192.0.2.1",
            "IPv4-mapped server addresses use IPv4 transport and interface-index semantics");
        var prefix = Importers.Outline(key + "?prefix=POST%20");
        var parameters = ModuleTransport.OutlineParameters(prefix);
        check(parameters.PrefixBase64 == Convert.ToBase64String("POST "u8) && prefix.ConnectionJson == plain.ConnectionJson,
            "URI prefix is preserved separately without widening privileged Shadowsocks source fields");
        check(Importers.Outline(key + "?prefix=").Transport == null, "Empty URI prefixes need no module");
        check(ModuleTransport.OutlineParameters(Importers.Outline(key + "?prefix=%00%C2%A8%C3%BF+")).PrefixBase64 == "AKj/IA==",
            "UTF-8 URI decoding preserves Outline's one-byte prefix semantics including NUL and Latin-1");
        foreach (string query in new[] { "prefix=%FF", "prefix=%", "prefix=%G0", "prefix=%C4%80", "prefix=123456789", "prefix=x&prefix=y", "prefix=x&PREFIX=y", "plugin=x", "prefix" })
            Reject(() => Importers.Outline(key + "?" + query), "Malformed or unsupported URI transport options never disappear: " + query);
        var copied = JsonSerializer.Deserialize<VpnSource>(JsonSerializer.Serialize(prefix))!;
        check(Store.SameSource(prefix, copied) && Equals(prefix.Transport, copied.Transport), "Typed module configuration survives source serialization and deduplication");
        check(!Store.SameSource(plain, prefix) && !Store.SameSource(prefix, Importers.Outline(key + "?prefix=GET%20")),
            "Plain sources and different prefixes remain distinct connections");
        foreach (string method in new[] { "aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305" })
        {
            int maximum = method == "aes-128-gcm" ? 8 : 16;
            var source = Importers.Shadowsocks("127.0.0.1", 23456, method, "fixture");
            check(ModuleTransport.WithOutlinePrefix(source, new string('x', maximum)).Transport != null,
                "Prefix policy retains at least eight random salt bytes: " + method);
            Reject(() => ModuleTransport.WithOutlinePrefix(source, new string('x', maximum + 1)), "Oversized prefixes are rejected without truncation: " + method);
        }
        Reject(() => ModuleTransport.WithOutlinePrefix(Importers.Shadowsocks("127.0.0.1", 23456, "2022-blake3-aes-128-gcm", "fixture"), "x"),
            "Unsupported prefix cipher never falls back to ordinary Shadowsocks");
        foreach (var transport in new[]
        {
            prefix.Transport! with { ModuleId = "arbitrary-module" },
            prefix.Transport! with { SchemaVersion = 2 },
            prefix.Transport! with { Outline = null },
            prefix.Transport! with { Outline = new() { PrefixBase64 = "" } },
            prefix.Transport! with { Outline = new() { PrefixBase64 = "eA==\n" } }
        })
        {
            var altered = JsonSerializer.Deserialize<VpnSource>(JsonSerializer.Serialize(prefix))!;
            altered.Transport = transport;
            Reject(() => ModuleTransport.RequiredModule(altered), "Unknown, malformed or noncanonical module descriptors cannot start");
        }
        int Ready(string json) => ModuleProcess.ReadReady(new MemoryStream(Encoding.UTF8.GetBytes(json)), CancellationToken.None).GetAwaiter().GetResult();
        check(Ready("{\"protocolVersion\":1,\"port\":23456}\n") == 23456, "Module readiness uses a bounded versioned loopback-port handshake");
        foreach (string json in new[]
        {
            "{\"protocolVersion\":1,\"port\":23456}", "{}\n", "null\n",
            "{\"protocolVersion\":2,\"port\":23456}\n", "{\"protocolVersion\":1,\"port\":80}\n",
            "{\"protocolVersion\":\"1\",\"port\":23456}\n", "{\"protocolVersion\":1,\"port\":null}\n",
            "{\"protocolVersion\":1,\"port\":65536}\n", "{\"protocolVersion\":1,\"port\":23456,\"port\":23457}\n",
            "{\"protocolVersion\":1,\"port\":23456,\"path\":\"arbitrary.exe\"}\n", new string('x', 1024)
        }) Reject(() => Ready(json), "Invalid module readiness is rejected before configuring the VPN");
    }
}
