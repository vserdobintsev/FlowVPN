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
    public static async Task Update(HttpClient client, bool force)
    {
        JsonObject? old = null;
        try
        {
            old = JsonNode.Parse(await File.ReadAllTextAsync(MetaPath))?.AsObject();
        }
        catch (IOException) { }
        catch (JsonException) { }
        bool validCache = old != null && await ValidCache(old);
        if (!force && validCache && DateTimeOffset.TryParse(old!["fetched"]?.ToString(), out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromHours(6))
            return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FlowVPN/1.4");
        var revisionJson = JsonNode.Parse(Encoding.UTF8.GetString(await BoundedHttp.Download(client, "https://api.github.com/repos/runetfreedom/russia-blocked-geosite/commits/release", 1024 * 1024, token)));
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
        string checksum = Encoding.UTF8.GetString(await BoundedHttp.Download(client, url + ".sha256sum", 4096, token)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!Regex.IsMatch(checksum, @"\A[a-fA-F0-9]{64}\z"))
            throw new InvalidDataException("Источник не вернул контрольную сумму GeoSite.");
        byte[] bytes = await BoundedHttp.Download(client, url, 128 * 1024 * 1024, token);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(checksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма GeoSite не совпала.");
        var catalog = await Task.Run(() => GeoSiteReader.Read(bytes), token);
        if (!catalog.Categories.Contains("ru-blocked"))
            throw new InvalidDataException("В каталоге GeoSite отсутствует базовая категория.");
        Store.Atomic(DataPath, bytes);
        Store.Atomic(Path.Combine(Store.Rules, "domain-categories.json"), JsonSerializer.SerializeToUtf8Bytes(catalog.Categories, Store.JsonOptions));
        Store.WriteJson(MetaPath, new JsonObject { ["revision"] = revision, ["url"] = url, ["sha256"] = hash, ["fetched"] = DateTimeOffset.UtcNow.ToString("O"), ["categories"] = catalog.Categories.Count });
    }
    static async Task<bool> ValidCache(JsonObject metadata)
    {
        try
        {
            await using var file = new FileStream(DataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (file.Length > 128 * 1024 * 1024)
                return false;
            return Convert.ToHexString(await SHA256.HashDataAsync(file)).Equals(metadata["sha256"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }
}
