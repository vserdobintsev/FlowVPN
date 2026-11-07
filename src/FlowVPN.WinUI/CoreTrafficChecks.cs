using System.Text;
using System.Text.Json;

namespace FlowVPN;

internal static class CoreTrafficChecks
{
    public static async Task Run()
    {
        var passed = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed.Add(label);
        }
        static CoreTrafficDelta Parse(string json) => CoreTrafficMonitor.ParseFrame(Encoding.UTF8.GetBytes(json));
        Check(Parse("{\"up\":123,\"down\":456}") == new CoreTrafficDelta(123, 456), "Traffic frame preserves byte counters");
        Check(Parse("{\"down\":0,\"up\":9223372036854775807}").Uploaded == long.MaxValue, "Traffic parser accepts the full nonnegative Int64 range without summation overflow");
        foreach (string invalid in new[]
        {
            "{}", "[]", "{\"up\":1}", "{\"up\":-1,\"down\":0}", "{\"up\":1.5,\"down\":0}",
            "{\"up\":9223372036854775808,\"down\":0}", "{\"up\":1,\"up\":2,\"down\":0}",
            "{\"up\":1,\"down\":0,\"extra\":3}", "{\"up\":\"1\",\"down\":0}", "{\"up\":null,\"down\":0}",
            "{\"up\":1,\"down\":0} trailing"
        })
        {
            bool rejected = false;
            try { _ = Parse(invalid); }
            catch (Exception error) when (error is InvalidDataException or JsonException) { rejected = true; }
            Check(rejected, "Traffic parser rejects malformed or ambiguous frame " + passed.Count);
        }

        var frames = new List<CoreTrafficDelta>();
        using (var fragmented = new FragmentedStream(Encoding.UTF8.GetBytes("{\"up\":1,\"down\":2}\n{\"up\":3,\"down\":4}\n")))
        {
            bool ended = false;
            try { await CoreTrafficMonitor.ReadFrames(fragmented, frames.Add, CancellationToken.None); }
            catch (EndOfStreamException) { ended = true; }
            Check(ended && frames.SequenceEqual(new[] { new CoreTrafficDelta(1, 2), new CoreTrafficDelta(3, 4) }), "Fragmented NDJSON frames parse independently and EOF is a stream gap");
        }
        using (var oversized = new MemoryStream(new byte[257]))
        {
            bool rejected = false;
            try { await CoreTrafficMonitor.ReadFrames(oversized, _ => { }, CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "An unterminated traffic frame is bounded to 256 bytes");
        }
        using (var blocked = new WaitingStream())
        using (var cancellation = new CancellationTokenSource())
        {
            Task reading = CoreTrafficMonitor.ReadFrames(blocked, _ => { }, cancellation.Token);
            cancellation.Cancel();
            bool cancelled = false;
            try { await reading; }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Traffic read cancellation owns and completes its pending read");
        }

        var clock = new ManualClock();
        var state = new CoreTrafficState(clock);
        string session = state.Snapshot().SessionId;
        Check(!state.Snapshot().HasSample && !state.Snapshot().HasTotals, "A fresh core session has no fabricated zero sample or total");
        state.BeginStream();
        clock.Advance(TimeSpan.FromSeconds(1));
        state.Record(new(1000, 2000));
        var sample = state.Snapshot();
        Check(sample.HasSample && sample.Sequence == 1 && sample.UploadedPerSecond == 1000 && sample.DownloadedPerSecond == 2000 && sample.SampleAgeMilliseconds == 0, "A one-second frame records rates and a unique sequence");
        state.RecordTotals(new("test", 15_000, 20_000, [], clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(4));
        sample = state.Snapshot();
        Check(sample.SampleAgeMilliseconds == 4000 && sample.TotalsAgeMilliseconds == 4000 && sample.UploadedTotal == 15_000, "Sample and authoritative totals age independently using a monotonic clock");
        state.Record(new(10_000, 20_000));
        Check(!state.Snapshot().HasSample && state.Snapshot().Sequence == 1, "A delayed frame becomes a gap rather than an invented rate spike");
        state.Record(new(10_000, 20_000));
        Check(!state.Snapshot().HasSample, "A batched frame with near-zero arrival interval cannot create a rate spike");
        state.Disconnected();
        state.BeginStream();
        clock.Advance(TimeSpan.FromSeconds(1));
        state.Record(new(500, 700));
        sample = state.Snapshot();
        Check(sample.SessionId == session && sample.Sequence == 2 && sample.HasSample && sample.UploadedTotal == 15_000, "Stream reconnection keeps core identity and authoritative totals while resuming the sequence");
        state.RecordTotals(new("test", 0, 0, [], clock.GetUtcNow()));
        Check(state.Snapshot().UploadedTotal == 15_000 && state.Snapshot().DownloadedTotal == 20_000, "Regressing totals cannot overwrite the current core session counters");
        state.RecordTotals(new("test", long.MaxValue, long.MaxValue, [], clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(1));
        state.Record(new(long.MaxValue, long.MaxValue));
        sample = state.Snapshot();
        Check(double.IsFinite(sample.UploadedPerSecond) && sample.UploadedTotal == long.MaxValue, "Large valid counters remain finite without adding stream deltas to totals");
        Check(new CoreTrafficState(clock).Snapshot().SessionId != session, "A new core session receives a new non-secret traffic identity");
        var beforeClockChange = sample.SampledAt;
        clock.WallOffset = TimeSpan.FromHours(-1);
        clock.Advance(TimeSpan.FromSeconds(1));
        state.Record(new(1, 2));
        Check(state.Snapshot().SampledAt == beforeClockChange.AddSeconds(1), "Wall-clock changes cannot reorder traffic timestamps within a core session");

        var metadata = CoreControl.ParseSnapshot("{\"uploadTotal\":1,\"downloadTotal\":2,\"connections\":[{\"id\":\"" + Guid.NewGuid() + "\",\"metadata\":{\"destinationIP\":\"127.0.0.1\"}}]}", "test");
        Check(metadata.Connections.Single().DestinationIp == "127.0.0.1", "Connection snapshots preserve the destination address independently of hostname");
        Check(metadata.TotalConnectionCount == 1 && (metadata with { Connections = [] }).TotalConnectionCount == 1, "IPC detail truncation preserves the authoritative active-connection count");
        var many = CoreControl.ParseSnapshot(JsonSerializer.Serialize(new
        {
            uploadTotal = 1, downloadTotal = 2,
            connections = Enumerable.Range(0, 300).Select(_ => new { id = Guid.NewGuid(), metadata = new { host = new string('a', 400) } }).ToArray()
        }), "test");
        Check(many.TotalConnectionCount == 300 && many.Connections.Count == 256 && many.Connections.All(c => c.Host.Length == 253),
            "Connection parsing counts all valid identities while materializing at most 256 bounded detail records");
        foreach (string invalid in new[] { "{}", "{\"uploadTotal\":-1,\"downloadTotal\":0}", "{\"uploadTotal\":9223372036854775808,\"downloadTotal\":0}" })
        {
            bool rejected = false;
            try { _ = CoreControl.ParseSnapshot(invalid, "test"); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Authoritative connection totals reject missing, negative or overflowing values " + passed.Count);
        }
        BrokerHost.ValidateRequest(new BrokerRequest { Command = "traffic" }, alreadyStarted: true);
        bool beforeStartRejected = false;
        try { BrokerHost.ValidateRequest(new BrokerRequest { Command = "traffic" }, alreadyStarted: false); }
        catch (InvalidDataException) { beforeStartRejected = true; }
        Check(beforeStartRejected, "Traffic IPC is read-only and only available after a core session starts");

        Store.Atomic(Path.Combine(Store.Data, "core-traffic-checks.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Passed = passed.Count, Tests = passed, UsesTun = false, ExternalTraffic = false
        }, Store.JsonOptions));
    }

    sealed class ManualClock : TimeProvider
    {
        long ticks;
        public TimeSpan WallOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks) + WallOffset;
        public void Advance(TimeSpan value) => ticks += value.Ticks;
    }
    sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], token);
    }
    sealed class WaitingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
    }
}
