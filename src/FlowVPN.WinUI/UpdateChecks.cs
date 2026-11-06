using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace FlowVPN;

internal static class UpdateChecks
{
    sealed class SizedResponseHandler(int bodySize, int declaredSize) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bodySize]) };
            response.Content.Headers.ContentLength = declaredSize;
            return Task.FromResult(response);
        }
    }
    sealed class RedirectHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var result = new HttpResponseMessage(HttpStatusCode.Redirect);
            result.Headers.Location = new Uri("http://example.test/unsafe");
            return Task.FromResult(result);
        }
    }
    public static async Task Run()
    {
        var passed = new List<string>();
        void Check(bool test, string name)
        {
            if (!test)
                throw new Exception("Update test failed: " + name);
            passed.Add(name);
        }
        void Reject(Action action, string name)
        {
            try
            {
                action();
            }
            catch (InvalidDataException) { passed.Add(name); return; }
            throw new Exception("Invalid update accepted: " + name);
        }
        var identity = new UpdateIdentity("FlowVPN.Preview", "CN=FlowVPN Development", "x64", new(1, 2, 0, 0));
        var channel = new UpdateChannel { Id = "preview", PackageName = identity.Name, FeedUrl = "https://example.test/preview/latest.json" };
        UpdateRelease Release() => new()
        {
            SchemaVersion = 1,
            Channel = "preview",
            Version = "1.2.1.0",
            CoreVersion = "1.14.4",
            PackageName = identity.Name,
            Publisher = identity.Publisher,
            Architecture = "x64",
            MinimumWindowsBuild = 19041,
            SizeBytes = 100,
            Sha256 = new string('A', 64),
            PackageUrl = "https://example.test/releases/update.msix",
            AppInstallerUrl = "https://example.test/preview/FlowVPN.appinstaller",
            Notes = "Test release"
        };
        UpdateRelease Parse(UpdateRelease value) => UpdateProtocol.Parse(JsonSerializer.Serialize(value), channel, identity, 26100);
        Check(Parse(Release()).Version == "1.2.1.0", "Matching release accepted");
        foreach (string url in new[] { "http://example.test/x", "file:///C:/update.msix", "https://user:pass@example.test/x", "https://example.test/x#fragment" })
            Reject(() => UpdateProtocol.Https(url), "Unsafe URL rejected: " + url.Split(':')[0] + "/" + passed.Count);
        foreach (string version in new[] { "1.2.3", "1.2.3.65536", "-1.0.0.0", "latest" })
            Reject(() => UpdateProtocol.Version4(version), "Malformed version rejected: " + version);
        var bad = Release();
        bad.Channel = "stable";
        Reject(() => Parse(bad), "Wrong channel rejected");
        bad = Release();
        bad.Publisher = "CN=Someone Else";
        Reject(() => Parse(bad), "Wrong publisher rejected");
        bad = Release();
        bad.PackageName = "OtherApp";
        Reject(() => Parse(bad), "Wrong package rejected");
        bad = Release();
        bad.Architecture = "arm64";
        Reject(() => Parse(bad), "Wrong architecture rejected");
        bad = Release();
        bad.MinimumWindowsBuild = 99999;
        Reject(() => Parse(bad), "Newer OS requirement rejected");
        bad = Release();
        bad.SizeBytes = UpdateProtocol.MaxPackageBytes + 1;
        Reject(() => Parse(bad), "Oversized package rejected");
        bad = Release();
        bad.Sha256 = "missing";
        Reject(() => Parse(bad), "Missing checksum rejected");
        bad = Release();
        bad.PackageUrl = "https://example.test/installer.exe";
        Reject(() => Parse(bad), "Non-MSIX target rejected");
        var release = Release();
        Check(UpdateProtocol.CanStage(false, true, false, identity, release), "New version can stage in installed full app");
        Check(!UpdateProtocol.CanStage(true, true, false, identity, release), "Preview cannot install even with an available release");
        Check(!UpdateProtocol.CanStage(false, false, false, identity, release), "Portable build cannot install");
        Check(!UpdateProtocol.CanStage(false, true, true, identity, release), "Busy operation blocks staging");
        release.Version = "1.2.0.0";
        Check(!UpdateProtocol.CanStage(false, true, false, identity, release), "Same version not reinstalled");
        release.Version = "1.1.9.0";
        Check(!UpdateProtocol.CanStage(false, true, false, identity, release), "Downgrade blocked");
        byte[] Package(string name, bool signature = true)
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("AppxManifest.xml").Open()))
                    writer.Write($"<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"{name}\" Publisher=\"CN=FlowVPN Development\" Version=\"1.2.1.0\" ProcessorArchitecture=\"x64\"/></Package>");
                // This is only a parser fixture. It is never submitted to Windows deployment.
                if (signature)
                {
                    using var writer = new StreamWriter(zip.CreateEntry("AppxSignature.p7x").Open());
                    writer.Write("fixture");
                }
            }
            return memory.ToArray();
        }
        void Validate(byte[] bytes, UpdateRelease info)
        {
            using var memory = new MemoryStream(bytes);
            UpdateProtocol.ValidatePackage(memory, info);
        }
        UpdateRelease Describe(byte[] bytes)
        {
            var r = Release();
            r.SizeBytes = bytes.Length;
            r.Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            return r;
        }
        var package = Package(identity.Name);
        release = Describe(package);
        Validate(package, release);
        passed.Add("Package metadata matches feed; OS signature check remains separate");
        var changed = package.ToArray();
        changed[^1] ^= 1;
        Reject(() => Validate(changed, release), "Changed download rejected by SHA-256");
        Reject(() => Validate(package[..^1], release), "Truncated download rejected");
        var other = Package("OtherApp");
        Reject(() => Validate(other, Describe(other)), "Package identity rechecked inside archive");
        var unsigned = Package(identity.Name, false);
        Reject(() => Validate(unsigned, Describe(unsigned)), "Unsigned archive rejected before deployment");
        using (var source = new MemoryStream(new byte[11]))
        using (var target = new MemoryStream())
        {
            try
            {
                await UpdateTransport.CopyBounded(source, target, 10, null, CancellationToken.None);
                throw new Exception("Unbounded download accepted");
            }
            catch (InvalidDataException) { passed.Add("Streaming response limit enforced"); }
        }
        using (var source = new MemoryStream(new byte[5]))
        using (var target = new MemoryStream())
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try
            {
                await UpdateTransport.CopyBounded(source, target, 10, null, cancel.Token);
                throw new Exception("Cancelled transfer continued");
            }
            catch (OperationCanceledException) { passed.Add("Download cancellation respected"); }
        }
        using (var handler = new RedirectHandler())
        using (var client = new HttpClient(handler))
        {
            try
            {
                await UpdateTransport.Feed(client, "https://example.test/feed", CancellationToken.None);
                throw new Exception("HTTPS downgrade accepted");
            }
            catch (InvalidDataException) { Check(handler.Requests == 1, "Redirect to HTTP rejected before second request"); }
        }
        using (var client = new HttpClient(new SizedResponseHandler(10, 10)))
            Check((await BoundedHttp.Download(client, "https://example.test/rules", 10, CancellationToken.None)).Length == 10, "A valid bounded HTTPS response is preserved");
        using (var handler = new RedirectHandler())
        using (var client = new HttpClient(handler))
        {
            try
            {
                await BoundedHttp.Download(client, "https://example.test/outline-key", 1024, CancellationToken.None);
                throw new Exception("Dynamic key HTTPS downgrade accepted");
            }
            catch (InvalidDataException) { Check(handler.Requests == 1, "Dynamic key/repository HTTPS downgrade is rejected before requesting the insecure target"); }
        }
        foreach (var sample in new[] { (Body: 11, Declared: 11), (Body: 11, Declared: 10), (Body: 9, Declared: 10) })
        {
            using var client = new HttpClient(new SizedResponseHandler(sample.Body, sample.Declared));
            try
            {
                await BoundedHttp.Download(client, "https://example.test/rules", 10, CancellationToken.None);
                throw new Exception("Oversized or truncated repository response accepted");
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { passed.Add($"Repository response bounds enforced: body={sample.Body}, declared={sample.Declared}"); }
        }
        Store.Atomic(Path.Combine(Store.Data, "update-tests.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Passed = passed.Count,
            Tests = passed
        }, Store.JsonOptions));
    }
}
