using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FlowVPN;

internal static class DeveloperAvatarChecks
{
    public static async Task Run()
    {
        var passed = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed.Add(label);
        }
        string folder = Path.Combine(Store.Run, "avatar-check-" + Guid.NewGuid().ToString("N"));
        var clock = new AvatarClock();
        byte[] first = Png(System.Drawing.Color.Teal), second = Png(System.Drawing.Color.Coral);
        using var handler = new FakeHttp();
        using var client = new HttpClient(handler);
        var cache = new DeveloperAvatarCache(folder, client, clock, (bytes, token) => Task.FromResult(bytes));
        try
        {
            var cached = await cache.ReadAsync(CancellationToken.None);
            Check(cached.Image is null, "Missing avatar cache leaves the initials available");
            handler.Queue.Enqueue(request =>
            {
                Check(request.RequestUri == DeveloperAvatarCache.ProfileUri && request.Headers.Authorization is null && request.Headers.UserAgent.Count > 0 &&
                    request.Headers.Contains("X-GitHub-Api-Version"), "Avatar metadata uses the fixed public GitHub endpoint without authorization");
                return Profile("\"profile-1\"");
            });
            handler.Queue.Enqueue(request =>
            {
                Check(request.RequestUri!.Host == "avatars.githubusercontent.com" && request.RequestUri.Query.Contains("s=128") && request.Headers.Authorization is null,
                    "Avatar image uses the allowlisted HTTPS host with a small requested size");
                return Response(HttpStatusCode.OK, first, "\"image-1\"");
            });
            byte[]? added = await cache.RefreshAsync(cached, CancellationToken.None);
            Check(added != null && added.SequenceEqual(first) && File.Exists(cache.ImagePath), "First successful refresh caches an image and its metadata");
            cached = await cache.ReadAsync(CancellationToken.None);
            int calls = handler.Calls;
            Check(await cache.RefreshAsync(cached, CancellationToken.None) is null && handler.Calls == calls, "Fresh 24-hour avatar cache performs no HTTP requests");

            DateTime untouched = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(cache.ImagePath, untouched);
            clock.Now = clock.Now.AddHours(25);
            handler.Queue.Enqueue(request =>
            {
                Check(request.Headers.IfNoneMatch.Single().ToString() == "\"profile-1\"", "Profile refresh sends its own ETag");
                return Response(HttpStatusCode.NotModified, []);
            });
            handler.Queue.Enqueue(request =>
            {
                Check(request.Headers.IfNoneMatch.Single().ToString() == "\"image-1\"", "Image refresh still runs after profile 304 and sends an independent ETag");
                return Response(HttpStatusCode.NotModified, []);
            });
            Check(await cache.RefreshAsync(cached, CancellationToken.None) is null && File.GetLastWriteTimeUtc(cache.ImagePath) == untouched,
                "Image 304 updates only small metadata and never rewrites the unchanged image");
            cached = await cache.ReadAsync(CancellationToken.None);
            Check(cached.Metadata?.CheckedAt == clock.Now, "Successful conditional refresh advances the daily check timestamp");

            clock.Now = clock.Now.AddHours(25);
            handler.Queue.Enqueue(_ => Response(HttpStatusCode.NotModified, []));
            handler.Queue.Enqueue(_ => Response(HttpStatusCode.OK, second, "\"image-2\""));
            byte[]? changed = await cache.RefreshAsync(cached, CancellationToken.None);
            Check(changed != null && changed.SequenceEqual(second), "A changed image replaces the cache even when avatar URL and profile ETag are unchanged");
            cached = await cache.ReadAsync(CancellationToken.None);
            Check(cached.Image!.SequenceEqual(second) && cached.Metadata?.ImageETag == "\"image-2\"", "Cache and independent image ETag remain consistent after replacement");

            clock.Now = clock.Now.AddHours(25);
            File.SetLastWriteTimeUtc(cache.ImagePath, untouched);
            handler.Queue.Enqueue(_ => Profile("\"profile-2\""));
            handler.Queue.Enqueue(_ => Response(HttpStatusCode.OK, second, "\"image-3\""));
            Check(await cache.RefreshAsync(cached, CancellationToken.None) is null && File.GetLastWriteTimeUtc(cache.ImagePath) == untouched,
                "A server 200 with identical normalized bytes also avoids rewriting the image");

            foreach (string bad in new[] { "http://avatars.githubusercontent.com/u/1", "https://avatars.githubusercontent.com.evil.example/u/1", "https://user@avatars.githubusercontent.com/u/1",
                "https://avatars.githubusercontent.com:444/u/1", "https://127.0.0.1/u/1", "https://api.github.com/u/1", "https://avatars.githubusercontent.com/u/1#fragment" })
            {
                bool rejected = false;
                try { _ = DeveloperAvatarCache.ImageUri(bad); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Avatar address rejects an untrusted scheme, host, credentials, port or fragment " + passed.Count);
            }
            byte[] hugeDimensions = first.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(hugeDimensions.AsSpan(16, 4), 1025);
            bool dimensionsRejected = false;
            try { DeveloperAvatarCache.ValidateImageHeader(hugeDimensions, 1024); }
            catch (InvalidDataException) { dimensionsRejected = true; }
            Check(dimensionsRejected, "Oversized source dimensions are rejected before native pixel decoding");
            byte[] normalized = await DeveloperAvatarCache.NormalizeImage(Png(System.Drawing.Color.Teal, 256), CancellationToken.None);
            DeveloperAvatarCache.ValidateImageHeader(normalized, 128);
            Check(BinaryPrimitives.ReadUInt32BigEndian(normalized.AsSpan(16, 4)) == 128, "Native image decoding produces a canonical PNG downscaled to 128 pixels");

            async Task RejectRefresh(HttpResponseMessage profileResponse, HttpResponseMessage? imageResponse, string label)
            {
                using var fake = new FakeHttp();
                fake.Queue.Enqueue(_ => profileResponse);
                if (imageResponse != null) fake.Queue.Enqueue(_ => imageResponse);
                using var http = new HttpClient(fake);
                var isolated = new DeveloperAvatarCache(Path.Combine(folder, "rejected"), http, clock, (bytes, token) => Task.FromResult(bytes));
                bool rejected = false;
                try { await isolated.RefreshAsync(new(null, null), CancellationToken.None); }
                catch (Exception error) when (error is InvalidDataException or HttpRequestException or JsonException) { rejected = true; }
                Check(rejected && !File.Exists(isolated.ImagePath), label);
            }
            await RejectRefresh(Response(HttpStatusCode.OK, new byte[DeveloperAvatarCache.MaxJsonBytes + 1]), null, "Oversized public JSON is bounded before parsing or caching");
            await RejectRefresh(Profile("\"p\""), Response(HttpStatusCode.OK, new byte[DeveloperAvatarCache.MaxImageBytes + 1]), "Oversized image downloads cannot grow the cache");
            await RejectRefresh(Profile("\"p\""), Response(HttpStatusCode.OK, hugeDimensions), "Oversized image dimensions cannot reach normalization or cache writes");
            var redirect = Response(HttpStatusCode.Found, []);
            redirect.Headers.Location = new Uri("https://untrusted.example/avatar.png");
            await RejectRefresh(redirect, null, "Unexpected redirects are rejected instead of followed");
            await RejectRefresh(Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{\"avatar_url\":\"https://untrusted.example/avatar.png\"}")), null,
                "Untrusted avatar URLs in public JSON are rejected before an image request");

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                calls = handler.Calls;
                bool cancelled = false;
                try { await cache.RefreshAsync(new(null, null), cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && handler.Calls == calls, "Window cancellation prevents requests and cache writes");
            }
            using (var blocked = new BlockingHttp())
            using (var http = new HttpClient(blocked))
            using (var cancellation = new CancellationTokenSource())
            {
                var isolated = new DeveloperAvatarCache(Path.Combine(folder, "cancelled"), http, clock);
                Task<byte[]?> refresh = isolated.RefreshAsync(new(null, null), cancellation.Token);
                cancellation.Cancel();
                bool cancelled = false;
                try { await refresh; }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && !File.Exists(isolated.MetadataPath), "Window cancellation completes an in-flight HTTP request without writing metadata");
            }
            Check(handler.Queue.Count == 0, "All fake public requests were consumed without any real network access");
        }
        finally
        {
            // This path is a unique test-created child of Store.Run, never a caller-supplied directory.
            string absolute = Path.GetFullPath(folder), parent = Path.GetFullPath(Store.Run).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected avatar test directory.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
        Store.Atomic(Path.Combine(Store.Data, "developer-avatar-checks.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Passed = passed.Count, Tests = passed, UsesNetwork = false
        }, Store.JsonOptions));
    }

    static byte[] Png(System.Drawing.Color color, int size = 1)
    {
        using var bitmap = new System.Drawing.Bitmap(size, size);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }
    static HttpResponseMessage Profile(string etag) => Response(HttpStatusCode.OK,
        Encoding.UTF8.GetBytes("{\"avatar_url\":\"https://avatars.githubusercontent.com/u/12121164?v=4\"}"), etag);
    static HttpResponseMessage Response(HttpStatusCode status, byte[] bytes, string? etag = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        if (etag != null) response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }
    sealed class FakeHttp : HttpMessageHandler
    {
        public readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> Queue = new();
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            if (Queue.Count == 0) throw new InvalidOperationException("Unexpected avatar request.");
            return Task.FromResult(Queue.Dequeue()(request));
        }
    }
    sealed class AvatarClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed class BlockingHttp : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("A cancelled request cannot complete.");
        }
    }
}
