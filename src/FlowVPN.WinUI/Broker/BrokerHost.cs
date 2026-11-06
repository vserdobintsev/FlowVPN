using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;

namespace FlowVPN;

// The elevated entry point deliberately never initializes Store, WinUI, importers or updater code.
internal static class BrokerHost
{
    public static int Run(string[] args)
    {
        try
        {
#if FLOWVPN_PACKAGE && !FLOWVPN_PACKAGE_PREVIEW
            if (!BrokerPeer.HasPackageIdentity() || !Native.IsAdmin)
                return 1;
            var launch = ParseArguments(args);
            return RunAsync(launch.Name, launch.Nonce, launch.ParentId).GetAwaiter().GetResult();
#else
            return 1;
#endif
        }
        catch { return 1; }
    }

    internal static (string Name, string Nonce, int ParentId) ParseArguments(string[] args)
    {
        if (args.Length != 4 || args[0] != "--broker" || !BrokerWire.IsNonce(args[2]) ||
            !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out int parent) || parent <= 0 || parent == Environment.ProcessId)
            throw new ArgumentException("Некорректные параметры служебного процесса VPN.");
        BrokerPeer.ValidateName(args[1]);
        return (args[1], args[2], parent);
    }

    static async Task<int> RunAsync(string name, string nonce, int parentId)
    {
        using var parent = Process.GetProcessById(parentId);
        _ = parent.SafeHandle;
        using var lifetime = new CancellationTokenSource();
        Task parentWatch = WatchParent(parent, lifetime);
        // Identification prevents the unelevated pipe server from impersonating this elevated token.
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(20));
                await pipe.ConnectAsync(handshake.Token);
                BrokerPeer.ValidateServer(pipe, parent);
                await BrokerWire.WriteAsync(pipe, new BrokerHandshake { Nonce = nonce }, handshake.Token, BrokerWire.MaxHandshakeBytes);
                BrokerWire.ValidateHandshake(await BrokerWire.ReadAsync<BrokerHandshake>(pipe, handshake.Token, BrokerWire.MaxHandshakeBytes), nonce);
            }
            await RunSessionAsync(pipe, lifetime.Token);
            return 0;
        }
        finally
        {
            lifetime.Cancel();
            await parentWatch;
        }
    }

    static async Task WatchParent(Process parent, CancellationTokenSource lifetime)
    {
        try { await parent.WaitForExitAsync(lifetime.Token); lifetime.Cancel(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (InvalidOperationException) { lifetime.Cancel(); }
    }

    internal static void ValidateRequest(BrokerRequest request, bool alreadyStarted)
    {
        bool valid = request.Command switch
        {
            "start" => !alreadyStarted && request.Plan != null && request.ConnectionId == null,
            "status" or "stop" => request.Plan == null && request.ConnectionId == null,
            "close" => alreadyStarted && request.Plan == null && Guid.TryParse(request.ConnectionId, out _),
            _ => false
        };
        if (!valid)
            throw new InvalidDataException("Недопустимая команда управления VPN.");
    }

    // Tests can exercise framing/lifecycle here without enabling the production elevated CLI.
    internal static async Task RunSessionAsync(Stream pipe, CancellationToken token)
    {
        await using var session = new Session();
        bool started = false;
        while (!token.IsCancellationRequested)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            operation.CancelAfter(TimeSpan.FromSeconds(30));
            var request = await BrokerWire.ReadAsync<BrokerRequest>(pipe, operation.Token,
                started ? BrokerWire.MaxControlBytes : BrokerWire.MaxFrameBytes);
            ValidateRequest(request, started);
            BrokerReply reply;
            bool end = request.Command == "stop";
            try
            {
                switch (request.Command)
                {
                    case "start":
                        started = true;
                        await session.Start(request.Plan!, operation.Token);
                        break;
                    case "close":
                        await session.CloseConnection(request.ConnectionId!, operation.Token);
                        break;
                    case "stop":
                        await session.Stop();
                        break;
                }
                reply = new() { Success = true, State = await session.State(operation.Token, request.Command == "start") };
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                end |= request.Command == "start";
                reply = new()
                {
                    Success = false,
                    Error = request.Command == "start" ? "Не удалось запустить VPN. Проверьте параметры подключения и журнал." : "Не удалось выполнить команду VPN.",
                    State = session.LocalState()
                };
            }
            await BrokerWire.WriteAsync(pipe, reply, operation.Token, BrokerWire.MaxReplyBytes);
            if (end)
                return;
        }
    }

    static string Short(string value, int length) => value.Length <= length ? value : value[..length];

    sealed class Session : IAsyncDisposable
    {
        readonly LogBuffer logs = new();
        CoreIntegrity? core;
        CoreJob? job;
        CoreControl? control;
        ProxyAccess? proxy;
        Task[] readers = [];
        int proxyPort;
        string interfaceName = "";
        bool Running => core != null && !core.Process.HasExited;

        public async Task Start(BrokerPlan plan, CancellationToken token)
        {
            var existing = Process.GetProcessesByName("sing-box");
            try
            {
                if (existing.Length != 0)
                    throw new InvalidOperationException("Уже работает sing-box. Остановите другой туннель перед подключением.");
            }
            finally { foreach (var process in existing) process.Dispose(); }
            bool activeVpn = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up &&
                System.Text.RegularExpressions.Regex.IsMatch(n.Name + " " + n.Description, "TAP|DCO|OpenVPN|Outline|WireGuard", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)));
            if (activeVpn)
                throw new InvalidOperationException("Обнаружен другой активный VPN-адаптер. Отключите его перед подключением.");
            var link = ConfigBuilder.Detect();
            interfaceName = link.Name;
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            proxy = new ProxyAccess();
            control = new CoreControl(proxyPort);
            string configuration = BrokerConfig.Build(plan, link, proxyPort, control, proxy).ToJsonString();
            if (Encoding.UTF8.GetByteCount(configuration) > BrokerWire.MaxFrameBytes)
                throw new InvalidDataException("Конфигурация VPN превышает допустимый размер.");

            using (var checkedCore = CoreIntegrity.Start(StartInfo("check", hiddenConsole: false)))
            using (var checkedJob = new CoreJob(checkedCore.Process))
            {
                Task output = logs.Capture(checkedCore.Process.StandardOutput), error = logs.Capture(checkedCore.Process.StandardError);
                try
                {
                    await WriteConfiguration(checkedCore.Process, configuration, token);
                    await checkedCore.Process.WaitForExitAsync(token);
                    await Task.WhenAll(output, error).WaitAsync(token);
                    if (checkedCore.Process.ExitCode != 0)
                        throw new InvalidDataException("Движок отклонил конфигурацию VPN. Подробности находятся в журнале.");
                }
                finally
                {
                    if (!checkedCore.Process.HasExited)
                        checkedCore.Process.Kill();
                }
            }
            core = CoreIntegrity.Start(StartInfo("run", hiddenConsole: true));
            job = new CoreJob(core.Process);
            readers = [logs.Capture(core.Process.StandardOutput), logs.Capture(core.Process.StandardError)];
            await WriteConfiguration(core.Process, configuration, token);
        }

        static ProcessStartInfo StartInfo(string command, bool hiddenConsole)
        {
            var start = new ProcessStartInfo
            {
                UseShellExecute = false, CreateNoWindow = !hiddenConsole, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            foreach (string arg in new[] { "--disable-color", command, "-c", "stdin" })
                start.ArgumentList.Add(arg);
            return start;
        }

        static async Task WriteConfiguration(Process process, string configuration, CancellationToken token)
        {
            try
            {
                await process.StandardInput.WriteAsync(configuration.AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
            }
            finally { process.StandardInput.Close(); }
        }

        public BrokerState LocalState(bool includeCredentials = false) => new()
        {
            Running = Running,
            ProcessId = core?.Process.Id ?? 0,
            Logs = logs.Drain(),
            ProxyPort = proxyPort,
            ProxyUsername = includeCredentials ? proxy?.Username ?? "" : "",
            ProxyPassword = includeCredentials ? proxy?.Password ?? "" : "",
            InterfaceName = Short(interfaceName, 256)
        };

        public async Task<BrokerState> State(CancellationToken token, bool includeCredentials = false)
        {
            var state = LocalState(includeCredentials);
            if (state.Running && control != null)
            {
                try
                {
                    var snapshot = await control.Snapshot(token);
                    // Bound reply size independently of the engine's HTTP response limit.
                    state.Snapshot = snapshot with { Version = Short(snapshot.Version, 64), Connections = snapshot.Connections.Take(256).Select(c => c with
                    {
                        Id = Guid.Parse(c.Id).ToString("D"), Host = Short(c.Host, 253), ProcessPath = Short(c.ProcessPath, 512), Route = Short(c.Route, 256)
                    }).ToArray() };
                    if (snapshot.Connections.Count > 256)
                        state.ControlError = "Показаны первые 256 соединений.";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException or FormatException)
                {
                    state.ControlError = "API движка временно недоступен.";
                }
            }
            return state;
        }

        public Task CloseConnection(string id, CancellationToken token) =>
            (control ?? throw new InvalidOperationException("Движок не запущен.")).CloseConnection(id, token);

        public async Task Stop()
        {
            try
            {
                if (core != null && !core.Process.HasExited)
                {
                    Task stopping = Native.Stop(core.Process);
                    try { await stopping.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (TimeoutException)
                    {
                        // Job disposal below is the bounded fallback; observe a late failure after its process handle closes.
                        _ = stopping.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
            }
            finally
            {
                job?.Dispose(); job = null;
                core?.Dispose(); core = null;
                control?.Dispose(); control = null;
                proxy = null; proxyPort = 0;
                if (readers.Length != 0)
                    await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(3));
                readers = [];
            }
        }

        public async ValueTask DisposeAsync() => await Stop();
    }

    // Both streams share a bounded ring; an unterminated line cannot grow without limit.
    internal sealed class LogBuffer
    {
        const int MaxLines = 128, MaxLineCharacters = 2000;
        readonly Queue<string> lines = new();
        readonly object gate = new();
        int dropped;
        public async Task Capture(StreamReader reader)
        {
            char[] buffer = new char[1024];
            var line = new StringBuilder(MaxLineCharacters);
            bool truncated = false;
            try
            {
                int length;
                while ((length = await reader.ReadAsync(buffer)) != 0)
                    for (int i = 0; i < length; i++)
                    {
                        char character = buffer[i];
                        if (character == '\n')
                        {
                            Add(line.ToString() + (truncated ? "…" : ""));
                            line.Clear(); truncated = false;
                        }
                        else if (character != '\r')
                        {
                            if (line.Length < MaxLineCharacters) line.Append(character);
                            else truncated = true;
                        }
                    }
                if (line.Length != 0)
                    Add(line.ToString() + (truncated ? "…" : ""));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
        void Add(string value)
        {
            lock (gate)
            {
                if (lines.Count == MaxLines) { lines.Dequeue(); dropped++; }
                lines.Enqueue(value);
            }
        }
        public string[] Drain()
        {
            lock (gate)
            {
                string[] result = dropped == 0 ? lines.ToArray() : ["Пропущено старых строк журнала: " + dropped, .. lines];
                lines.Clear(); dropped = 0;
                return result;
            }
        }
    }
}
