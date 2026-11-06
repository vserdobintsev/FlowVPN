using System.Net;
using System.Text;
namespace FlowVPN;

internal static class UpdateTransport
{
    public static HttpClient Client(IWebProxy? proxy = null) => BoundedHttp.Client(TimeSpan.FromMinutes(10), proxy);
    internal static async Task CopyBounded(Stream input, Stream output, long limit, IProgress<int>? progress, CancellationToken token)
    {
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int last = -1, count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > limit)
                throw new InvalidDataException("Ответ сервера превышает допустимый размер.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            int percent = (int)(total * 100 / limit);
            if (percent != last)
            {
                progress?.Report(percent);
                last = percent;
            }
        }
    }
    public static async Task<string> Feed(HttpClient client, string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await BoundedHttp.Open(client, UpdateProtocol.Https(url), timeout.Token);
        if (response.Content.Headers.ContentLength > 128 * 1024)
            throw new InvalidDataException("Описание выпуска слишком большое.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory = new MemoryStream();
        await CopyBounded(stream, memory, 128 * 1024, null, timeout.Token);
        return Encoding.UTF8.GetString(memory.ToArray());
    }
    public static async Task<string> Download(HttpClient client, UpdateRelease release, IProgress<int>? progress, CancellationToken token)
    {
        string folder = Path.Combine(Store.Data, "updates");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, release.Sha256.ToUpperInvariant() + ".msix"), partial = path + ".partial";
        if (File.Exists(path))
        {
            try
            {
                using var existing = File.OpenRead(path);
                UpdateProtocol.ValidatePackage(existing, release);
                return path;
            }
            catch (InvalidDataException) { File.Delete(path); } // Corrupted local cache must not prevent a retry.
        }
        try
        {
            using var response = await BoundedHttp.Open(client, UpdateProtocol.Https(release.PackageUrl), token);
            if (response.Content.Headers.ContentLength is long length && length != release.SizeBytes)
                throw new InvalidDataException("Размер загрузки не совпадает с выпуском.");
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await CopyBounded(source, target, release.SizeBytes, progress, token);
            using (var downloaded = File.OpenRead(partial))
                UpdateProtocol.ValidatePackage(downloaded, release);
            File.Move(partial, path, true);
            PrunePackages(folder, path);
            return path;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    static void PrunePackages(string folder, string current)
    {
        foreach (var file in new DirectoryInfo(folder).GetFiles("*.msix")
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name, @"\A[0-9A-Fa-f]{64}\.msix\z") && (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc).Skip(3))
            if (!file.FullName.Equals(current, StringComparison.OrdinalIgnoreCase))
                try
                {
                    file.Delete();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
    }
    public static void BackupSettings(string version)
    {
        string source = Path.Combine(Store.Data, "settings.dat");
        if (!File.Exists(source))
            return;
        string folder = Path.Combine(Store.Data, "updates", "settings-backups");
        Directory.CreateDirectory(folder);
        File.Copy(source, Path.Combine(folder, UpdateProtocol.Version4(version) + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".dat"), false);
        foreach (var file in new DirectoryInfo(folder).GetFiles("*.dat").OrderByDescending(f => f.LastWriteTimeUtc).Skip(3))
            file.Delete();
    }
}
