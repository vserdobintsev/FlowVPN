using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlowVPN;

internal static class CoreControlChecks
{
    public static async Task Run()
    {
        var passed = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition)
                throw new InvalidOperationException(label);
            passed.Add(label);
        }
        var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start();
        int proxyPort = ((IPEndPoint)reserve.LocalEndpoint).Port;
        reserve.Stop();
        using var api = new CoreControl(proxyPort);
        var proxyAccess = new ProxyAccess();
        string file = Path.Combine(Store.Run, "clash-api-check-" + Guid.NewGuid().ToString("N") + ".json");
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["disabled"] = true },
            ["inbounds"] = new JsonArray { new JsonObject { ["type"] = "mixed", ["listen"] = "127.0.0.1", ["listen_port"] = proxyPort, ["users"] = proxyAccess.Users() } },
            ["outbounds"] = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = "direct" } },
            ["route"] = new JsonObject { ["final"] = "direct" },
            ["experimental"] = api.Configuration()
        };
        Process? process = null;
        CoreIntegrity? verifiedCore = null;
        CoreJob? job = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            Store.WriteJson(file, config);
            await CoreCli.Run("check", "-c", file);
            Check(api.Port != proxyPort && api.Secret.Length == 64 && config["experimental"]!["clash_api"]!["external_controller"]!.ToString().StartsWith("127.0.0.1:"), "Control API is loopback-only with a fresh 256-bit secret and a separate port");
            var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string arg in new[] { "--disable-color", "run", "-c", file })
                start.ArgumentList.Add(arg);
            verifiedCore = await Task.Run(() => CoreIntegrity.Start(start));
            process = verifiedCore.Process;
            job = new CoreJob(process);
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            CoreSnapshot? snapshot = null;
            while (snapshot == null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException("API test core failed: " + await stderr);
                try
                {
                    snapshot = await api.Snapshot(timeout.Token);
                }
                catch (HttpRequestException) { await Task.Delay(100, timeout.Token); }
            }
            Check(snapshot.Version.Contains(UpdateProtocol.ExpectedCoreVersion()) && snapshot.Connections.Count == 0, "Bundled sing-box answers version and initial connection snapshot over authenticated API");
            using var anonymous = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var response = await anonymous.GetAsync($"http://127.0.0.1:{api.Port}/version", timeout.Token);
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "Unauthenticated API requests are rejected");
            using var unauthenticatedProxy = new TcpClient();
            await unauthenticatedProxy.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await unauthenticatedProxy.GetStream().WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
            byte[] negotiation = new byte[2];
            await unauthenticatedProxy.GetStream().ReadExactlyAsync(negotiation, timeout.Token);
            Check(negotiation.SequenceEqual(new byte[] { 5, 255 }), "Loopback SOCKS5 proxy rejects anonymous authentication before accepting a destination");
            using var traffic = new HttpClient(new HttpClientHandler { UseProxy = true, Proxy = proxyAccess.At(proxyPort) });
            int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            var transfer = traffic.GetStringAsync($"http://127.0.0.1:{targetPort}/test", timeout.Token);
            using var accepted = await target.AcceptTcpClientAsync(timeout.Token);
            var requestBuffer = new byte[1];
            await accepted.GetStream().ReadExactlyAsync(requestBuffer, timeout.Token);
            await accepted.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100000\r\n\r\nx"), timeout.Token);
            do
            {
                snapshot = await api.Snapshot(timeout.Token);
                if (snapshot.Connections.Count == 0)
                    await Task.Delay(100, timeout.Token);
            } while (snapshot.Connections.Count == 0);
            Check(snapshot.Connections.Count == 1, "API reports a real loopback-only proxied connection");
            await api.CloseConnection(snapshot.Connections[0].Id, timeout.Token);
            do
            {
                snapshot = await api.Snapshot(timeout.Token);
                if (snapshot.Connections.Count > 0)
                    await Task.Delay(100, timeout.Token);
            } while (snapshot.Connections.Count > 0);
            bool interrupted = false;
            try
            {
                await transfer;
            }
            catch (HttpRequestException) { interrupted = true; }
            catch (OperationCanceledException) { interrupted = true; }
            Check(interrupted && snapshot.Connections.Count == 0, "DELETE closes the selected real connection and updates the snapshot");
            bool rejected = false;
            try
            {
                await api.CloseConnection("../../configs", timeout.Token);
            }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Connection commands reject arbitrary paths instead of issuing unrelated API requests");
        }
        finally
        {
            target.Stop();
            job?.Dispose();
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                    await process.WaitForExitAsync();
                }
                finally { verifiedCore?.Dispose(); }
            }
            File.Delete(file);
        }
        Store.Atomic(Path.Combine(Store.Data, "core-api-checks.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Passed = passed.Count,
            Tests = passed,
            UsesTun = false,
            ExternalTraffic = false
        }, Store.JsonOptions));
    }
}
