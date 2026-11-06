using System.Net;

namespace FlowVPN;

public enum VpnHealth
{
    Stopped, Checking, Online, Unreachable, NoNetwork, NetworkChanged
}

public static class VpnConnectivity
{
    // A process being alive is not evidence of a working tunnel. Probe through its private SOCKS inbound.
    public static async Task<string?> Probe(HttpClient client, CancellationToken token)
    {
        foreach (string url in new[] { "https://api.ipify.org", "https://www.cloudflare.com/cdn-cgi/trace" })
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var buffer = new MemoryStream();
                var bytes = new byte[1024];
                int count;
                while ((count = await stream.ReadAsync(bytes, deadline.Token)) > 0)
                {
                    if (buffer.Length + count > 8192)
                        throw new InvalidDataException("Unexpected connectivity response size");
                    buffer.Write(bytes, 0, count);
                }
                string? ip = ParseAddress(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
                if (ip != null)
                    return ip;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
            }
        }
        return null;
    }

    public static string? ParseAddress(string response)
    {
        string value = response.Trim();
        if (IPAddress.TryParse(value, out var address))
            return address.ToString();
        foreach (string line in value.Split('\n'))
            if (line.StartsWith("ip=", StringComparison.Ordinal) && IPAddress.TryParse(line[3..].Trim(), out address))
                return address.ToString();
        return null;
    }

    public static VpnHealth Assess(bool physicalNetwork, bool sameInterface, bool probeSucceeded) =>
        !physicalNetwork ? VpnHealth.NoNetwork : !sameInterface ? VpnHealth.NetworkChanged : probeSucceeded ? VpnHealth.Online : VpnHealth.Unreachable;
}
