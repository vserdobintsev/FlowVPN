using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace FlowVPN;

public record Uplink(string Name, string Dns);
public static class ConfigBuilder
{
    static JsonArray A(IEnumerable<string> values) => new(values.Select(x => JsonValue.Create(x)).ToArray());
    public static Uplink Detect()
    {
        var all = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)).Where(n => !System.Text.RegularExpressions.Regex.IsMatch(n.Name + " " + n.Description, "TAP|TUN|DCO|VPN|Virtual|Hyper-V|VMware|WireGuard|Outline", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
        foreach (var n in all.OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1))
        {
            var ip = n.GetIPProperties();
            if (!ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                continue;
            string dns = ip.DnsAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "1.1.1.1";
            return new(n.Name, dns);
        }
        throw new Exception("Не найдено физическое подключение с IPv4-шлюзом. Подключите Wi-Fi или Ethernet.");
    }
    public static JsonObject Build(Settings settings, VpnProfile profile, Uplink link, int proxyPort, CoreControl? control = null, ProxyAccess? proxyAccess = null)
    {
        var baseDomains = settings.BaseRulesEnabled ? DomainRepository.Current(settings.BaseDomainCategory) : null;
        var domains = DomainCoverage.Effective(profile.Domains, baseDomains);
        var source = settings.Sources.FirstOrDefault(s => s.Id == profile.SourceId) ?? throw new Exception("Выберите источник подключения.");
        var target = JsonNode.Parse(source.ConnectionJson)!.AsObject();
        target["tag"] = "vpn";
        var dnsRules = new JsonArray();
        if (domains.Count > 0)
            dnsRules.Add(new JsonObject { ["domain_suffix"] = A(domains), ["server"] = "dns-vpn" });
        if (profile.Apps.Count > 0)
            dnsRules.Add(new JsonObject { ["process_path_regex"] = A(profile.Apps.Select(a => a.Pattern)), ["server"] = profile.Mode == "exclude" && !settings.BaseRulesEnabled ? "dns-direct" : "dns-vpn" });
        var rules = new JsonArray { new JsonObject { ["inbound"] = A(["manager"]), ["action"] = "route", ["outbound"] = "vpn" }, new JsonObject { ["action"] = "sniff" }, new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" } };
        if (settings.BaseRulesEnabled)
            rules.Add(new JsonObject { ["rule_set"] = A(["ru-blocked"]), ["action"] = "route", ["outbound"] = "vpn" });
        if (baseDomains != null)
            rules.Add(new JsonObject { ["rule_set"] = A(["ru-domains"]), ["action"] = "route", ["outbound"] = "vpn" });
        rules.Add(new JsonObject { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "direct" });
        if (domains.Count > 0)
            rules.Add(new JsonObject { ["domain_suffix"] = A(domains), ["action"] = "route", ["outbound"] = "vpn" });
        if (profile.Apps.Count > 0 && profile.Mode != "all")
            rules.Add(new JsonObject { ["process_path_regex"] = A(profile.Apps.Select(a => a.Pattern)), ["action"] = "route", ["outbound"] = profile.Mode == "exclude" ? "direct" : "vpn" });
        var route = new JsonObject { ["auto_detect_interface"] = false, ["default_interface"] = link.Name, ["default_domain_resolver"] = "dns-direct", ["rules"] = rules, ["final"] = profile.Mode == "include" ? "direct" : "vpn" };
        if (settings.BaseRulesEnabled)
            route["rule_set"] = new JsonArray { new JsonObject { ["type"] = "local", ["tag"] = "ru-blocked", ["format"] = "binary", ["path"] = Path.Combine(Store.Rules, "ru-blocked.srs") } };
        if (baseDomains != null)
            route["rule_set"]!.AsArray().Add(new JsonObject { ["type"] = "local", ["tag"] = "ru-domains", ["format"] = "source", ["path"] = DomainRepository.RulePath(settings.BaseDomainCategory) });
        var outbounds = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = "direct" } };
        var endpoints = new JsonArray();
        if (source.Kind == "OpenVPN")
            endpoints.Add(target);
        else
            outbounds.Add(target);
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true, ["output"] = Path.Combine(Store.ProfileLogs(profile.Id), "sing-box.log") },
            ["dns"] = new JsonObject { ["servers"] = new JsonArray { new JsonObject { ["type"] = "udp", ["tag"] = "dns-direct", ["server"] = link.Dns, ["server_port"] = 53 }, new JsonObject { ["type"] = "https", ["tag"] = "dns-vpn", ["server"] = "1.1.1.1", ["server_port"] = 443, ["path"] = "/dns-query", ["detour"] = "vpn", ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "cloudflare-dns.com" } } }, ["rules"] = dnsRules, ["final"] = settings.BaseRulesEnabled || profile.Mode != "include" ? "dns-vpn" : "dns-direct", ["strategy"] = "ipv4_only" },
            ["inbounds"] = new JsonArray { new JsonObject { ["type"] = "tun", ["tag"] = "flow-tun", ["interface_name"] = "FlowVPN", ["address"] = A(["172.30.254.1/30", "fd72:9b63:7a11::1/126"]), ["mtu"] = 1400, ["auto_route"] = true, ["strict_route"] = false, ["dns_mode"] = "native", ["stack"] = "mixed" }, new JsonObject { ["type"] = "mixed", ["tag"] = "manager", ["listen"] = "127.0.0.1", ["listen_port"] = proxyPort } },
            ["endpoints"] = endpoints,
            ["outbounds"] = outbounds,
            ["route"] = route
        };
        config["inbounds"]![1]!["users"] = (proxyAccess ?? new ProxyAccess()).Users();
        if (control != null)
            config["experimental"] = control.Configuration();
        return config;
    }
}
public static class CoreCli
{
    public static async Task<string> Run(params string[] args)
    {
        using var verifiedCore = await Task.Run(CoreIntegrity.Acquire);
        var info = new ProcessStartInfo(Store.Bin) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Store.Root };
        info.ArgumentList.Add("--disable-color");
        foreach (var a in args)
            info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new Exception("Не удалось запустить sing-box.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
        }
        catch { process.Kill(); throw; }
        string result = (await stdout) + "\n" + (await stderr);
        if (process.ExitCode != 0)
            throw new Exception(result.Trim());
        return result.Trim();
    }
}
public static class RuleRepository
{
    public const string Repository = "https://github.com/runetfreedom/russia-blocked-geoip";
    const string Api = "https://api.github.com/repos/runetfreedom/russia-blocked-geoip/commits/release";
    public static string MetaPath => Path.Combine(Store.Rules, "metadata.json");
    public static JsonObject? Metadata()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(MetaPath))?.AsObject();
        }
        catch { return null; }
    }
    public static string Description()
    {
        return Describe(Metadata());
    }
    public static string Describe(JsonObject? metadata)
    {
        if (metadata == null)
            return "База ещё не загружена";
        string revision = metadata["revision"]?.ToString() ?? "—";
        string date = DateTimeOffset.TryParse(metadata["fetched"]?.ToString(), out var fetched)
            ? fetched.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "неизвестно";
        return $"ru-blocked.srs  •  {metadata["bytes"]} байт\r\nЗагружено: {date}\r\nРевизия: {revision[..Math.Min(12, revision.Length)]}";
    }
    public static bool Due()
    {
        var m = Metadata();
        return !File.Exists(Path.Combine(Store.Rules, "ru-blocked.srs")) || m == null || !DateTimeOffset.TryParse(m["fetched"]?.ToString(), out var date) || DateTimeOffset.UtcNow - date > TimeSpan.FromHours(6);
    }
    internal static async Task<bool> ValidCache(JsonObject metadata)
    {
        try
        {
            await using var file = new FileStream(Path.Combine(Store.Rules, "ru-blocked.srs"), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            return file.Length is > 0 and <= 16 * 1024 * 1024 &&
                Convert.ToHexString(await SHA256.HashDataAsync(file)).Equals(metadata["sha256"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }
    public static async Task<bool> Update(HttpClient client)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FlowVPN/1.4");
        var commit = JsonNode.Parse(await BoundedHttp.Download(client, Api, 1024 * 1024, token));
        string revision = commit?["sha"]?.ToString() ?? throw new Exception("GitHub не вернул ревизию списка.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(revision, @"\A[0-9a-f]{40}\z"))
            throw new Exception("Некорректная ревизия GitHub.");
        var metadata = Metadata();
        if (metadata?["revision"]?.ToString() == revision && await ValidCache(metadata))
        {
            metadata["fetched"] = DateTimeOffset.UtcNow.ToString("O");
            Store.WriteJson(MetaPath, metadata);
            return false;
        }
        string url = $"https://raw.githubusercontent.com/runetfreedom/russia-blocked-geoip/{revision}/srs/ru-blocked.srs";
        var data = await BoundedHttp.Download(client, url, 16 * 1024 * 1024, token);
        string candidate = Path.Combine(Store.Run, "geoip-" + Guid.NewGuid().ToString("N") + ".srs"), decoded = candidate + ".json";
        try
        {
            await File.WriteAllBytesAsync(candidate, data);
            await CoreCli.Run("rule-set", "decompile", candidate, "-o", decoded);
            var parsed = JsonNode.Parse(await File.ReadAllTextAsync(decoded));
            if (parsed?["rules"] is not JsonArray array || array.Count == 0)
                throw new Exception("Получен пустой список.");
            foreach (var rule in array)
            {
                if (rule is not JsonObject r || r.Count != 1 || r["ip_cidr"] is not JsonArray ips || ips.Count == 0)
                    throw new Exception("Репозиторий изменил формат: ожидаются только IP-подсети.");
            }
            Store.Atomic(Path.Combine(Store.Rules, "ru-blocked.srs"), data);
            Store.WriteJson(MetaPath, new JsonObject { ["repository"] = Repository, ["url"] = url, ["revision"] = revision, ["fetched"] = DateTimeOffset.UtcNow.ToString("O"), ["bytes"] = data.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(data)) });
            return true;
        }
        finally { File.Delete(candidate); File.Delete(decoded); }
    }
}
public sealed class VpnEngine
{
    public IWebProxy? Proxy
    {
        get; private set;
    }
    CoreControl? control;
    public CoreSnapshot? Snapshot
    {
        get; private set;
    }
    public string ControlError { get; private set; } = "";
    public VpnHealth Health { get; private set; } = VpnHealth.Stopped; public bool Healthy => Running && Health == VpnHealth.Online;
    Process? process; CoreJob? job; CancellationTokenSource? monitor; public bool Running => process != null && !process.HasExited; public bool Busy
    {
        get; private set;
    }
    public string Status { get; private set; } = "Отключено"; public string? ActiveProfile
    {
        get; private set;
    }
    public string? ActiveProfileId
    {
        get; private set;
    }
    public int ProxyPort
    {
        get; private set;
    }
    public event Action? Changed;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly SemaphoreSlim logGate = new(1, 1);
    void Set(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
    public HttpClient Client()
    {
        if (!Running)
            throw new Exception("Сначала подключите VPN, чтобы обновить список через него.");
        return BoundedHttp.Client(TimeSpan.FromSeconds(35), Proxy);
    }
    public async Task Connect(Settings settings, VpnProfile profile)
    {
        // A connection uses an immutable snapshot of the user's choices at invocation time.
        settings = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        profile = System.Text.Json.JsonSerializer.Deserialize<VpnProfile>(System.Text.Json.JsonSerializer.Serialize(profile))!;
        await gate.WaitAsync();
        try
        {
            if (Running)
                return;
            Busy = true;
            Health = VpnHealth.Checking;
            Set("Подготовка подключения…");
            if (!Native.IsAdmin)
                throw new Exception("Для TUN нужен запуск с правами администратора. Откройте FlowVPN из меню «Пуск».");
            var existingCore = Process.GetProcessesByName("sing-box");
            try
            {
                if (existingCore.Length > 0)
                    throw new Exception("Уже работает sing-box. Остановите другой туннель перед подключением.");
            }
            finally { foreach (var other in existingCore) other.Dispose(); }
            var activeVpn = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up && System.Text.RegularExpressions.Regex.IsMatch(n.Name + " " + n.Description, "TAP|DCO|OpenVPN|Outline|WireGuard", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.")));
            if (activeVpn)
                throw new Exception("Обнаружен другой активный VPN-адаптер. Отключите OpenVPN Connect / Outline перед запуском.");
            if (settings.BaseRulesEnabled && !File.Exists(Path.Combine(Store.Rules, "ru-blocked.srs")))
                throw new Exception("Нет локальной базовой таблицы. Загрузите её в настройках приложения → «База блокировок».");
            if (settings.BaseRulesEnabled && DomainRepository.Current(settings.BaseDomainCategory) == null)
                throw new InvalidOperationException("Загрузите выбранную доменную категорию в настройках общей базы.");
            var link = ConfigBuilder.Detect();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            ProxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var proxyAccess = new ProxyAccess();
            Proxy = proxyAccess.At(ProxyPort);
            var selected = settings.Sources.FirstOrDefault(s => s.Id == profile.SourceId) ?? throw new Exception("Выберите подключение.");
            if (selected.DynamicKey.Length > 0)
            {
                Set("Обновление динамического ключа Outline…");
                var updated = await Importers.DynamicOutline(selected.DynamicKey);
                selected.ConnectionJson = updated.ConnectionJson;
            }
            Directory.CreateDirectory(Store.ProfileLogs(profile.Id));
            string consolePath = Path.Combine(Store.ProfileLogs(profile.Id), "console.log");
            string config = Path.Combine(Store.Run, "runtime.json");
            control?.Dispose();
            control = new CoreControl(ProxyPort);
            Store.WriteJson(config, ConfigBuilder.Build(settings, profile, link, ProxyPort, control, proxyAccess));
            await CoreCli.Run("check", "-c", config);
            string log = Path.Combine(Store.ProfileLogs(profile.Id), "sing-box.log");
            if (File.Exists(log) && new FileInfo(log).Length > 5000000)
                File.Move(log, log + ".previous", true);
            var info = new ProcessStartInfo(Store.Bin) { UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Store.Root, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "--disable-color", "run", "-c", config })
                info.ArgumentList.Add(arg);
            if (process != null)
            {
                process.Dispose();
                process = null;
            }
            job?.Dispose();
            job = null;
            monitor?.Cancel();
            monitor?.Dispose();
            monitor = null;
            using (await Task.Run(CoreIntegrity.Acquire))
                process = Process.Start(info) ?? throw new Exception("sing-box не запустился.");
            job = new CoreJob(process);
            _ = Capture(process.StandardError, consolePath);
            _ = Capture(process.StandardOutput, consolePath);
            ActiveProfile = profile.Name;
            ActiveProfileId = profile.Id;
            monitor = new();
            var token = monitor.Token;
            Set("Проверка VPN-соединения…");
            using var client = Client();
            string? ip = null;
            for (int i = 0; i < 2 && Running; i++)
            {
                ip = await VpnConnectivity.Probe(client, token);
                if (ip != null)
                    break;
                await Task.Delay(1000, token);
            }
            if (ip == null)
                throw new Exception("VPN не прошёл проверку соединения. " + (Running ? "Сервер или интернет недоступен." : "sing-box завершился. Откройте журнал."));
            Health = VpnHealth.Online;
            Set($"Подключено  •  {ip}");
            _ = Monitor(process, token);
            _ = WatchControl(process, control, token);
            _ = WatchConnectivity(process, link.Name, token);
        }
        catch (OperationCanceledException) when (monitor?.IsCancellationRequested == true) { await StopInternal(); }
        catch { await StopInternal(); throw; }
        finally { Busy = false; Changed?.Invoke(); gate.Release(); }
    }
    async Task Capture(StreamReader reader, string logPath)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.Length > 2000)
                    line = line[..2000];
                await logGate.WaitAsync();
                try
                {
                    await File.AppendAllTextAsync(logPath, line + Environment.NewLine);
                }
                finally { logGate.Release(); }
            }
        }
        catch { }
    }
    async Task Monitor(Process p, CancellationToken token)
    {
        try
        {
            await p.WaitForExitAsync(token);
            if (!token.IsCancellationRequested && ReferenceEquals(process, p))
            {
                monitor?.Cancel();
                control?.Dispose();
                control = null;
                Snapshot = null;
                Proxy = null;
                Health = VpnHealth.Stopped;
                Set("Соединение завершилось. Проверьте журнал.");
                File.Delete(Path.Combine(Store.Run, "runtime.json"));
                ActiveProfile = null;
                ActiveProfileId = null;
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!token.IsCancellationRequested && ReferenceEquals(process, p)) { Health = VpnHealth.Stopped; Set("Ошибка наблюдения за sing-box: " + e.Message); } }
    }
    async Task WatchConnectivity(Process session, string originalInterface, CancellationToken token)
    {
        try
        {
            using var client = Client();
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), token);
                if (!ReferenceEquals(process, session) || session.HasExited)
                    return;
                bool physical = true, same = true;
                try
                {
                    same = ConfigBuilder.Detect().Name == originalInterface;
                }
                catch { physical = false; }
                string? ip = physical && same ? await VpnConnectivity.Probe(client, token) : null;
                if (token.IsCancellationRequested || !ReferenceEquals(process, session) || session.HasExited)
                    return;
                Health = VpnConnectivity.Assess(physical, same, ip != null);
                Set(Health switch
                {
                    VpnHealth.NoNetwork => "Нет физического подключения к сети",
                    VpnHealth.NetworkChanged => "Сеть изменилась. Переподключите VPN",
                    VpnHealth.Unreachable => "Туннель не отвечает. Проверка повторится",
                    _ => "Подключено  •  " + ip
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!token.IsCancellationRequested && ReferenceEquals(process, session)) { Health = VpnHealth.Unreachable; Set("Не удалось проверить туннель: " + e.Message); } }
    }
    async Task WatchControl(Process session, CoreControl api, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var snapshot = await api.Snapshot(token);
                if (token.IsCancellationRequested || !ReferenceEquals(process, session) || session.HasExited)
                    return;
                Snapshot = snapshot;
                ControlError = "";
                Changed?.Invoke();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException or ObjectDisposedException or InvalidOperationException or FormatException)
            {
                if (token.IsCancellationRequested || !ReferenceEquals(process, session))
                    return;
                ControlError = "API движка временно недоступен";
                Snapshot = null;
                Changed?.Invoke();
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), token);
            }
            catch (OperationCanceledException) { return; }
        }
    }
    public async Task CloseConnection(string id, CancellationToken token)
    {
        var api = control ?? throw new InvalidOperationException("Движок не запущен.");
        await api.CloseConnection(id, token);
        var snapshot = await api.Snapshot(token);
        if (ReferenceEquals(control, api))
        {
            Snapshot = snapshot;
            Changed?.Invoke();
        }
    }
    async Task StopInternal()
    {
        monitor?.Cancel();
        monitor?.Dispose();
        monitor = null;
        if (process != null)
        {
            try
            {
                await Native.Stop(process);
            }
            finally { process.Dispose(); process = null; job?.Dispose(); job = null; }
        }
        ActiveProfile = null;
        ActiveProfileId = null;
        Proxy = null;
        Health = VpnHealth.Stopped;
        control?.Dispose();
        control = null;
        Snapshot = null;
        ControlError = "";
        File.Delete(Path.Combine(Store.Run, "runtime.json"));
        Set("Отключено");
    }
    public async Task Disconnect()
    {
        monitor?.Cancel();
        await gate.WaitAsync();
        try
        {
            Busy = true;
            Set("Отключение…");
            await StopInternal();
        }
        finally { Busy = false; Changed?.Invoke(); gate.Release(); }
    }
}
