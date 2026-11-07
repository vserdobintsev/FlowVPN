using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlowVPN;

/// <summary>A verified, unelevated transport whose lifetime belongs to one VPN session.</summary>
internal sealed class ModuleProcess : IAsyncDisposable, IDisposable
{
    readonly Process process;
    readonly CoreJob job;
    readonly ModuleLease lease;
    readonly object sync = new();
    Task? stopping;
    int exitConfirmed;
    public BrokerLocalTransport Transport { get; }
    public bool HasExited => process.HasExited;
    public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);

    ModuleProcess(Process process, CoreJob job, ModuleLease lease, BrokerLocalTransport transport) =>
        (this.process, this.job, this.lease, Transport) = (process, job, lease, transport);

    public static async Task<ModuleProcess> StartAsync(VpnSource source, CancellationToken token)
    {
        var parameters = ModuleTransport.OutlineParameters(source);
        var uplink = ConfigBuilder.Detect();
        var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item => item.Name == uplink.Name)
            ?? throw new InvalidOperationException("Сетевое подключение изменилось. Попробуйте подключиться снова.");
        var addresses = await Dns.GetHostAddressesAsync(parameters.Server, token).ConfigureAwait(false);
        var address = ServerAddress(addresses);
        var properties = network.GetIPProperties();
        int index = address.AddressFamily == AddressFamily.InterNetwork
            ? properties.GetIPv4Properties().Index : properties.GetIPv6Properties().Index;
        var lease = await ModuleManager.Current.AcquireAsync(ModuleTransport.OutlineModuleId, token).ConfigureAwait(false);
        Process? process = null;
        CoreJob? job = null;
        try
        {
            var transport = new BrokerLocalTransport
            {
                ModuleId = ModuleTransport.OutlineModuleId, ProtocolVersion = 1,
                Username = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), InterfaceName = uplink.Name
            };
            string configuration = JsonSerializer.Serialize(new
            {
                protocolVersion = 1, server = address.ToString(), serverPort = parameters.ServerPort,
                method = parameters.Method, password = parameters.Password, prefixBase64 = parameters.PrefixBase64,
                interfaceIndex = index, socksUsername = transport.Username, socksPassword = transport.Password
            });
            if (Encoding.UTF8.GetByteCount(configuration) > 1024 * 1024 - 1)
                throw new InvalidDataException("Параметры подключения слишком велики для модуля Outline.");
            var start = new ProcessStartInfo(lease.ExecutablePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(lease.ExecutablePath)!
            };
            // Secrets travel only through an inherited anonymous pipe, never the command line or disk.
            process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить модуль Outline.");
            process.StandardInput.NewLine = "\n";
            job = new CoreJob(process);
            _ = DrainErrors(process.StandardError);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await process.StandardInput.WriteLineAsync(configuration.AsMemory(), deadline.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            transport.Port = await ReadReady(process.StandardOutput.BaseStream, deadline.Token).ConfigureAwait(false);
            if (process.HasExited) throw new InvalidOperationException("Модуль Outline завершился до подключения.");
            return new(process, job, lease, transport);
        }
        catch
        {
            try
            {
                job?.Dispose();
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                if (process == null) lease.Dispose();
                else ReleaseAfterExit(process, lease);
            }
            throw;
        }
    }

    internal static async Task<int> ReadReady(Stream input, CancellationToken token)
    {
        byte[] buffer = new byte[1024];
        int count = 0;
        while (count < buffer.Length)
        {
            int read = await input.ReadAsync(buffer.AsMemory(count, 1), token).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("Модуль Outline не сообщил о готовности.");
            if (buffer[count++] == '\n')
            {
                using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                    !root.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int protocol) || protocol != 1 ||
                    !root.TryGetProperty("port", out var port) || port.ValueKind != JsonValueKind.Number || !port.TryGetInt32(out int number) || number is < 1024 or > 65535)
                    throw new InvalidDataException("Модуль Outline сообщил недопустимые параметры запуска.");
                return number;
            }
        }
        throw new InvalidDataException("Ответ модуля Outline превышает допустимый размер.");
    }

    internal static IPAddress ServerAddress(IEnumerable<IPAddress> addresses)
    {
        IPAddress? ipv6 = null;
        foreach (var address in addresses)
        {
            if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
            if (address.AddressFamily == AddressFamily.InterNetwork) return address;
            if (address.AddressFamily == AddressFamily.InterNetworkV6) ipv6 ??= address;
        }
        return ipv6 ?? throw new InvalidOperationException("Не удалось определить адрес VPN-сервера.");
    }

    static async Task DrainErrors(StreamReader reader)
    {
        // Module output is intentionally not copied into logs: a dependency may include credentials.
        char[] buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync) return Volatile.Read(ref exitConfirmed) != 0 ? ValueTask.CompletedTask : new(stopping ??= Stop());
    }
    async Task Stop()
    {
        job.Dispose(); // KILL_ON_JOB_CLOSE also covers application termination.
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        finally { ReleaseAfterExit(process, lease, () => Volatile.Write(ref exitConfirmed, 1)); }
    }
    static void ReleaseAfterExit(Process process, ModuleLease lease, Action? confirmed = null)
    {
        // Even if asynchronous waiting failed, never unlock the module while its child is alive.
        int released = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            process.Dispose();
            lease.Dispose();
            confirmed?.Invoke();
        }
        if (process.HasExited) { Release(); return; }
        process.Exited += (_, _) => Release();
        process.EnableRaisingEvents = true; // Also raises for a process that exited before registration.
    }
    public void Dispose() => _ = DisposeQuietly();
    async Task DisposeQuietly()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
