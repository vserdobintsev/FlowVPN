using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace FlowVPN;

internal static class BrokerTransportChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        string nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        using (var frame = new MemoryStream())
        {
            var request = new BrokerRequest { Command = "close", ConnectionId = Guid.NewGuid().ToString() };
            await BrokerWire.WriteAsync(frame, request, token);
            using var fragmented = new FragmentedStream(frame.ToArray());
            var result = await BrokerWire.ReadAsync<BrokerRequest>(fragmented, token);
            check(result.Command == request.Command && result.ConnectionId == request.ConnectionId,
                "Broker frames round-trip across fragmented pipe reads");
        }
        foreach (int length in new[] { -1, 0, BrokerWire.MaxFrameBytes + 1, int.MaxValue })
        {
            using var frame = Frame("", length);
            check(await Rejected(() => BrokerWire.ReadAsync<BrokerRequest>(frame, token)),
                "Broker rejects invalid advertised frame length " + length);
        }
        foreach (var example in new[]
        {
            ("{\"Command\":\"status\",\"Command\":\"stop\"}", "duplicate command"),
            ("{\"Command\":\"start\",\"Plan\":{\"Kind\":\"Outline\",\"Kind\":\"OpenVPN\"}}", "nested duplicate field"),
            ("{\"Command\":\"status\",\"FileName\":\"cmd.exe\"}", "unknown command field"),
            ("{\"Command\":\"start\",\"Plan\":{\"Executable\":\"cmd.exe\"}}", "unknown plan field"),
            ("{\"Command\":\"status\"} {}", "trailing JSON"),
            ("null", "null request")
        })
        {
            using var frame = Frame(example.Item1);
            check(await Rejected(() => BrokerWire.ReadAsync<BrokerRequest>(frame, token)), "Broker rejects " + example.Item2);
        }
        using (var frame = Frame("{", 100))
            check(await Rejected(() => BrokerWire.ReadAsync<BrokerRequest>(frame, token)), "Broker rejects a truncated payload");
        using (var frame = Frame(new string('[', 33) + "0" + new string(']', 33)))
            check(await Rejected(() => BrokerWire.ReadAsync<BrokerRequest>(frame, token)), "Broker limits JSON nesting before deserialization");
        using (var frame = Frame("{\"Command\":\"status\"}"))
            check(await Rejected(() => BrokerWire.ReadAsync<BrokerRequest>(frame, token, 4)), "Broker enforces the smaller control-frame limit");
        using (var frame = new MemoryStream())
            check(await Rejected(() => BrokerWire.WriteAsync(frame, new BrokerHandshake { Nonce = nonce }, token, 4)) && frame.Length == 0,
                "Broker rejects oversized writes before sending a partial frame");
        BrokerWire.ValidateHandshake(new BrokerHandshake { Nonce = nonce.ToLowerInvariant() }, nonce);
        check(Rejected(() => BrokerWire.ValidateHandshake(new BrokerHandshake { Version = 2, Nonce = nonce }, nonce)) &&
            Rejected(() => BrokerWire.ValidateHandshake(new BrokerHandshake { Nonce = (nonce[0] == '0' ? "1" : "0") + nonce[1..] }, nonce)) &&
            Rejected(() => BrokerWire.ValidateHandshake(new BrokerHandshake { Nonce = "bad" }, nonce)),
            "Broker handshake checks protocol version and the complete nonce");

        string pipeName = BrokerPeer.CreateName();
        var launch = BrokerHost.ParseArguments(["--broker", pipeName, nonce, int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        check(launch.Name == pipeName && launch.Nonce == nonce && launch.ParentId == int.MaxValue &&
            Rejected(() => BrokerHost.ParseArguments(["--broker", pipeName, nonce, "1", "--root", @"C:\attacker"])) &&
            Rejected(() => BrokerHost.ParseArguments(["--broker", @"remote\pipe", nonce, "1"])) &&
            Rejected(() => BrokerHost.ParseArguments(["--broker", pipeName, nonce, "-1"])) &&
            Rejected(() => BrokerHost.ParseArguments(["--broker", pipeName, nonce, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)])),
            "Broker launch accepts only the four fixed arguments and an external parent PID");
        check(Rejected(() => BrokerHost.ValidateRequest(new BrokerRequest { Command = "execute" }, false)) &&
            Rejected(() => BrokerHost.ValidateRequest(new BrokerRequest { Command = "start", Plan = new() }, true)) &&
            Rejected(() => BrokerHost.ValidateRequest(new BrokerRequest { Command = "status", Plan = new() }, true)) &&
            Rejected(() => BrokerHost.ValidateRequest(new BrokerRequest { Command = "close", ConnectionId = "../../configs" }, true)) &&
            Rejected(() => BrokerHost.ValidateRequest(new BrokerRequest { Command = "stop", ConnectionId = Guid.NewGuid().ToString() }, true)),
            "Broker commands cannot restart, carry unexpected payloads or address arbitrary API paths");

        var logs = new BrokerHost.LogBuffer();
        using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 65536)))))
            await logs.Capture(reader);
        string[] longLine = logs.Drain();
        check(longLine.Length == 1 && longLine[0].Length == 2001, "Broker bounds unterminated engine log lines");
        using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 1000))))))
            await logs.Capture(reader);
        string[] burst = logs.Drain();
        check(burst.Length == 129 && burst[0].Contains("872", StringComparison.Ordinal) && burst[^1] == "999" && logs.Drain().Length == 0,
            "Broker log bursts retain a bounded tail and report dropped lines");

        // This is a local, unelevated duplex test. No UAC prompt, engine process or TUN is created.
        if (Native.IsAdmin)
            check(Rejected(() => { using var forbidden = BrokerPeer.CreateServer(pipeName); }), "An elevated UI cannot create a production broker channel");
        else
        {
            using var ordinary = BrokerPeer.CreateServer(BrokerPeer.CreateName());
            check(ordinary != null, "An ordinary UI can create its production broker channel");
        }
        using var server = BrokerPeer.CreateServerCore(pipeName);
        check(Rejected(() => { using var duplicate = BrokerPeer.CreateServerCore(pipeName); }), "Broker refuses a second pipe instance");
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        await Task.WhenAll(server.WaitForConnectionAsync(token), client.ConnectAsync(token));
        using var self = Process.GetCurrentProcess();
        check(Rejected(() => BrokerPeer.ValidateClient(server, self)) && Rejected(() => BrokerPeer.ValidateServer(client, self)),
            "Broker peer authentication rejects equal-elevation processes despite a matching SID and PID");
        Task session = BrokerHost.RunSessionAsync(client, token);
        await BrokerWire.WriteAsync(server, new BrokerRequest { Command = "status" }, token);
        var status = await BrokerWire.ReadAsync<BrokerReply>(server, token);
        check(status.Success && status.State is { Running: false, ProcessId: 0, ProxyPassword: "", Snapshot: null },
            "An idle broker returns bounded state without process handles or control secrets");
        await BrokerWire.WriteAsync(server, new BrokerRequest { Command = "stop" }, token);
        var stopped = await BrokerWire.ReadAsync<BrokerReply>(server, token);
        await session.WaitAsync(token);
        check(stopped.Success && stopped.State is { Running: false }, "Broker stop acknowledges and terminates its single session");

        string disconnectName = BrokerPeer.CreateName();
        using var disconnectServer = BrokerPeer.CreateServerCore(disconnectName);
        using var disconnectClient = new NamedPipeClientStream(".", disconnectName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        await Task.WhenAll(disconnectServer.WaitForConnectionAsync(token), disconnectClient.ConnectAsync(token));
        Task disconnected = BrokerHost.RunSessionAsync(disconnectClient, token);
        disconnectServer.Dispose();
        check(await Rejected(() => disconnected.WaitAsync(token)), "Closing the UI channel terminates an idle broker session");
    }

    static MemoryStream Frame(string value, int? advertised = null)
    {
        byte[] payload = Encoding.UTF8.GetBytes(value), data = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data, advertised ?? payload.Length);
        payload.CopyTo(data, 4);
        return new MemoryStream(data);
    }
    static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or Win32Exception) { return true; }
    }
    static async Task<bool> Rejected(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or ArgumentException) { return true; }
    }
    sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
}
