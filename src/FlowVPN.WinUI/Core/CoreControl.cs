using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlowVPN;

public sealed record CoreConnection(string Id, string Host, string ProcessPath, string Route, long Uploaded, long Downloaded, string DestinationIp = "");
public sealed record CoreSnapshot(string Version, long Uploaded, long Downloaded, IReadOnlyList<CoreConnection> Connections, DateTimeOffset CapturedAt, int? TotalConnectionCount = null);

// The management API is local, authenticated and separate from the tunnel's traffic probe.
public sealed class CoreControl : IDisposable
{
    public const int MaximumConnections = 256;
    readonly HttpClient client;
    public int Port
    {
        get;
    }
    public string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    string version = "";
    public CoreControl(int excludePort = 0)
    {
        do
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
        } while (Port == excludePort);
        client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{Port}/"),
            Timeout = TimeSpan.FromSeconds(4),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
    }
    public JsonObject Configuration() => new()
    {
        ["clash_api"] = new JsonObject
        {
            ["external_controller"] = $"127.0.0.1:{Port}",
            ["secret"] = Secret,
            ["access_control_allow_origin"] = new JsonArray("http://127.0.0.1"),
            ["access_control_allow_private_network"] = false
        }
    };
    public async Task<CoreSnapshot> Snapshot(CancellationToken token)
    {
        if (version.Length == 0)
        {
            var data = JsonNode.Parse(await client.GetStringAsync("version", token));
            version = data?["version"]?.GetValue<string>() ?? throw new InvalidDataException("Движок не сообщил версию API.");
        }
        return ParseSnapshot(await client.GetStringAsync("connections", token), version);
    }
    public static CoreSnapshot ParseSnapshot(string json, string version)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var data = document.RootElement;
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Некорректный ответ API.");
        long uploaded = Counter(data, "uploadTotal", required: true), downloaded = Counter(data, "downloadTotal", required: true);
        var connections = new List<CoreConnection>(MaximumConnections);
        int total = 0;
        if (data.TryGetProperty("connections", out var items) && items.ValueKind != JsonValueKind.Null)
        {
            if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Некорректный список соединений API.");
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var value) ||
                    value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id)) continue;
                total++;
                if (connections.Count == MaximumConnections) continue;
                item.TryGetProperty("metadata", out var metadata);
                string route = "";
                if (item.TryGetProperty("chains", out var chains) && chains.ValueKind == JsonValueKind.Array)
                {
                    foreach (var chain in chains.EnumerateArray())
                    {
                        if (chain.ValueKind != JsonValueKind.String) continue;
                        route += (route.Length == 0 ? "" : " → ") + Trim(chain.GetString() ?? "", 256);
                        if (route.Length >= 256) { route = Trim(route, 256); break; }
                    }
                }
                connections.Add(new(id.ToString("D"), Text(metadata, "host", 253), Text(metadata, "processPath", 512), route,
                    Counter(item, "upload"), Counter(item, "download"), Text(metadata, "destinationIP", 64)));
            }
        }
        return new(version, uploaded, downloaded, connections, DateTimeOffset.Now, total);

        static string Trim(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
        static string Text(JsonElement item, string name, int maximum) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Trim(value.GetString() ?? "", maximum) : "";
        static long Counter(JsonElement item, string name, bool required = false)
        {
            if (!item.TryGetProperty(name, out var value))
                return required ? throw new InvalidDataException("Отсутствует счётчик API.") : 0;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number >= 0
                ? number : throw new InvalidDataException("Некорректный счётчик API.");
        }
    }
    internal async Task ReadTraffic(Action<CoreTrafficDelta> receive, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "traffic");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await CoreTrafficMonitor.ReadFrames(stream, receive, token).ConfigureAwait(false);
    }
    public async Task CloseConnection(string id, CancellationToken token)
    {
        if (!Guid.TryParse(id, out _))
            throw new ArgumentException("Некорректный идентификатор соединения.", nameof(id));
        using var response = await client.DeleteAsync("connections/" + Uri.EscapeDataString(id), token);
        response.EnsureSuccessStatusCode();
    }
    public void Dispose() => client.Dispose();
}
