using System.Text.Json.Nodes;

namespace FlowVPN;

internal static class ImportChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(OpenVpnSyntax.Tokens("remote example.test 1194 # note").SequenceEqual(new[] { "remote", "example.test", "1194" }), "Inline comments do not turn a TCP remote into UDP");
        check(OpenVpnSyntax.Tokens("ca \"C:\\\\Example App\\\\ca.pem\"").SequenceEqual(new[] { "ca", @"C:\Example App\ca.pem" }), "Quoted OpenVPN Windows paths decode doubled backslashes");
        check(OpenVpnSyntax.Tokens(@"ca Example\ App/ca.pem")[1] == "Example App/ca.pem", "Escaped whitespace remains in one OpenVPN argument");
        check(OpenVpnSyntax.Tokens("verify-x509-name 'CN=Example #1' subject ; note")[1] == "CN=Example #1", "Comment markers inside quoted certificate names are preserved");
        foreach (string line in new[] { "ca \"unterminated", "ca invalid\\q", "ca \"a\"b" })
        {
            bool rejected = false;
            try
            {
                OpenVpnSyntax.Tokens(line);
            }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Malformed OpenVPN quoting or escaping is rejected");
        }
        string folder = Path.Combine(Store.Run, "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "profile.ovpn");
            const string materials = "\n<ca>\nfixture-ca\n</ca>\n<cert>\nfixture-cert\n</cert>\n<key>\nfixture-key\n</key>\n";
            const string start = "client\nproto tcp6-client\nremote ::1 1194 # server\n";
            File.WriteAllText(file, start + "verify-x509-name 'CN=Fixture' subject\n" + materials);
            var source = Importers.OpenVpn(file);
            var node = JsonNode.Parse(source.ConnectionJson)!;
            check(node["tls"]?["server_name"]?.ToString() == "CN=Fixture" && node["tls"]?["server_name_type"]?.ToString() == "subject", "OpenVPN certificate-name checks use supported sing-box fields");
            check(node["network"]?.ToString() == "tcp6" && node["servers"]?[0]?["network"]?.ToString() == "tcp6", "OpenVPN import preserves TCP and address-family selection");
            foreach (string directive in new[] { "peer-fingerprint 00:11", "--peer-fingerprint 00:11", "tls-cert-profile preferred", "config nested.ovpn", "setenv opt peer-fingerprint 00:11", "remote", "remote example.test 70000", "proto unsupported", "verify-x509-name", "verify-x509-name example bogus" })
            {
                File.WriteAllText(file, start + directive + materials);
                bool rejected = false;
                try
                {
                    Importers.OpenVpn(file);
                }
                catch (InvalidDataException) { rejected = true; }
                check(rejected, "Unsafe or malformed OpenVPN option is not silently dropped: " + directive.Split(' ')[0]);
            }
            string ca = Path.Combine(folder, "ca.pem");
            File.WriteAllText(ca, "first-ca");
            File.WriteAllText(file, start + "ca ca.pem\n<cert>\nfixture-cert\n</cert>\n<key>\nfixture-key\n</key>\n");
            string first = Importers.OpenVpn(file).Fingerprint;
            File.WriteAllText(ca, "rotated-ca");
            check(Importers.OpenVpn(file).Fingerprint != first, "External OpenVPN material changes are detected even when the profile text is unchanged");
        }
        finally { Directory.Delete(folder, true); }
        const string basicOutline = "{\"server\":\"::1\",\"server_port\":23456,\"method\":\"chacha20-ietf-poly1305\",\"password\":\"fixture:password\"}";
        var outline = JsonNode.Parse(Importers.DynamicPayload(basicOutline).ConnectionJson)!;
        check(outline["server"]?.ToString() == "::1" && outline["password"]?.ToString() == "fixture:password", "Dynamic Outline JSON preserves IPv6 hosts and colon-containing passwords");
        var withoutPrefix = Importers.DynamicPayload(basicOutline);
        var emptyPrefix = Importers.DynamicPayload(basicOutline.Replace("}", ",\"prefix\":\"\"}"));
        check(emptyPrefix.ConnectionJson == withoutPrefix.ConnectionJson && emptyPrefix.Fingerprint == withoutPrefix.Fingerprint &&
            JsonNode.Parse(emptyPrefix.ConnectionJson)!["prefix"] == null, "An exactly empty Outline prefix is a no-op and is never forwarded to the engine");
        string Failure(string payload)
        {
            try { Importers.DynamicPayload(payload); }
            catch (InvalidDataException error) { return error.Message; }
            return "";
        }
        foreach (string value in new[] { "\" \"", "\"POST \"", "\"\\u0000\"" })
        {
              var prefixed = Importers.DynamicPayload(basicOutline.Replace("}", ",\"prefix\":" + value + "}"));
              check(ModuleTransport.RequiredModule(prefixed) == ModuleTransport.OutlineModuleId && prefixed.ConnectionJson == withoutPrefix.ConnectionJson,
                  "Nonempty Outline prefixes, including whitespace and control bytes, use a typed module descriptor outside the engine source schema");
        }
        foreach (string value in new[] { "null", "true", "42", "[]", "{}" })
            check(Failure(basicOutline.Replace("}", ",\"prefix\":" + value + "}")).Contains("должно быть строкой", StringComparison.Ordinal), "An Outline prefix must be a string, never an implicitly empty JSON value");
        foreach (string field in new[] { "server", "server_port", "method", "password" })
            check(Failure(basicOutline.Insert(1, "\"" + field + "\":\"duplicate\",")).Contains("повторяющиеся", StringComparison.Ordinal), "Repeated required Outline fields get a duplicate-field error: " + field);
        check(Failure(basicOutline.Replace("}", ",\"plugin\":\"fixture-secret\"}")).Contains("неподдерживаемые", StringComparison.Ordinal), "Unknown transport parameters remain rejected separately from duplicate fields");
        string providerError = Failure("{\"error\":{\"message\":\"fixture-secret\",\"details\":\"https://fixture.invalid/private\"}}");
        check(providerError.Contains("Сервис Outline", StringComparison.Ordinal) && !providerError.Contains("fixture", StringComparison.Ordinal), "Provider errors are identified without displaying untrusted provider text");
        string transportError = Failure("{\"transport\":{\"$type\":\"fixture-secret\"}}");
        check(transportError.Contains("расширенный транспорт", StringComparison.Ordinal) && !transportError.Contains("fixture", StringComparison.Ordinal), "Advanced Outline transports get an explicit non-disclosing compatibility error");
          foreach (string bad in new[] { basicOutline.Replace("}", ",\"prefix\":\"\\u0100\"}"), basicOutline.Replace("23456", "\"23456\""), basicOutline.Replace("::1", "example.test/path") })
        {
            bool rejected = false;
            try
            {
                Importers.DynamicPayload(bad);
            }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Unsupported transport fields or malformed dynamic Outline values are rejected");
        }
    }
}
