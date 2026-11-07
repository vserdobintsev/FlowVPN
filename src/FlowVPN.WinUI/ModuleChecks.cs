using System.Net;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FlowVPN;

internal static class ModuleChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("FlowVPN isolated module fixture: never an executable.");
        var definition = new ModuleDefinition("outline-transport", "1.0.0", "Outline", "Fixture", "flowvpn-outline-transport.exe",
            "https://github.com/vserdobintsev/FlowVPN/releases/download/outline-transport-v1.0.0/flowvpn-outline-transport.exe",
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), ["outline.ss-prefix.tcp.v1"]);
        var fixtureDirectory = Directory.CreateTempSubdirectory("FlowVPN-module-checks-");
        string fixture = fixtureDirectory.FullName;
        using var handler = new TestHandler();
        using var client = new HttpClient(handler);
        ModuleManager Manager(string name, ModuleDefinition? item = null) => new(Path.Combine(fixture, name), new ModuleCatalog([item ?? definition]), client);
        async Task Reject(Func<Task> action, string label)
        {
            bool rejected = false;
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException or OperationCanceledException)
            { rejected = true; }
            check(rejected, label);
        }
        static bool Locked(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
        }
        handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        try
        {
            var manager = Manager("happy");
            check((await manager.GetStatusAsync(definition.Id)).State == ModuleState.Missing, "Module status does not invent installation or create directories");
            check(!Directory.Exists(Path.Combine(fixture, "happy")), "Checking a missing module does not mutate its filesystem");
            int progressEvents = 0;
            var stages = new HashSet<ModuleProgressStage>();
            manager.Changed += (_, _) => throw new InvalidOperationException("Fixture observer failure");
            manager.Changed += (_, _) =>
            {
                if (manager.DownloadProgress is { } progress) { progressEvents++; stages.Add(progress.Stage); }
            };
            await manager.InstallAsync(definition.Id);
            check((await manager.GetStatusAsync(definition.Id)).State == ModuleState.Installed && progressEvents > 0 && manager.DownloadProgress == null,
                "Pinned bytes install atomically, report shared progress and produce a verified installed status");
            check(stages.SetEquals(Enum.GetValues<ModuleProgressStage>()), "Observer failures cannot interrupt download, verification and placement phase notifications");
            using (var lease = await manager.AcquireAsync(definition.Id))
            {
                check(File.ReadAllBytes(lease.ExecutablePath).SequenceEqual(bytes), "A module lease names exactly the verified executable");
                check((await manager.GetStatusAsync(definition.Id)).State == ModuleState.InUse, "An active module lease is visible to all UI consumers");
                await Reject(() => manager.RemoveAsync(definition.Id), "A module in use cannot be uninstalled");
                await Reject(() => manager.InstallAsync(definition.Id), "A module in use cannot be replaced by an installation");
                check(Locked(() => { using var writable = File.OpenWrite(lease.ExecutablePath); }), "The OS denies writes while a verified executable is leased");
                check(Locked(() => File.Delete(lease.ExecutablePath)), "The OS denies deletion while a verified executable is leased");
                check(Locked(() => File.Move(lease.ExecutablePath, lease.ExecutablePath + ".renamed")), "The OS denies executable rename across a verified lease");
                string versionFolder = Path.GetDirectoryName(lease.ExecutablePath)!;
                check(Locked(() => Directory.Move(versionFolder, versionFolder + ".renamed")), "The OS pins executable ancestors against rename during use");
                using var writableDirectory = CreateFile(versionFolder, 0x40000000, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
                    3, 0x02200000, IntPtr.Zero);
                check(writableDirectory.IsInvalid, "The OS denies directory write handles that could retarget a leased module path");
            }
            string installed = Path.Combine(fixture, "happy", definition.Id, definition.Version, definition.ExecutableName);
            var corrupted = bytes.ToArray();
            corrupted[0] ^= 0x01;
            File.WriteAllBytes(installed, corrupted);
            check((await manager.GetStatusAsync(definition.Id)).State == ModuleState.Corrupt, "Same-size tampering is detected by status hashing");
            await Reject(async () => { using var lease = await manager.AcquireAsync(definition.Id); }, "Tampered bytes never produce an executable lease");
            await manager.InstallAsync(definition.Id);
            check(File.ReadAllBytes(installed).SequenceEqual(bytes), "A verified reinstall atomically replaces a corrupt cache entry");
            await manager.RemoveAsync(definition.Id);
            check((await manager.GetStatusAsync(definition.Id)).State == ModuleState.Missing, "Removing an inactive module clears only the known executable");

            handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupted) });
            var wrongHash = Manager("wrong-hash");
            await Reject(() => wrongHash.InstallAsync(definition.Id), "A wrong SHA-256 never publishes a downloaded executable");
            check((await wrongHash.GetStatusAsync(definition.Id)).State == ModuleState.Missing && !Directory.EnumerateFiles(fixture, "*.partial", SearchOption.AllDirectories).Any(),
                "Rejected download leaves neither a final executable nor its temporary file");

            handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new NonSeekingStream([.. bytes, 1])) });
            var oversized = Manager("oversized");
            await Reject(() => oversized.InstallAsync(definition.Id), "Chunked downloads cannot exceed the pinned size without a Content-Length header");
            check((await oversized.GetStatusAsync(definition.Id)).State == ModuleState.Missing, "Oversized streamed bytes are never published");
            handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new NonSeekingStream(bytes[..^1])) });
            await Reject(() => Manager("truncated").InstallAsync(definition.Id), "Truncated streamed downloads fail the exact-size check");

            using var canceled = new CancellationTokenSource();
            var cancelManager = Manager("canceled");
            handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new NonSeekingStream(bytes, firstChunkOnly: true)) });
            cancelManager.Changed += (_, _) => { if (cancelManager.DownloadProgress?.BytesReceived > 0) canceled.Cancel(); };
            bool cancelObserved = false;
            try { await cancelManager.InstallAsync(definition.Id, token: canceled.Token); }
            catch (OperationCanceledException) { cancelObserved = true; }
            check(cancelObserved && cancelManager.DownloadProgress == null && (await cancelManager.GetStatusAsync(definition.Id)).State == ModuleState.Missing,
                "Cancellation closes executable staging and leaves the module uninstalled while retaining resumable cache bytes");
            check(!Directory.EnumerateFiles(fixture, "*.partial", SearchOption.AllDirectories).Any(), "Canceled and rejected installations leave no executable staging files");

            var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.Respond = async (_, token) =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(token);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            };
            var concurrent = Manager("concurrent");
            Task install = concurrent.InstallAsync(definition.Id);
            try
            {
                await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                check((await concurrent.GetStatusAsync(definition.Id)).State == ModuleState.Installing,
                    "Status remains responsive while a serialized download is pending");
                using var queuedCancellation = new CancellationTokenSource();
                Task queuedRemoval = concurrent.RemoveAsync(definition.Id, queuedCancellation.Token);
                queuedCancellation.Cancel();
                bool canceledWait = false;
                try { await queuedRemoval; } catch (OperationCanceledException) { canceledWait = true; }
                check(canceledWait && concurrent.DownloadProgress != null, "Canceling a queued operation cannot cancel or clear another installation");
            }
            finally { releaseRequest.TrySetResult(); await install; }
            check((await concurrent.GetStatusAsync(definition.Id)).State == ModuleState.Installed,
                "A concurrent canceled removal cannot erase the completed module");

            int requests = 0;
            handler.Respond = (_, _) =>
            {
                requests++;
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://attacker.invalid/module.exe");
                return Task.FromResult(redirect);
            };
            await Reject(() => Manager("redirect").InstallAsync(definition.Id), "Redirects outside the explicit GitHub asset allowlist fail closed");
            check(requests == 1, "An untrusted redirect target receives no HTTP request");
            foreach (string url in new[] { "http://github.com/asset", "https://github.com:8443/asset", "https://user@github.com/asset", "https://github.com.evil.invalid/asset", "https://github.com/asset#fragment" })
                await Reject(() => { ModuleManager.ValidateDownloadUri(new Uri(url)); return Task.CompletedTask; }, "Invalid module download origin is rejected: " + url);
            requests = 0;
            handler.Respond = (_, _) =>
            {
                requests++;
                if (requests == 1)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                    response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/fixture?signature=pinned-by-hash");
                    return Task.FromResult(response);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            };
            var redirected = Manager("allowed-redirect");
            await redirected.InstallAsync(definition.Id);
            check(requests == 2 && (await redirected.GetStatusAsync(definition.Id)).State == ModuleState.Installed,
                "GitHub release-assets redirects preserve hash and exact-size verification");

            foreach (var statusCode in new[] { HttpStatusCode.NotFound, HttpStatusCode.Gone })
            {
                handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(statusCode));
                HttpRequestException? failure = null;
                try { await Manager("missing-release-" + (int)statusCode).InstallAsync(definition.Id); }
                catch (HttpRequestException error) { failure = error; }
                check(failure is { } reported && reported.StatusCode == statusCode && ModuleErrorText.Describe(reported).StartsWith("Файл модуля пока недоступен на сервере выпуска", StringComparison.Ordinal),
                    "Missing release asset HTTP " + (int)statusCode + " is reported as publication availability, not an internet failure");
            }
            check(ModuleErrorText.Describe(new TaskCanceledException("private-url-token")).Contains("не ответил вовремя", StringComparison.Ordinal),
                "An installation deadline reports a timeout separately from explicit user cancellation");
            check(ModuleErrorText.Describe(new HttpRequestException(HttpRequestError.ConnectionError, "private-url-token")).Contains("соединиться", StringComparison.Ordinal),
                "Connection failures receive a connectivity-specific explanation");
            check(ModuleErrorText.Describe(new HttpRequestException("private-url-token", null, HttpStatusCode.ServiceUnavailable)).Contains("Сервер выпуска", StringComparison.Ordinal)
                && !ModuleErrorText.Describe(new Exception("private-url-token")).Contains("private-url-token", StringComparison.Ordinal),
                "Server failures stay distinct and raw exception URLs or credentials never reach module feedback");

            var unavailable = Manager("unpublished", definition with { DownloadUrl = "", Sha256 = "", SizeBytes = 0 });
            int requestsBefore = requests;
            check((await unavailable.GetStatusAsync(definition.Id)).State == ModuleState.Unavailable, "Unpublished catalog artifacts cannot masquerade as downloadable modules");
            await Reject(() => unavailable.InstallAsync(definition.Id), "An unpublished artifact cannot start installation");
            check(requests == requestsBefore, "Unpublished artifacts perform no network requests");
            await Reject(() => { _ = new ModuleCatalog([definition with { Id = "../escape" }]); return Task.CompletedTask; }, "Catalog identifiers cannot escape the module root");
            await Reject(() => { _ = new ModuleCatalog([definition with { DownloadUrl = "https://github.com/other/repo/releases/download/v1/module.exe" }]); return Task.CompletedTask; }, "Only the pinned FlowVPN release URL is accepted in the trusted catalog");
            await Reject(() => { _ = new ModuleCatalog([definition with { ProtocolVersion = 2 }]); return Task.CompletedTask; }, "Incompatible IPC metadata fails before filesystem or download work");
            check(manager.FindByCapability("outline.ss-prefix.tcp.v1")?.Id == definition.Id && manager.FindByCapability("unknown") == null,
                "Capabilities resolve through the catalog without product-name guesses");
        }
        finally
        {
            // Only the unique directory created for this run is eligible for cleanup.
            fixtureDirectory.Delete(recursive: true);
        }
    }

    sealed class TestHandler : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = null!;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Respond(request, token);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);
    sealed class NonSeekingStream(byte[] bytes, bool firstChunkOnly = false) : Stream
    {
        int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (firstChunkOnly && position > 0) await Task.Delay(Timeout.Infinite, token);
            int length = Math.Min(buffer.Length, bytes.Length - position);
            if (firstChunkOnly) length = Math.Min(length, 1);
            bytes.AsMemory(position, length).CopyTo(buffer);
            position += length;
            return length;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
