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
    public static async Task<JsonObject> Build(Settings settings, VpnProfile profile, Uplink link, int proxyPort, CoreControl? control = null, ProxyAccess? proxyAccess = null)
    {
        var plan = await BrokerPlanBuilder.Create(settings, profile);
        using var ownedControl = control is null ? new CoreControl(proxyPort) : null;
        return BrokerConfig.Build(plan, link, proxyPort, control ?? ownedControl!, proxyAccess ?? new ProxyAccess());
    }
}
public static class CoreCli
{
    public static Task<string> Run(params string[] args) => Run(CancellationToken.None, args);

    public static Task<string> Run(CancellationToken token, params string[] args) => RunCore(token, null, args);

    public static Task<string> Check(JsonObject configuration, CancellationToken token = default) => RunCore(token, configuration, "check", "-c", "stdin");

    static async Task<string> RunCore(CancellationToken token, JsonObject? configuration, params string[] args)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = configuration != null,
            StandardInputEncoding = configuration == null ? null : new System.Text.UTF8Encoding(false),
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("--disable-color");
        foreach (var a in args)
            info.ArgumentList.Add(a);
        using var verifiedCore = await Task.Run(() => CoreIntegrity.Start(info), token).ConfigureAwait(false);
        var process = verifiedCore.Process;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            if (configuration != null)
            {
                // Serialize directly to the inherited pipe: connection secrets never need a temporary JSON file.
                await System.Text.Json.JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, configuration, Store.JsonOptions, deadline.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            // Cancellation owns the child until it exits. In particular, the caller may
            // delete decompile fixtures only after this task has completed.
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (IOException) { }
            if (error is OperationCanceledException && !token.IsCancellationRequested)
                throw new TimeoutException("sing-box не завершил операцию за 25 секунд.", error);
            throw;
        }
        string result = (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
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
    internal static async Task<bool> ValidCache(JsonObject metadata, CancellationToken token = default)
    {
        try
        {
            await using var file = new FileStream(Path.Combine(Store.Rules, "ru-blocked.srs"), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            return file.Length is > 0 and <= 16 * 1024 * 1024 &&
                Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(metadata["sha256"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }
    internal static string[] ParseIpRules(JsonArray rules, CancellationToken token = default)
    {
        const int maxTerms = 2_000_000;
        if (rules.Count is 0 or > maxTerms)
            throw new InvalidDataException("Пустая или слишком большая таблица IP-подсетей.");
        var cidrs = new List<string>();
        foreach (var rule in rules)
        {
            token.ThrowIfCancellationRequested();
            if (rule is not JsonObject fields || fields.Count != 1 || !fields.TryGetPropertyValue("ip_cidr", out var value))
                throw new InvalidDataException("Некорректная таблица IP-подсетей: разрешено только поле ip_cidr.");
            // sing-box decompiles a single CIDR as a string and multiple CIDRs as an array.
            if (value is JsonArray { Count: > 0 } values)
            {
                if ((long)cidrs.Count + values.Count > maxTerms)
                    throw new InvalidDataException("Таблица IP-подсетей превышает допустимое число правил.");
                foreach (var item in values)
                    Add(item);
            }
            else if (value is JsonValue)
                Add(value);
            else
                throw new InvalidDataException("Некорректное значение ip_cidr: ожидается строка или непустой массив строк.");
        }
        return cidrs.ToArray();

        void Add(JsonNode? value)
        {
            token.ThrowIfCancellationRequested();
            if (cidrs.Count >= maxTerms || value is not JsonValue item || !item.TryGetValue<string>(out var text) ||
                text.Length is 0 or > 64 || !IPNetwork.TryParse(text, out _))
                throw new InvalidDataException("Некорректная IP-подсеть или превышен допустимый размер таблицы.");
            cidrs.Add(text);
        }
    }
    public static async Task<bool> Update(HttpClient client, CancellationToken cancellationToken = default, IProgress<TransferProgress>? progress = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FlowVPN/1.4");
        var commit = JsonNode.Parse(await BoundedHttp.Download(client, Api, 1024 * 1024, token, progress, "Версия IP-базы"));
        string revision = commit?["sha"]?.ToString() ?? throw new Exception("GitHub не вернул ревизию списка.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(revision, @"\A[0-9a-f]{40}\z"))
            throw new Exception("Некорректная ревизия GitHub.");
        var metadata = Metadata();
        progress?.Report(new("IP-база", TransferStage.Verifying));
        if (metadata?["revision"]?.ToString() == revision && await ValidCache(metadata, token))
        {
            metadata["fetched"] = DateTimeOffset.UtcNow.ToString("O");
            Store.WriteJson(MetaPath, metadata);
            return false;
        }
        string url = $"https://raw.githubusercontent.com/runetfreedom/russia-blocked-geoip/{revision}/srs/ru-blocked.srs";
        var data = await BoundedHttp.Download(client, url, 16 * 1024 * 1024, token, progress, "IP-база");
        progress?.Report(new("IP-база", TransferStage.Verifying));
        string candidate = Path.Combine(Store.Run, "geoip-" + Guid.NewGuid().ToString("N") + ".srs"), decoded = candidate + ".json";
        try
        {
            await File.WriteAllBytesAsync(candidate, data, token);
            await CoreCli.Run(token, "rule-set", "decompile", candidate, "-o", decoded);
            var parsed = JsonNode.Parse(await File.ReadAllTextAsync(decoded, token));
            if (parsed is not JsonObject root || root.Any(p => p.Key is not ("version" or "rules")) || root["rules"] is not JsonArray array)
                throw new Exception("Получен пустой список.");
            _ = ParseIpRules(array, token);
            token.ThrowIfCancellationRequested();
            progress?.Report(new("IP-база", TransferStage.Applying));
            Store.Atomic(Path.Combine(Store.Rules, "ru-blocked.srs"), data);
            Store.WriteJson(MetaPath, new JsonObject { ["repository"] = Repository, ["url"] = url, ["revision"] = revision, ["fetched"] = DateTimeOffset.UtcNow.ToString("O"), ["bytes"] = data.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(data)) });
            return true;
        }
        finally { File.Delete(candidate); File.Delete(decoded); }
    }
}
public sealed class VpnEngine : IDisposable
{
    public IWebProxy? Proxy { get; private set; }
    public CoreSnapshot? Snapshot { get; private set; }
    public CoreTrafficSnapshot? Traffic { get; private set; }
    public string ControlError { get; private set; } = "";
    public VpnHealth Health { get; private set; } = VpnHealth.Stopped;
    public bool Healthy => Running && Health == VpnHealth.Online;
    public bool Running => running && broker is { IsClosed: false };
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "Отключено";
    public string? ActiveProfile { get; private set; }
    public string? ActiveProfileId { get; private set; }
    public int ProxyPort { get; private set; }
    public event Action? Changed;
    public event Action? TrafficChanged;
    BrokerClient? broker;
    ModuleProcess? transportModule;
    CancellationTokenSource? session;
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim gate = new(1, 1), logGate = new(1, 1);
    readonly EventHandler processExit;
    bool running, stopping;
    string? sessionFailure;
    int disposed;

    public VpnEngine()
    {
        processExit = (_, _) => Dispose();
        AppDomain.CurrentDomain.ProcessExit += processExit;
    }
    void Set(string status) { Status = status; Changed?.Invoke(); }
    public HttpClient Client()
    {
        if (!Running)
            throw new InvalidOperationException("Сначала подключите VPN, чтобы обновить список через него.");
        return BoundedHttp.Client(TimeSpan.FromSeconds(35), Proxy);
    }

    public async Task Connect(Settings settings, VpnProfile profile, Func<string, CancellationToken, Task<bool>>? prepareModule = null)
    {
        settings = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        profile = System.Text.Json.JsonSerializer.Deserialize<VpnProfile>(System.Text.Json.JsonSerializer.Serialize(profile))!;
        await gate.WaitAsync(lifetime.Token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Running) return;
            Busy = true;
            await StopInternal();
            Health = VpnHealth.Checking;
            session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            sessionFailure = null;
            var token = session.Token;
            Set("Подготовка подключения…");
            BrokerClient.EnsureSupported();
            var existingCore = Process.GetProcessesByName("sing-box");
            try
            {
                if (existingCore.Length > 0)
                    throw new InvalidOperationException("Уже работает sing-box. Остановите другой туннель перед подключением.");
            }
            finally { foreach (var other in existingCore) other.Dispose(); }
            bool activeVpn = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up &&
                System.Text.RegularExpressions.Regex.IsMatch(n.Name + " " + n.Description, "TAP|DCO|OpenVPN|Outline|WireGuard", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.")));
            if (activeVpn)
                throw new InvalidOperationException("Обнаружен другой активный VPN-адаптер. Отключите OpenVPN Connect / Outline перед запуском.");
            var selected = settings.Sources.FirstOrDefault(source => source.Id == profile.SourceId) ?? throw new InvalidOperationException("Выберите подключение.");
            if (selected.DynamicKey.Length > 0)
            {
                Set("Обновление динамического ключа Outline…");
                var updated = await Importers.DynamicOutline(selected.DynamicKey, token);
                selected.ConnectionJson = updated.ConnectionJson;
                selected.Transport = updated.Transport;
            }
            if (ModuleTransport.RequiredModule(selected) is not null)
            {
                Set("Подготовка модуля Outline…");
                if (prepareModule != null && !await prepareModule(ModuleTransport.OutlineCapability, token))
                {
                    await StopInternal();
                    return;
                }
                token.ThrowIfCancellationRequested();
                transportModule = await ModuleProcess.StartAsync(selected, token);
            }
            var plan = await BrokerPlanBuilder.Create(settings, profile, token, transportModule?.Transport);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Store.ProfileLogs(profile.Id));
            var current = new BrokerClient();
            broker = current;
            if (transportModule is { } module)
            {
                if (module.HasExited) throw new InvalidOperationException("Модуль Outline завершился до подключения.");
                _ = WatchModule(current, module, token);
            }
            if (Volatile.Read(ref disposed) != 0) { current.Dispose(); token.ThrowIfCancellationRequested(); }
            Set("Подтвердите разрешение Windows для VPN…");
            var state = await current.Start(plan, token);
            if (!state.Running || state.ProxyPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(state.ProxyUsername) ||
                state.ProxyUsername.Length > 128 || !BrokerWire.IsNonce(state.ProxyPassword) || string.IsNullOrWhiteSpace(state.InterfaceName))
                throw new InvalidDataException("Процесс VPN не сообщил действующее подключение.");
            ActiveProfile = profile.Name;
            ActiveProfileId = profile.Id;
            ProxyPort = state.ProxyPort;
            Proxy = new WebProxy($"socks5://127.0.0.1:{state.ProxyPort}")
            {
                Credentials = new NetworkCredential(state.ProxyUsername, state.ProxyPassword)
            };
            running = true;
            await ApplyState(state, profile.Id, token);
            // Poll while probing too: the broker must keep receiving liveness requests.
            _ = WatchState(current, profile.Id, token);
            _ = WatchTraffic(current, token);
            Set("Проверка VPN-соединения…");
            using var client = Client();
            string? ip = null;
            for (int attempt = 0; attempt < 2 && Running; attempt++)
            {
                ip = await VpnConnectivity.Probe(client, token);
                if (ip != null) break;
                await Task.Delay(1000, token);
            }
            token.ThrowIfCancellationRequested();
            if (ip == null || !Running)
                throw new InvalidOperationException("VPN не прошёл проверку соединения. Проверьте сервер, интернет и журнал профиля.");
            Health = VpnHealth.Online;
            Set("Подключено  •  " + ip);
            _ = WatchConnectivity(current, state.InterfaceName, token);
        }
        catch (OperationCanceledException) when (session?.IsCancellationRequested == true || lifetime.IsCancellationRequested)
        {
            string? failure = sessionFailure;
            await StopInternal();
            if (failure != null)
                throw new InvalidOperationException(failure);
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223) { await StopInternal(); }
        catch (BrokerRejectedException error)
        {
            if (error.State != null)
                await ApplyState(error.State, profile.Id, CancellationToken.None);
            await StopInternal();
            throw;
        }
        catch { await StopInternal(); throw; }
        finally { Busy = stopping; Changed?.Invoke(); gate.Release(); }
    }

    async Task ApplyState(BrokerState state, string profileId, CancellationToken token)
    {
        running = state.Running;
        Snapshot = state.Snapshot;
        ControlError = state.ControlError;
        if (state.Logs.Length > 0)
        {
            await logGate.WaitAsync(token);
            try
            {
                string path = Path.Combine(Store.ProfileLogs(profileId), "sing-box.log");
                if (File.Exists(path) && new FileInfo(path).Length > 5_000_000)
                    File.Move(path, path + ".previous", true);
                string text = string.Join(Environment.NewLine, state.Logs.Select(line => line.Length > 2000 ? line[..2000] : line)) + Environment.NewLine;
                await File.AppendAllTextAsync(path, text, token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ControlError = "Не удалось записать журнал профиля";
            }
            finally { logGate.Release(); }
        }
        Changed?.Invoke();
    }

    async Task WatchState(BrokerClient current, string profileId, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && ReferenceEquals(broker, current))
            {
                await Task.Delay(TimeSpan.FromSeconds(4), token);
                var state = await current.State(token);
                if (token.IsCancellationRequested || !ReferenceEquals(broker, current)) return;
                await ApplyState(state, profileId, token);
                if (!state.Running)
                {
                    await EndSession(current, "Соединение завершилось. Проверьте журнал.");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(broker, current))
            {
                if (error is BrokerRejectedException { State: { } state })
                    await ApplyState(state, profileId, CancellationToken.None);
                await EndSession(current, "Связь с процессом VPN потеряна: " + error.Message);
            }
        }
    }

    async Task WatchTraffic(BrokerClient current, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && ReferenceEquals(broker, current))
            {
                try
                {
                    var sample = await current.Traffic(token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || !ReferenceEquals(broker, current)) return;
                    Traffic = sample;
                    TrafficChanged?.Invoke();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(broker, current)) return;
                    // Telemetry is optional. Health/lifecycle monitoring owns connection failures.
                    if (Traffic is { } old)
                    {
                        Traffic = old with { HasSample = false, SampleAgeMilliseconds = long.MaxValue };
                        TrafficChanged?.Invoke();
                    }
                    if (current.IsClosed) return;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    async Task EndSession(BrokerClient current, string status)
    {
        current.Dispose();
        sessionFailure = status;
        CancelSession();
        await gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(broker, current)) return;
            Busy = true;
            await StopInternal();
            Set(status);
        }
        finally { Busy = stopping; Changed?.Invoke(); gate.Release(); }
    }

    async Task WatchConnectivity(BrokerClient current, string originalInterface, CancellationToken token)
    {
        try
        {
            using var client = Client();
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), token);
                if (!ReferenceEquals(broker, current) || !Running) return;
                bool physical = true, same = true;
                try { same = ConfigBuilder.Detect().Name == originalInterface; }
                catch { physical = false; }
                string? ip = physical && same ? await VpnConnectivity.Probe(client, token) : null;
                if (token.IsCancellationRequested || !ReferenceEquals(broker, current) || !Running) return;
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
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(broker, current))
            {
                Health = VpnHealth.Unreachable;
                Set("Не удалось проверить туннель: " + error.Message);
            }
        }
    }

    public async Task CloseConnection(string id, CancellationToken token)
    {
        var current = broker ?? throw new InvalidOperationException("Движок не запущен.");
        try
        {
            var state = await current.CloseConnection(id, token);
            if (ReferenceEquals(broker, current) && ActiveProfileId is { } profile)
                await ApplyState(state, profile, token);
        }
        catch (BrokerRejectedException error)
        {
            if (error.State != null && ActiveProfileId is { } profile)
                await ApplyState(error.State, profile, CancellationToken.None);
            throw;
        }
    }

    void CancelSession()
    {
        try { session?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    async Task StopInternal()
    {
        CancelSession();
        var old = broker;
        if (old != null)
        {
            stopping = true;
            Busy = true;
            Set("Отключение…");
            // An in-flight broker command can still be unwinding after IPC closes.
            // A timeout is not proof that its elevated child has actually stopped.
            try { await old.Stop(CancellationToken.None); }
            catch (Exception error) when (old.ExitConfirmed && error is (IOException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)) { }
            Interlocked.CompareExchange(ref broker, null, old);
        }
        var module = transportModule;
        if (module != null)
        {
            stopping = true;
            Busy = true;
            await module.DisposeAsync();
            Interlocked.CompareExchange(ref transportModule, null, module);
        }
        stopping = false;
        session?.Dispose();
        session = null;
        running = false;
        ActiveProfile = null;
        ActiveProfileId = null;
        Proxy = null;
        ProxyPort = 0;
        Snapshot = null;
        Traffic = null;
        TrafficChanged?.Invoke();
        Health = VpnHealth.Stopped;
        ControlError = "";
        Set("Отключено");
    }
    public async Task Disconnect()
    {
        CancelSession();
        await gate.WaitAsync();
        try
        {
            Busy = true;
            Set("Отключение…");
            await StopInternal();
        }
        finally { Busy = stopping; Changed?.Invoke(); gate.Release(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit -= processExit;
        lifetime.Cancel();
        CancelSession();
        Interlocked.Exchange(ref broker, null)?.Dispose();
        Interlocked.Exchange(ref transportModule, null)?.Dispose();
        running = false;
        Proxy = null;
        Snapshot = null;
        Traffic = null;
    }

    async Task WatchModule(BrokerClient current, ModuleProcess module, CancellationToken token)
    {
        try
        {
            await module.WaitForExitAsync(token);
            if (!token.IsCancellationRequested && ReferenceEquals(transportModule, module))
                await EndSession(current, "Модуль Outline завершился. Подключение остановлено.");
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }
}
