using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlowVPN;

public static class Importers
{
    static readonly TimeSpan ParseTimeout = TimeSpan.FromMilliseconds(250);
    static string ReadMaterial(string path)
    {
        if (!LocalPathPolicy.TryFile(path, out string full))
            throw new InvalidDataException("Импортируйте профиль и ключи из существующих локальных файлов. Сетевые пути и ссылки на них не поддерживаются.");
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Файл OpenVPN больше допустимого размера.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    static JsonArray Arr(IEnumerable<string> values) => new(values.Select(x => JsonValue.Create(x)).ToArray());
    public static VpnSource OpenVpn(string path)
    {
        var raw = ReadMaterial(path);
        var blocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(raw, @"<([\w-]+)>\s*([\s\S]*?)\s*</\1>", RegexOptions.None, ParseTimeout))
            blocks[m.Groups[1].Value] = m.Groups[2].Value + "\n";
        string plain = Regex.Replace(raw, @"<([\w-]+)>[\s\S]*?</\1>", "", RegexOptions.None, ParseTimeout);
        var directives = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in plain.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#') || t.StartsWith(';'))
                continue;
            var parts = OpenVpnSyntax.Tokens(t);
            if (parts.Length == 0)
                continue;
            if (parts[0].StartsWith("--", StringComparison.Ordinal))
                parts[0] = parts[0][2..];
            if (!directives.ContainsKey(parts[0]))
                directives[parts[0]] = [];
            directives[parts[0]].Add(parts[1..]);
        }
        string Value(string key, string fallback = "") => directives.TryGetValue(key, out var list) ? list.Last().FirstOrDefault() ?? fallback : fallback;
        string Material(string key)
        {
            if (blocks.TryGetValue(key, out var v))
                return v;
            var f = Value(key);
            if (f.Length == 0)
                throw new Exception($"В OpenVPN-профиле отсутствует {key}.");
            return ReadMaterial(Path.GetFullPath(f, Path.GetDirectoryName(Path.GetFullPath(path))!));
        }
        var blocked = new[] { "tls-auth", "tls-crypt", "tls-crypt-v2", "auth-user-pass", "pkcs12", "secret", "http-proxy", "socks-proxy", "compress", "comp-lzo", "script-security", "up", "down", "plugin", "remote-random-hostname", "tls-verify", "route-up", "route-pre-down", "tls-remote", "verify-hash", "crl-verify", "remote-cert-ku", "remote-cert-eku", "tls-cipher", "tls-ciphersuites", "tls-version-min", "tls-version-max", "peer-fingerprint", "tls-cert-profile", "tls-groups", "ecdh-curve", "ns-cert-type", "capath", "config", "connection", "tls-export-cert", "ipchange", "x509-username-field", "compat-mode" };
        var unsupported = blocked.Where(k => directives.ContainsKey(k) || blocks.ContainsKey(k)).ToArray();
        if (unsupported.Length > 0)
            throw new InvalidDataException("Для этого OpenVPN-профиля пока не поддерживаются: " + string.Join(", ", unsupported) + ". Импорт остановлен, параметры не отброшены.");
        if (Value("dev", "tun").StartsWith("tap", StringComparison.OrdinalIgnoreCase))
            throw new Exception("TAP-профили не поддерживаются.");
        if (directives.TryGetValue("setenv", out var environment) && environment.Any(v => v.FirstOrDefault() == "opt"))
            throw new InvalidDataException("Обёртка setenv opt пока не поддерживается. Импорт остановлен, параметры не отброшены.");
        if (directives.ContainsKey("remote-cert-tls") && Value("remote-cert-tls") != "server")
            throw new InvalidDataException("Для клиентского профиля требуется remote-cert-tls server.");
        if (!directives.TryGetValue("remote", out var remotes) || remotes.Count == 0 || remotes.Any(r => r.Length is < 1 or > 3 || string.IsNullOrWhiteSpace(r[0])))
            throw new InvalidDataException("Укажите remote: адрес, необязательный порт и клиентский протокол.");
        var proto = OpenVpnSyntax.Network(Value("proto", "udp"));
        var servers = new JsonArray();
        foreach (var remote in remotes)
        {
            string network = remote.Length > 2 ? OpenVpnSyntax.Network(remote[2]) : proto;
            servers.Add(new JsonObject { ["server"] = remote[0], ["server_port"] = OpenVpnSyntax.Port(remote.Length > 1 ? remote[1] : Value("rport", Value("port", "1194"))), ["network"] = network });
        }
        var ca = Material("ca");
        var cert = Material("cert");
        var key = Material("key");
        if (key.Contains("ENCRYPTED"))
            throw new Exception("Нужен профиль с незашифрованным клиентским ключом; пароль ключа не поддерживается.");
        var tls = new JsonObject { ["certificate"] = Arr([ca]), ["client_certificate"] = Arr([cert]), ["client_key"] = Arr([key]), ["remote_certificate_tls"] = "server" };
        if (directives.TryGetValue("verify-x509-name", out var verify))
        {
            var arguments = verify[^1];
            if (arguments.Length is < 1 or > 2 || string.IsNullOrWhiteSpace(arguments[0]))
                throw new InvalidDataException("verify-x509-name требует непустое имя сертификата.");
            string type = arguments.ElementAtOrDefault(1) ?? "subject";
            if (type is not ("subject" or "name" or "name-prefix"))
                throw new InvalidDataException("Неизвестный тип verify-x509-name.");
            tls["server_name"] = arguments[0];
            tls["server_name_type"] = type;
        }
        var endpoint = new JsonObject { ["type"] = "openvpn-client", ["tag"] = "vpn", ["mode"] = "tls", ["network"] = proto, ["servers"] = servers, ["tls"] = tls, ["remote_random"] = directives.ContainsKey("remote-random"), ["route_no_pull"] = true, ["redirect_gateway"] = false, ["system"] = false, ["domain_resolver"] = "dns-direct" };
        var cipher = Value("data-ciphers-fallback", Value("cipher"));
        if (cipher != "")
            endpoint["data_ciphers_fallback"] = cipher;
        if (Value("data-ciphers") != "")
            endpoint["data_ciphers"] = Arr(Value("data-ciphers").Split(':'));
        if (Value("auth") != "")
            endpoint["auth"] = Value("auth");
        return new()
        {
            Name = remotes[0][0] + " · OpenVPN",
            Kind = "OpenVPN",
            Origin = path,
            ConnectionJson = endpoint.ToJsonString(),
            Fingerprint = Store.Hash(endpoint.ToJsonString())
        };
    }
    static string Base64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '=')));
    }
    public static VpnSource Outline(string access, string? name = null)
    {
        access = access.Trim();
        if (access.Length > 65536)
            throw new InvalidDataException("Ключ Outline слишком длинный.");
        if (!access.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Ожидается ключ ss://. Динамический ssconf:// можно импортировать отдельной кнопкой.");
        var fragment = access.IndexOf('#');
        if (name == null && fragment >= 0)
            name = Uri.UnescapeDataString(access[(fragment + 1)..]);
        var body = fragment >= 0 ? access[5..fragment] : access[5..];
        var qi = body.IndexOf('?');
        if (qi >= 0)
        {
            var query = body[(qi + 1)..];
            if (query.Split('&').Any(x => x.Length > 0 && !x.StartsWith("outline=", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Этот ключ использует расширенные параметры транспорта (например, prefix/plugin), которые пока не поддерживаются.");
            body = body[..qi];
        }
        body = body.TrimEnd('/');
        if (!body.Contains('@'))
            body = Base64(body);
        int at = body.LastIndexOf('@');
        if (at < 0)
            throw new Exception("В ключе Outline отсутствует адрес сервера.");
        string auth = body[..at];
        if (!auth.Contains(':'))
            auth = Base64(auth);
        else
            auth = Uri.UnescapeDataString(auth);
        int colon = auth.IndexOf(':');
        if (colon < 1)
            throw new Exception("Некорректная авторизация Outline.");
        string method = auth[..colon], password = auth[(colon + 1)..];
        var uri = new Uri("tcp://" + body[(at + 1)..]);
        if (uri.AbsolutePath is not ("" or "/") || uri.UserInfo.Length > 0)
            throw new InvalidDataException("Ключ Outline содержит неподдерживаемый путь сервера.");
        if (uri.Port < 1 || uri.Port > 65535)
            throw new Exception("Некорректный порт Outline.");
        var supported = new[] { "chacha20-ietf-poly1305", "aes-128-gcm", "aes-256-gcm", "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305" };
        if (!supported.Contains(method))
            throw new Exception("Метод Shadowsocks не поддерживается приложением: " + method);
        var node = new JsonObject { ["type"] = "shadowsocks", ["tag"] = "vpn", ["server"] = uri.Host.Trim('[', ']'), ["server_port"] = uri.Port, ["method"] = method, ["password"] = password, ["domain_resolver"] = "dns-direct" };
        return new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? uri.Host + " · Outline" : name,
            Kind = "Outline",
            Origin = "Outline access key",
            ConnectionJson = node.ToJsonString(),
            Fingerprint = Store.Hash(node.ToJsonString())
        };
    }
    static IEnumerable<(string Key, string? Name)> Keys(JsonNode? n)
    {
        if (n is JsonObject o)
        {
            if (o["accessKey"] is JsonValue v && v.TryGetValue<string>(out var k))
                yield return (k, o["name"]?.ToString());
            foreach (var p in o)
                foreach (var item in Keys(p.Value))
                    yield return item;
        }
        else if (n is JsonArray a)
            foreach (var c in a)
                foreach (var item in Keys(c))
                    yield return item;
    }
    public static (int Added, List<string> Notes) Installed(Settings settings)
    {
        int count = 0;
        var notes = new List<string>();
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!LocalPathPolicy.TryDirectory(roaming, out string localRoaming, allowLocalLinks: false))
            return (0, ["Папка данных клиентов недоступна для безопасного локального импорта."]);
        string ovpn = Path.Combine(localRoaming, "OpenVPN Connect", "profiles");
        if (LocalPathPolicy.TryDirectory(ovpn, out string localOpenVpn, allowLocalLinks: false))
            foreach (var f in Directory.EnumerateFiles(localOpenVpn, "*.ovpn", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                try
                {
                    if (Store.AddSource(settings, OpenVpn(f)))
                        count++;
                }
                catch (Exception e) { notes.Add(Path.GetFileName(f) + ": " + e.Message); }
            }
        var outline = Path.Combine(localRoaming, "Outline", "Local Storage", "leveldb");
        if (LocalPathPolicy.TryDirectory(outline, out string localOutline, allowLocalLinks: false))
            try
            {
                var keys = LevelStore.ServerValues(localOutline).SelectMany(s => Keys(JsonNode.Parse(s))).ToList();
                foreach (var entry in keys)
                    try
                    {
                        var source = DynamicOutline(entry.Key).GetAwaiter().GetResult();
                        if (!string.IsNullOrWhiteSpace(entry.Name))
                            source.Name = entry.Name;
                        if (Store.AddSource(settings, source))
                            count++;
                    }
                    catch (Exception e) { notes.Add((entry.Name ?? "Outline") + ": " + e.Message); }
                if (keys.Count == 0)
                    notes.Add("Outline: в локальном хранилище не найдены сохранённые серверы.");
            }
            catch (Exception e) { notes.Add("Не удалось прочитать Outline: " + e.Message); }
        Store.SeedProfiles(settings);
        return (count, notes);
    }
    public static async Task<VpnSource> DynamicOutline(string input)
    {
        input = input.Trim();
        if (!input.StartsWith("ssconf://", StringComparison.OrdinalIgnoreCase))
            return Outline(input);
        var address = "https://" + input[9..].Split('#')[0];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var client = BoundedHttp.Client(TimeSpan.FromSeconds(25));
        var text = Encoding.UTF8.GetString(await BoundedHttp.Download(client, address, 1024 * 1024, deadline.Token));
        var source = DynamicPayload(text);
        source.DynamicKey = input;
        source.Origin = "Outline · динамический ключ";
        source.Fingerprint = Store.Hash(input);
        return source;
    }
    internal static VpnSource DynamicPayload(string text)
    {
        if (text.TrimStart().StartsWith("ss://"))
            return Outline(text.Trim());
        if (JsonNode.Parse(text) is not JsonObject n || n.Any(p => p.Key is not ("server" or "server_port" or "method" or "password")))
            throw new InvalidDataException("Этот ssconf использует расширенный формат. Параметры транспорта не были отброшены: импорт остановлен.");
        string Value(string key) => n[key] is JsonValue value && value.TryGetValue<string>(out var result) && result.Length > 0
            ? result : throw new InvalidDataException("Некорректное поле динамического ключа Outline: " + key);
        if (n["server_port"] is not JsonValue portValue || !portValue.TryGetValue<int>(out int port) || port is < 1 or > 65535)
            throw new InvalidDataException("Некорректный порт динамического ключа Outline.");
        string host = Value("server");
        if (Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown)
            throw new InvalidDataException("Некорректный адрес динамического ключа Outline.");
        if (host.Contains(':') && !host.StartsWith('['))
            host = "[" + host + "]";
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(Value("method") + ":" + Value("password")));
        return Outline("ss://" + auth + "@" + host + ":" + port);
    }
}
