using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace FlowVPN;

internal static class ResumableDownloadChecks
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLink(string name, string existing, IntPtr reserved);
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    sealed class Observer(Action<TransferProgress> report) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) => report(value); }
    static HttpResponseMessage Response(byte[] bytes, long declared, long? offset = null)
    {
        var response = new HttpResponseMessage(offset == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentLength = declared;
        if (offset != null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset.Value, offset.Value + declared - 1, 100);
        return response;
    }
    public static async Task Run(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("FlowVPN-resume-checks-");
        byte[] bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        Task<byte[]> Download(HttpClient client, string name, CancellationToken token = default, IProgress<TransferProgress>? progress = null) =>
            ResumableDownload.Download(client, "https://example.test/data", hash, 100, Path.Combine(directory.FullName, name), token, progress, exactBytes: 100);
        try
        {
            int requests = 0;
            using (var client = new HttpClient(new Handler(request =>
            {
                requests++;
                if (requests == 1) return Response(bytes[..40], 100);
                check(request.Headers.Range?.Ranges.Single().From == 40, "A truncated artifact is resumed at its actual persisted byte offset");
                return Response(bytes[40..], 60, 40);
            })))
                check((await Download(client, "resume")).SequenceEqual(bytes) && requests == 2, "A valid partial response completes the pinned artifact without restarting");
            requests = 0;
            using (var client = new HttpClient(new Handler(_ => ++requests == 1 ? Response(bytes[..40], 100) : Response(bytes, 100))))
                check((await Download(client, "no-range")).SequenceEqual(bytes) && requests == 2, "A server ignoring Range safely restarts rather than appending duplicate bytes");
            requests = 0;
            using (var client = new HttpClient(new Handler(_ => { requests++; return Response(bytes[..40], 100); })))
            {
                bool rejected = false;
                try { await Download(client, "three"); } catch (IOException) { rejected = true; }
                check(rejected && requests == 3, "A failing server without resume support receives exactly three attempts");
            }
            using (var cancellation = new CancellationTokenSource())
            using (var client = new HttpClient(new Handler(_ => Response(bytes[..40], 100))))
            {
                bool canceled = false;
                try { await Download(client, "across-calls", cancellation.Token, new Observer(p => { if (p.BytesReceived > 0) cancellation.Cancel(); })); }
                catch (OperationCanceledException) { canceled = true; }
                check(canceled && Directory.EnumerateFiles(Path.Combine(directory.FullName, "across-calls"), "*.part").Any(), "User cancellation preserves non-executable partial bytes for another invocation");
            }
            using (var client = new HttpClient(new Handler(request =>
            {
                check(request.Headers.Range?.Ranges.Single().From == 40, "A new download invocation resumes the persisted cache");
                return Response(bytes[40..], 60, 40);
            })))
                check((await Download(client, "across-calls")).SequenceEqual(bytes), "Persisted resume verifies the entire artifact before returning it");
            requests = 0;
            using (var client = new HttpClient(new Handler(_ => ++requests == 1 ? Response(bytes[..40], 100) : Response(bytes[40..], 60, 39))))
            {
                bool rejected = false;
                try { await Download(client, "bad-range"); } catch (InvalidDataException) { rejected = true; }
                check(rejected, "A mismatching Content-Range is rejected without joining unrelated bytes");
            }
            using (var client = new HttpClient(new Handler(_ => Response(new byte[100], 100))))
            {
                bool rejected = false;
                try { await Download(client, "bad-hash"); } catch (InvalidDataException) { rejected = true; }
                check(rejected && new FileInfo(Directory.GetFiles(Path.Combine(directory.FullName, "bad-hash"), "*.part").Single()).Length == 0,
                    "Hash mismatch clears untrusted partial content and never returns artifact bytes");
            }
            string partial = Directory.GetFiles(Path.Combine(directory.FullName, "three"), "*.part").Single();
            string alias = Path.Combine(directory.FullName, "outside-cache.bin");
            check(CreateHardLink(alias, partial, IntPtr.Zero), "The cache hard-link rejection fixture is created on the local disk");
            using (var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("A linked cache must never trigger a request"))))
            {
                bool rejected = false;
                try { await Download(client, "three"); } catch (InvalidDataException) { rejected = true; }
                check(rejected && File.ReadAllBytes(alias).SequenceEqual(bytes[..40]), "A hard-linked partial is rejected before network access or modification");
            }
            string expired = Path.Combine(directory.FullName, "expired");
            Directory.CreateDirectory(expired);
            string key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("https://example.test/data\n" + hash))) + ".part";
            File.WriteAllBytes(Path.Combine(expired, key), bytes[..40]);
            File.SetLastWriteTimeUtc(Path.Combine(expired, key), DateTime.UtcNow.AddDays(-8));
            using (var client = new HttpClient(new Handler(request =>
            {
                check(request.Headers.Range == null, "An expired partial for the current artifact is discarded before downloading");
                return Response(bytes, 100);
            }))) await Download(client, "expired");
            string bounded = Path.Combine(directory.FullName, "bounded");
            Directory.CreateDirectory(bounded);
            for (int index = 0; index < 14; index++) File.WriteAllBytes(Path.Combine(bounded, index + ".part"), [1]);
            using (var client = new HttpClient(new Handler(_ => Response(bytes, 100)))) await Download(client, "bounded");
            check(Directory.GetFiles(bounded, "*.part").Length == 7, "Cache pruning leaves seven previous partials and reserves one slot for the active download");
        }
        finally { directory.Delete(true); }
    }
}
