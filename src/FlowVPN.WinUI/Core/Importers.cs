using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlowVPN;

public static class Importers
{
    const int MaxOpenVpnBytes = 4 * 1024 * 1024;
    const int MaxAccessKeyLength = 65536;
    public static IReadOnlyList<string> ShadowsocksMethods { get; } = Array.AsReadOnly<string>(
        ["chacha20-ietf-poly1305", "aes-128-gcm", "aes-256-gcm", "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305"]);
    static readonly TimeSpan ParseTimeout = TimeSpan.FromMilliseconds(250);
    static string ReadMaterial(string path)
    {
        if (!LocalPathPolicy.TryFile(path, out string full))
            throw new InvalidDataException("Импортируйте профиль и ключи из существующих локальных файлов. Сетевые пути и ссылки на них не поддерживаются.");
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxOpenVpnBytes)
            throw new InvalidDataException("Файл OpenVPN больше допустимого размера.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    static JsonArray Arr(IEnumerable<string> values) => new(values.Select(x => JsonValue.Create(x)).ToArray());
    public static VpnSource OpenVpn(string path)
    {
        var raw = ReadMaterial(path);
        return ParseOpenVpn(raw, path, Path.GetDirectoryName(Path.GetFullPath(path))!);
    }
    public static VpnSource OpenVpnText(string raw, string? name = null) =>
        ParseOpenVpn(raw, "OpenVPN · вставленный текст", null, name);

    static VpnSource ParseOpenVpn(string raw, string origin, string? materialBaseDirectory, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxOpenVpnBytes || Encoding.UTF8.GetByteCount(raw) > MaxOpenVpnBytes)
            throw new InvalidDataException("Вставьте непустую конфигурацию OpenVPN размером не больше 4 МиБ.");
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
            if (materialBaseDirectory == null && directives.ContainsKey(key) && Value(key) != "[inline]")
                throw new InvalidDataException($"Вставленный текст должен содержать блок <{key}> вместо пути к файлу. Для внешних сертификатов и ключей импортируйте файл .ovpn.");
            if (blocks.TryGetValue(key, out var v))
                return !string.IsNullOrWhiteSpace(v) ? v : throw new InvalidDataException($"Блок <{key}> не должен быть пустым.");
            if (materialBaseDirectory == null)
                throw new InvalidDataException($"Добавьте в текст блок <{key}>. Если сертификаты и ключи хранятся отдельно, импортируйте файл .ovpn.");
            var f = Value(key);
            if (f.Length == 0)
                throw new Exception($"В OpenVPN-профиле отсутствует {key}.");
            return ReadMaterial(Path.GetFullPath(f, materialBaseDirectory));
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
        string configuration = endpoint.ToJsonString();
        return new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? remotes[0][0] + " · OpenVPN" : name.Trim(),
            Kind = "OpenVPN",
            Origin = origin,
            ConnectionJson = configuration,
            Fingerprint = Store.Hash(configuration)
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
        if (access.Length > MaxAccessKeyLength)
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
        if (uri.AbsolutePath is not ("" or "/") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("Ключ Outline содержит неподдерживаемый путь сервера.");
        return ShadowsocksSource(uri.Host, uri.Port, method, password, name, "Outline", "Outline access key");
    }

    public static VpnSource Shadowsocks(string host, int port, string method, string password, string? name = null) =>
        ShadowsocksSource(host, port, method, password, name, "Shadowsocks", "Shadowsocks · ручная настройка");

    static VpnSource ShadowsocksSource(string host, int port, string method, string password, string? name, string label, string origin)
    {
        host = ShadowsocksHost(host);
        if (port is < 1 or > 65535)
            throw new InvalidDataException("Укажите порт от 1 до 65535.");
        if (!ShadowsocksMethods.Contains(method, StringComparer.Ordinal))
            throw new InvalidDataException("Метод Shadowsocks не поддерживается приложением.");
        if (password == null || password.Length > MaxAccessKeyLength)
            throw new InvalidDataException("Пароль Shadowsocks превышает допустимый размер.");
        // Credentials are opaque: URI syntax, whitespace and control characters are not rewritten.
        var node = new JsonObject { ["type"] = "shadowsocks", ["tag"] = "vpn", ["server"] = host, ["server_port"] = port, ["method"] = method, ["password"] = password, ["domain_resolver"] = "dns-direct" };
        string configuration = node.ToJsonString();
        return new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? (host.Contains(':') ? "[" + host + "]" : host) + " · " + label : name,
            Kind = "Outline",
            Origin = origin,
            ConnectionJson = configuration,
            Fingerprint = Store.Hash(configuration)
        };
    }

    static string ShadowsocksHost(string host)
    {
        host = host?.Trim() ?? "";
        if (host.Length is 0 or > 253 || host.Any(char.IsWhiteSpace) || host.Any(char.IsControl) ||
            host.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0)
            throw new InvalidDataException("Укажите доменное имя или IP-адрес сервера без протокола, пути и порта.");
        if (host.StartsWith('[') || host.EndsWith(']'))
        {
            if (!host.StartsWith('[') || !host.EndsWith(']') ||
                !IPAddress.TryParse(host[1..^1], out var address) || address.AddressFamily != AddressFamily.InterNetworkV6)
                throw new InvalidDataException("Некорректный IPv6-адрес сервера.");
            return address.ToString();
        }
        if (IPAddress.TryParse(host, out var ip))
            return ip.ToString();
        try { host = new IdnMapping().GetAscii(host).ToLowerInvariant(); }
        catch (ArgumentException) { throw new InvalidDataException("Некорректное доменное имя сервера."); }
        if (host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns)
            throw new InvalidDataException("Некорректное доменное имя или IP-адрес сервера.");
        return host;
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
        return (count, notes);
    }
    public static async Task<VpnSource> DynamicOutline(string input, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        input = input.Trim();
        if (!input.StartsWith("ssconf://", StringComparison.OrdinalIgnoreCase))
            return Outline(input);
        var address = "https://" + input[9..].Split('#')[0];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
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
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("JSON Shadowsocks должен содержать объект с параметрами подключения.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!fields.Add(property.Name))
                throw new InvalidDataException("JSON Shadowsocks содержит повторяющиеся поля. Удалите дубликаты в конфигурации или обратитесь к поставщику ключа.");
        if (fields.Contains("error"))
            throw new InvalidDataException("Сервис Outline вернул сообщение об ошибке вместо параметров подключения. Проверьте срок действия ключа и доступность услуги у поставщика.");
        if (fields.Contains("transport") || fields.Contains("$type"))
            throw new InvalidDataException("Этот ключ Outline использует расширенный транспорт, который эта версия FlowVPN не поддерживает. Используйте совместимый ключ Shadowsocks или подключение OpenVPN.");
        if (fields.Any(field => field is not ("server" or "server_port" or "method" or "password" or "prefix")))
            throw new InvalidDataException("JSON Shadowsocks содержит неподдерживаемые поля. Импорт остановлен, чтобы не изменить параметры подключения.");
        if (root.TryGetProperty("prefix", out var prefix))
        {
            if (prefix.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Поле prefix в JSON Shadowsocks должно быть строкой.");
            // Only the exact empty string is a no-op. Even whitespace changes transport bytes.
            if (prefix.GetString()!.Length != 0)
                throw new InvalidDataException("Ключ Outline использует непустой префикс Shadowsocks. Эта версия движка его не поддерживает. Используйте совместимый ключ без префикса или подключение OpenVPN.");
        }
        string Value(string key, bool allowEmpty = false) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } result && (allowEmpty || result.Length > 0)
                ? result : throw new InvalidDataException("Некорректное поле JSON Shadowsocks: " + key);
        if (!root.TryGetProperty("server_port", out var portValue) || portValue.ValueKind != JsonValueKind.Number || !portValue.TryGetInt32(out int port) || port is < 1 or > 65535)
            throw new InvalidDataException("Некорректный порт JSON Shadowsocks.");
        return ShadowsocksSource(Value("server"), port, Value("method"), Value("password", allowEmpty: true), null, "Shadowsocks", "Shadowsocks · JSON");
    }
}
