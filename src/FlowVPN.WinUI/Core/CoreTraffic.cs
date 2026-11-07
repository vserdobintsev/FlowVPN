using System.Text.Json;

namespace FlowVPN;

// These are routed payload bytes counted by sing-box, including direct outbounds, not NIC wire bytes.
public sealed record CoreTrafficSnapshot(string SessionId, long Sequence, DateTimeOffset StartedAt, DateTimeOffset SampledAt,
    double UploadedPerSecond, double DownloadedPerSecond, long SampleAgeMilliseconds, long UploadedTotal, long DownloadedTotal,
    long TotalsAgeMilliseconds, bool HasSample, bool HasTotals);

internal readonly record struct CoreTrafficDelta(long Uploaded, long Downloaded);

// One stream and one last-value snapshot per core; histories belong to the unelevated UI.
internal sealed class CoreTrafficMonitor : IAsyncDisposable
{
    readonly CancellationTokenSource lifetime = new();
    readonly Task worker;
    internal CoreTrafficState State { get; } = new();

    public CoreTrafficMonitor(CoreControl control) => worker = Observe(control);

    async Task Observe(CoreControl control)
    {
        int retrySeconds = 1;
        while (!lifetime.IsCancellationRequested)
        {
            State.BeginStream();
            try
            {
                await control.ReadTraffic(delta =>
                {
                    State.Record(delta);
                    retrySeconds = 1;
                }, lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or JsonException or ObjectDisposedException) { }
            finally { State.Disconnected(); }
            try { await Task.Delay(TimeSpan.FromSeconds(retrySeconds), lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            retrySeconds = Math.Min(retrySeconds * 2, 4);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { await worker.ConfigureAwait(false); }
        finally { lifetime.Dispose(); }
    }

    // A fixed-size frame buffer also bounds responses that never send a newline.
    internal static async Task ReadFrames(Stream stream, Action<CoreTrafficDelta> receive, CancellationToken token)
    {
        byte[] input = new byte[512], frame = new byte[256];
        int used = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        while (true)
        {
            int count = await stream.ReadAsync(input, deadline.Token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Поток статистики завершён.");
            for (int i = 0; i < count; i++)
            {
                if (input[i] == (byte)'\n')
                {
                    receive(ParseFrame(frame.AsMemory(0, used)));
                    used = 0;
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                }
                else
                {
                    if (used == frame.Length) throw new InvalidDataException("Слишком большой кадр статистики.");
                    frame[used++] = input[i];
                }
            }
        }
    }

    internal static CoreTrafficDelta ParseFrame(ReadOnlyMemory<byte> frame)
    {
        using var document = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 2 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Некорректный кадр статистики.");
        long? up = null, down = null;
        foreach (var field in document.RootElement.EnumerateObject())
        {
            if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt64(out long value) || value < 0)
                throw new InvalidDataException("Некорректный счётчик статистики.");
            switch (field.Name)
            {
                case "up" when up is null: up = value; break;
                case "down" when down is null: down = value; break;
                default: throw new InvalidDataException("Неизвестное или повторное поле статистики.");
            }
        }
        return new(up ?? throw new InvalidDataException("Отсутствует счётчик отправки."),
            down ?? throw new InvalidDataException("Отсутствует счётчик получения."));
    }
}

internal sealed class CoreTrafficState(TimeProvider? timeProvider = null)
{
    readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    readonly object gate = new();
    readonly string sessionId = Guid.NewGuid().ToString("N");
    readonly DateTimeOffset startedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    readonly long startedStamp = (timeProvider ?? TimeProvider.System).GetTimestamp();
    long sequence, previousFrame, sampleTick, totalsTick, uploadedTotal, downloadedTotal;
    double uploadRate, downloadRate;
    DateTimeOffset sampledAt;
    bool hasSample, hasTotals;

    public void BeginStream()
    {
        lock (gate) { previousFrame = clock.GetTimestamp(); hasSample = false; }
    }
    public void Disconnected() { lock (gate) hasSample = false; }

    public void Record(CoreTrafficDelta delta)
    {
        lock (gate)
        {
            long now = clock.GetTimestamp();
            double interval = clock.GetElapsedTime(previousFrame, now).TotalSeconds;
            previousFrame = now;
            // The API emits one frame per second. Delayed/batched frames are gaps;
            // dividing a backlog frame by a near-zero arrival interval would invent a spike.
            if (interval is < 0.5 or > 3 || delta.Uploaded < 0 || delta.Downloaded < 0)
            {
                hasSample = false;
                return;
            }
            sequence++;
            sampleTick = now;
            sampledAt = startedAt + clock.GetElapsedTime(startedStamp, now);
            uploadRate = delta.Uploaded / interval;
            downloadRate = delta.Downloaded / interval;
            hasSample = true;
        }
    }

    public void RecordTotals(CoreSnapshot snapshot)
    {
        lock (gate)
        {
            if (snapshot.Uploaded < uploadedTotal || snapshot.Downloaded < downloadedTotal) return;
            uploadedTotal = snapshot.Uploaded;
            downloadedTotal = snapshot.Downloaded;
            totalsTick = clock.GetTimestamp();
            hasTotals = true;
        }
    }

    public CoreTrafficSnapshot Snapshot()
    {
        lock (gate)
        {
            long now = clock.GetTimestamp();
            long Age(long stamp) => Math.Max(0, (long)clock.GetElapsedTime(stamp, now).TotalMilliseconds);
            return new(sessionId, sequence, startedAt, sampledAt, uploadRate, downloadRate,
                hasSample ? Age(sampleTick) : long.MaxValue, uploadedTotal, downloadedTotal,
                hasTotals ? Age(totalsTick) : long.MaxValue, hasSample, hasTotals);
        }
    }
}
