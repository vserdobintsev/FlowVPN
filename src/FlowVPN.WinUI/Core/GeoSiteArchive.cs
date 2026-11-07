using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlowVPN;

public static class GeoSiteArchive
{
    public static string DataPath => Path.Combine(Store.Rules, "geosite.dat");
    static string MetaPath => Path.Combine(Store.Rules, "geosite-metadata.json");
    public static async Task Update(HttpClient client, bool force, CancellationToken cancellationToken = default, IProgress<TransferProgress>? progress = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        var token = deadline.Token;
        progress?.Report(new("Доменная база", TransferStage.Verifying));
        JsonObject? old = null;
        try
        {
            old = JsonNode.Parse(await File.ReadAllTextAsync(MetaPath, token))?.AsObject();
        }
        catch (IOException) { }
        catch (JsonException) { }
        bool validCache = old != null && await ValidCache(old, token);
        if (!force && validCache && DateTimeOffset.TryParse(old!["fetched"]?.ToString(), out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromHours(6))
            return;
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FlowVPN/1.4");
        var revisionJson = JsonNode.Parse(Encoding.UTF8.GetString(await BoundedHttp.Download(client, "https://api.github.com/repos/runetfreedom/russia-blocked-geosite/commits/release", 1024 * 1024, token, progress, "Версия доменной базы")));
        string revision = revisionJson?["sha"]?.ToString() ?? "";
        if (!Regex.IsMatch(revision, @"\A[0-9a-f]{40}\z"))
            throw new InvalidDataException("GitHub не вернул ревизию GeoSite.");
        if (old?["revision"]?.ToString() == revision && validCache)
        {
            old["fetched"] = DateTimeOffset.UtcNow.ToString("O");
            Store.WriteJson(MetaPath, old);
            return;
        }
        string url = $"https://raw.githubusercontent.com/runetfreedom/russia-blocked-geosite/{revision}/geosite.dat";
        string checksum = Encoding.UTF8.GetString(await BoundedHttp.Download(client, url + ".sha256sum", 4096, token, progress, "Контрольная сумма доменной базы")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!Regex.IsMatch(checksum, @"\A[a-fA-F0-9]{64}\z"))
            throw new InvalidDataException("Источник не вернул контрольную сумму GeoSite.");
        byte[] bytes;
        try
        {
            bytes = await ResumableDownload.Download(client, url, checksum, 128 * 1024 * 1024,
                Path.Combine(Store.Data, "downloads"), token, progress, "Доменная база");
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("Истекло время загрузки доменной базы. Полученные данные сохранены; повторите попытку.", error); }
        progress?.Report(new("Доменная база", TransferStage.Verifying));
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(checksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма GeoSite не совпала.");
        var catalog = await Task.Run(() => GeoSiteReader.Read(bytes, token: token), token);
        if (!catalog.Categories.Contains("ru-blocked"))
            throw new InvalidDataException("В каталоге GeoSite отсутствует базовая категория.");
        token.ThrowIfCancellationRequested();
        progress?.Report(new("Доменная база", TransferStage.Applying));
        Store.Atomic(DataPath, bytes);
        Store.Atomic(Path.Combine(Store.Rules, "domain-categories.json"), JsonSerializer.SerializeToUtf8Bytes(catalog.Categories, Store.JsonOptions));
        Store.WriteJson(MetaPath, new JsonObject { ["revision"] = revision, ["url"] = url, ["sha256"] = hash, ["fetched"] = DateTimeOffset.UtcNow.ToString("O"), ["categories"] = catalog.Categories.Count });
    }
    static async Task<bool> ValidCache(JsonObject metadata, CancellationToken token)
    {
        try
        {
            await using var file = new FileStream(DataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (file.Length > 128 * 1024 * 1024)
                return false;
            return Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(metadata["sha256"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }
}
