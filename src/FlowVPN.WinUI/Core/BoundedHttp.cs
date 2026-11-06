namespace FlowVPN;

internal static class BoundedHttp
{
    public static HttpClient Client(TimeSpan timeout, System.Net.IWebProxy? proxy = null) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, Proxy = proxy })
        {
            Timeout = timeout
        };
    public static Uri Https(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        ? uri : throw new InvalidDataException("Источник должен использовать HTTPS без учётных данных в адресе.");
    public static async Task<HttpResponseMessage> Open(HttpClient client, Uri uri, CancellationToken token)
    {
        for (int redirect = 0; redirect < 6; redirect++)
        {
            Https(uri.AbsoluteUri);
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            try
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
                {
                    uri = new Uri(uri, response.Headers.Location);
                    response.Dispose();
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("Слишком много перенаправлений источника.");
    }
    public static async Task<byte[]> Download(HttpClient client, string url, int maxBytes, CancellationToken token)
    {
        using var response = await Open(client, Https(url), token);
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("Ответ источника больше ожидаемого размера.");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        if (response.Content.Headers.ContentLength is long length)
        {
            var bytes = new byte[checked((int)length)];
            await stream.ReadExactlyAsync(bytes, token);
            if (await stream.ReadAsync(new byte[1], token) != 0)
                throw new InvalidDataException("Размер ответа источника не совпал.");
            return bytes;
        }
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + count > maxBytes)
                throw new InvalidDataException("Ответ источника больше ожидаемого размера.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
}
