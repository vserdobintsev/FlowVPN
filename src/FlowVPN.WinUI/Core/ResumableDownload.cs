using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace FlowVPN;

internal sealed class DownloadInterruptedException(string message, Exception inner) : IOException(message, inner);

// Only hash-pinned artifacts use this cache. Unverified bytes never become installed files.
internal static class ResumableDownload
{
    static readonly SemaphoreSlim gate = new(1, 1);
    const long CacheLimit = 256L * 1024 * 1024;
    public static async Task<byte[]> Download(HttpClient client, string url, string sha256, int maxBytes,
        string cacheDirectory, CancellationToken token, IProgress<TransferProgress>? progress = null, string item = "Файл",
        long? exactBytes = null, Action<Uri>? validateUri = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (maxBytes > 128 * 1024 * 1024 || exactBytes is <= 0 || exactBytes > maxBytes ||
            sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Некорректные ограничения загрузки.");
        BoundedHttp.Https(url);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var directory = ModuleFiles.Open(cacheDirectory, true);
            string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url + "\n" + sha256.ToUpperInvariant()))) + ".part";
            Prune(directory, cacheDirectory, name);
            using var handle = ModuleFiles.OpenChecked(Path.Combine(cacheDirectory, name), false, deleteAccess: true, resume: true);
            using var file = new FileStream(handle, FileAccess.ReadWrite, 65536);
            if (file.Length > (exactBytes ?? maxBytes)) file.SetLength(0);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                // A previous attempt may have received all bytes immediately before cancellation.
                if (file.Length > 0 && await Valid(file, sha256, exactBytes, deadline.Token).ConfigureAwait(false))
                    return await Finish(file, progress, item, deadline.Token).ConfigureAwait(false);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await Receive(client, url, file, maxBytes, exactBytes, validateUri, progress, item, deadline.Token).ConfigureAwait(false);
                        progress?.Report(new(item, TransferStage.Verifying, file.Length, file.Length));
                        if (!await Valid(file, sha256, exactBytes, deadline.Token).ConfigureAwait(false))
                        {
                            file.SetLength(0);
                            throw new InvalidDataException("Контрольная сумма загрузки не совпала. Повторите загрузку.");
                        }
                        return await Finish(file, progress, item, deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (!deadline.IsCancellationRequested && Retryable(error))
                    {
                        if (attempt >= 2) throw new DownloadInterruptedException("Не удалось загрузить файл за три попытки. Полученные данные сохранены. Если сервер поддерживает докачку, повторная попытка продолжит загрузку; иначе файл будет загружен заново.", error);
                        await Task.Delay(TimeSpan.FromSeconds(attempt + 1), deadline.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested)
            { throw new DownloadInterruptedException("Сервер загрузки долго не отвечает. Полученные данные сохранены; повторите попытку для продолжения.", error); }
            catch (InvalidDataException) { file.SetLength(0); throw; }
        }
        finally { gate.Release(); }
    }
    static bool Retryable(Exception error) => error is EndOfStreamException or OperationCanceledException ||
        error is HttpRequestException http && (http.StatusCode == null || http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int?)http.StatusCode >= 500) ||
        error is IOException;

    static async Task Receive(HttpClient client, string url, FileStream file, int maxBytes, long? exactBytes,
        Action<Uri>? validateUri, IProgress<TransferProgress>? progress, string item, CancellationToken token)
    {
        long offset = file.Length;
        var uri = BoundedHttp.Https(url);
        HttpResponseMessage? response = null;
        try
        {
            for (int redirect = 0; redirect < 6; redirect++)
            {
                BoundedHttp.Https(uri.AbsoluteUri);
                validateUri?.Invoke(uri);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    uri = new Uri(uri, location); response.Dispose(); response = null; continue;
                }
                break;
            }
            if (response == null) throw new HttpRequestException("Слишком много перенаправлений источника.");
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
            { file.SetLength(0); throw new EndOfStreamException("Сервер отклонил продолжение; загрузка будет начата заново."); }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentEncoding.Count > 0) throw new InvalidDataException("Источник изменил кодирование файла.");
            long? total;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.Unit != "bytes" || range.From != offset || range.To == null || range.Length == null || range.To >= range.Length ||
                    range.To < range.From || response.Content.Headers.ContentLength is long count && count != range.To - range.From + 1)
                    throw new InvalidDataException("Сервер вернул некорректный диапазон докачки.");
                total = range.Length;
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            { file.SetLength(0); offset = 0; total = response.Content.Headers.ContentLength; }
            else throw new InvalidDataException("Неожиданный ответ источника загрузки.");
            if (total > maxBytes || exactBytes != null && total != null && total != exactBytes)
                throw new InvalidDataException("Размер загрузки не совпадает с ожидаемым.");
            file.Position = offset;
            progress?.Report(new(item, TransferStage.Downloading, offset, total ?? exactBytes));
            using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] buffer = new byte[65536];
            long lastReport = 0;
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                int read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (file.Position + read > (total ?? exactBytes ?? maxBytes)) throw new InvalidDataException("Загрузка превышает допустимый размер.");
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                if (System.Diagnostics.Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds >= 100)
                { progress?.Report(new(item, TransferStage.Downloading, file.Position, total ?? exactBytes)); lastReport = System.Diagnostics.Stopwatch.GetTimestamp(); }
            }
            await file.FlushAsync(token).ConfigureAwait(false);
            if (file.Length != (total ?? exactBytes ?? file.Length)) throw new EndOfStreamException("Ответ источника оборвался.");
        }
        finally { response?.Dispose(); }
    }
    static async Task<bool> Valid(FileStream file, string sha256, long? exactBytes, CancellationToken token)
    {
        if (exactBytes != null && file.Length != exactBytes) return false;
        file.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
    static async Task<byte[]> Finish(FileStream file, IProgress<TransferProgress>? progress, string item, CancellationToken token)
    {
        file.Position = 0;
        byte[] bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        progress?.Report(new(item, TransferStage.Downloading, bytes.Length, bytes.Length));
        ModuleFiles.MarkDeleted(file.SafeFileHandle);
        return bytes;
    }
    static void Prune(ModuleFiles directory, string path, string keep)
    {
        long bytes = 0;
        int count = 0;
        foreach (var entry in new DirectoryInfo(path).EnumerateFiles("*.part").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            if (entry.Name == keep)
            {
                if (DateTime.UtcNow - entry.LastWriteTimeUtc > TimeSpan.FromDays(7)) directory.Delete(entry.Name);
                continue;
            }
            bytes += entry.Length;
            if (++count > 7 || bytes > CacheLimit - 128L * 1024 * 1024 || DateTime.UtcNow - entry.LastWriteTimeUtc > TimeSpan.FromDays(7))
                directory.Delete(entry.Name);
        }
    }
}
