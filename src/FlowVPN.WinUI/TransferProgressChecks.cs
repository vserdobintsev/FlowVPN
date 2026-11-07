using System.Net;

namespace FlowVPN;

internal static class TransferProgressChecks
{
    sealed class Recorder(Action<TransferProgress>? received = null) : IProgress<TransferProgress>
    {
        public readonly List<TransferProgress> Reports = [];
        public void Report(TransferProgress value) { Reports.Add(value); received?.Invoke(value); }
    }
    sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response());
    }
    sealed class NonSeekingMemory(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    static HttpClient Client(byte[] bytes, long? length, bool unknown = false) => new(new ResponseHandler(() =>
    {
        HttpContent content = unknown ? new StreamContent(new NonSeekingMemory(bytes)) : new ByteArrayContent(bytes);
        if (!unknown) content.Headers.ContentLength = length;
        return new(HttpStatusCode.OK) { Content = content };
    }));

    public static async Task Run(Action<bool, string> check)
    {
        byte[] bytes = new byte[140_000];
        var known = new Recorder();
        using (var client = Client(bytes, bytes.Length))
            check((await BoundedHttp.Download(client, "https://example.test/data", bytes.Length, CancellationToken.None, known, "Test")).Length == bytes.Length,
                "Progress reporting preserves bounded response bytes");
        check(known.Reports[0] is { BytesReceived: 0, TotalBytes: 140_000, Stage: TransferStage.Downloading } &&
            known.Reports[^1] is { BytesReceived: 140_000, TotalBytes: 140_000 }, "Known-length progress reports actual initial and final byte counts");
        check(known.Reports.All(r => r.BytesReceived >= 0 && r.TotalBytes is { } total && r.BytesReceived <= total && r.Item == "Test"), "Download progress never claims more bytes than the response length");
        var unknown = new Recorder();
        using (var client = Client(bytes, null, unknown: true))
            await BoundedHttp.Download(client, "https://example.test/data", bytes.Length, CancellationToken.None, unknown);
        check(unknown.Reports.All(r => r.TotalBytes == null) && unknown.Reports[^1].BytesReceived == bytes.Length, "Chunked downloads expose bytes without inventing a percentage");
        bool truncated = false;
        using (var client = Client([1, 2], 3))
            try { await BoundedHttp.Download(client, "https://example.test/data", 10, CancellationToken.None); }
            catch (EndOfStreamException) { truncated = true; }
        check(truncated, "Truncated Content-Length remains a validation error");
        bool extra = false;
        using (var client = Client([1, 2, 3], 2))
            try { await BoundedHttp.Download(client, "https://example.test/data", 10, CancellationToken.None); }
            catch (InvalidDataException) { extra = true; }
        check(extra, "Bytes beyond Content-Length remain a validation error");
        bool oversized = false;
        using (var client = Client(bytes, null, unknown: true))
            try { await BoundedHttp.Download(client, "https://example.test/data", 1000, CancellationToken.None); }
            catch (InvalidDataException) { oversized = true; }
        check(oversized, "Unknown-length transfers still stop at their configured size limit");
        using var cancellation = new CancellationTokenSource();
        bool cancelled = false;
        using (var client = Client(bytes, bytes.Length))
            try { await BoundedHttp.Download(client, "https://example.test/data", bytes.Length, cancellation.Token, new Recorder(_ => cancellation.Cancel())); }
            catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "Cancellation interrupts a transfer before reading further chunks");
        bool parsingCancelled = false;
        try { GeoSiteReader.Read([10, 0], token: cancellation.Token); }
        catch (OperationCanceledException) { parsingCancelled = true; }
        check(parsingCancelled, "Category parsing observes cancellation before processing entries");
    }
}
