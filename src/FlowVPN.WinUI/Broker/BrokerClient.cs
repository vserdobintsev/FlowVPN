using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlowVPN;

internal sealed class BrokerRejectedException(string message, BrokerState? state) : InvalidOperationException(message)
{
    public BrokerState? State { get; } = state;
}

// The unelevated UI owns this pipe. Closing it is the broker's fail-closed stop signal.
internal sealed class BrokerClient : IDisposable
{
    readonly string name = BrokerPeer.CreateName();
    readonly string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    readonly NamedPipeServerStream pipe;
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim requests = new(1, 1);
    readonly object processLock = new();
    Process? brokerProcess;
    Task brokerExit = Task.CompletedTask;
    int disposed, started;
    public bool IsClosed => Volatile.Read(ref disposed) != 0;
    public bool ExitConfirmed { get { lock (processLock) return brokerExit.IsCompletedSuccessfully; } }

    public BrokerClient()
    {
        EnsureSupported();
        pipe = BrokerPeer.CreateServer(name);
    }

    internal static void EnsureSupported()
    {
#if FLOWVPN_PACKAGE && !FLOWVPN_PACKAGE_PREVIEW
        if (BrokerPeer.HasPackageIdentity() && !Native.IsAdmin && !Entry.Preview && !Entry.VisualTest && !Entry.ReleaseUiTest)
            return;
#endif
        throw new InvalidOperationException("Подключение доступно в установленной рабочей версии FlowVPN. Откройте её без прав администратора: Windows запросит разрешение только для VPN.");
    }

    internal static ProcessStartInfo CreateStartInfo(string pipeName, string sessionNonce, int parentId)
    {
        BrokerPeer.ValidateName(pipeName);
        if (!BrokerWire.IsNonce(sessionNonce) || parentId <= 0)
            throw new ArgumentException("Некорректные параметры сеанса VPN.");
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "FlowVPN.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (string value in new[] { "--broker", pipeName, sessionNonce, parentId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(value);
        return start;
    }

    public async Task<BrokerState> Start(BrokerPlan plan, CancellationToken token)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("Сеанс VPN уже был запущен.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            // ShellExecute can wait for UAC. Cancellation closes the pipe immediately;
            // a late approval cannot start a tunnel without the authenticated UI.
            var launch = Task.Run(() => Process.Start(CreateStartInfo(name, nonce, Environment.ProcessId)));
            Process? child;
            try { child = await launch.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch
            {
                _ = launch.ContinueWith(completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion) completed.Result?.Dispose();
                    else _ = completed.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            if (child == null)
                throw new InvalidOperationException("Windows не запустила процесс VPN.");
            lock (processLock)
            {
                if (IsClosed) { child.Dispose(); throw new ObjectDisposedException(nameof(BrokerClient)); }
                brokerProcess = child;
                brokerExit = ObserveExit(child);
            }
            await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            BrokerPeer.ValidateClient(pipe, child);
            var hello = await BrokerWire.ReadAsync<BrokerHandshake>(pipe, deadline.Token, BrokerWire.MaxHandshakeBytes).ConfigureAwait(false);
            BrokerWire.ValidateHandshake(hello, nonce);
            await BrokerWire.WriteAsync(pipe, hello, deadline.Token, BrokerWire.MaxHandshakeBytes).ConfigureAwait(false);
            return await Exchange(new() { Command = "start", Plan = plan }, deadline.Token).ConfigureAwait(false);
        }
        catch { Dispose(); throw; }
    }

    public Task<BrokerState> State(CancellationToken token) => Exchange(new() { Command = "status" }, token);
    public Task<BrokerState> CloseConnection(string id, CancellationToken token)
    {
        if (!Guid.TryParseExact(id, "D", out _))
            throw new ArgumentException("Некорректный идентификатор соединения.");
        return Exchange(new() { Command = "close", ConnectionId = id }, token);
    }
    public async Task Stop(CancellationToken token)
    {
        try
        {
            if (!IsClosed)
                await Exchange(new() { Command = "stop" }, token).ConfigureAwait(false);
        }
        finally
        {
            Dispose();
            Task exit;
            lock (processLock)
            {
                // A failed wait is not an exit. Keep the original process handle and
                // allow an explicit later stop to retry observing that same process.
                if (brokerExit.IsFaulted && brokerProcess != null)
                {
                    _ = brokerExit.Exception;
                    brokerExit = ObserveExit(brokerProcess);
                }
                exit = brokerExit;
            }
            await exit.WaitAsync(token).ConfigureAwait(false);
        }
    }

    async Task ObserveExit(Process child)
    {
        await child.WaitForExitAsync().ConfigureAwait(false);
        lock (processLock)
        {
            child.Dispose();
            if (ReferenceEquals(brokerProcess, child)) brokerProcess = null;
        }
    }

    async Task<BrokerState> Exchange(BrokerRequest request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await requests.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            await BrokerWire.WriteAsync(pipe, request, deadline.Token,
                request.Command == "start" ? BrokerWire.MaxFrameBytes : BrokerWire.MaxControlBytes).ConfigureAwait(false);
            var reply = await BrokerWire.ReadAsync<BrokerReply>(pipe, deadline.Token, BrokerWire.MaxReplyBytes).ConfigureAwait(false);
            if (!reply.Success)
                throw new BrokerRejectedException(reply.Error ?? "Процесс VPN отклонил запрос.", reply.State);
            return reply.State ?? throw new InvalidDataException("Процесс VPN не сообщил состояние.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or JsonException)
        {
            // A partial frame must never be reused by the next request.
            Dispose();
            throw;
        }
        finally { requests.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { lifetime.Cancel(); }
        finally { pipe.Dispose(); }
        // ObserveExit retains the original process handle until the broker has stopped.
        // Pending asynchronous operations still own the CTS/semaphore registrations.
        // Their managed containers are collected with this session after completion.
    }
}
